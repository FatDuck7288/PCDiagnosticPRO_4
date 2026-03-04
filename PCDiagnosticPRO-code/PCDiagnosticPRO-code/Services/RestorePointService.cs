using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.Services
{
    public sealed class RestorePointService
    {
        public RestorePointDialogData ReadFromCombinedRoot(JsonElement root)
        {
            var data = new RestorePointDialogData();

            if (!TryGetNestedElement(root, out var restoreData, "scan_powershell", "sections", "RestorePoints", "data") &&
                !TryGetNestedElement(root, out restoreData, "sections", "RestorePoints", "data") &&
                !TryGetNestedElement(root, out restoreData, "scan_powershell", "sections", "RestorePoints") &&
                !TryGetNestedElement(root, out restoreData, "sections", "RestorePoints"))
            {
                data.Count = null;
                data.Status = "Unavailable";
                data.Reason = "NoData";
                data.Confidence = "None";
                return data;
            }

            var count = GetInt(restoreData, "count") ?? GetInt(restoreData, "restorePointCount");
            var status = GetString(restoreData, "status") ?? GetString(restoreData, "restorePointStatus") ?? string.Empty;
            var reason = GetString(restoreData, "reason") ?? GetString(restoreData, "unavailableReason") ?? string.Empty;
            var confidence = GetString(restoreData, "confidence") ?? string.Empty;

            var points = new List<RestorePointEntry>();
            if (TryGetProperty(restoreData, "points", out var pointsElement) && pointsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var point in pointsElement.EnumerateArray())
                    points.Add(ParsePoint(point));
            }
            else if (TryGetProperty(restoreData, "restorePoints", out var restorePointsElement) && restorePointsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var point in restorePointsElement.EnumerateArray())
                    points.Add(ParsePoint(point));
            }

            var normalizedStatus = status.Trim();
            var unavailable = normalizedStatus.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
                              normalizedStatus.Contains("indisponible", StringComparison.OrdinalIgnoreCase);

            if (count.HasValue && count.Value < 0)
                count = null;

            if (!count.HasValue && points.Count > 0)
                count = points.Count;

            if (unavailable || (!string.IsNullOrWhiteSpace(reason) && !count.HasValue))
            {
                data.Count = null;
                data.Status = "Unavailable";
                data.Reason = string.IsNullOrWhiteSpace(reason) ? "Error" : reason;
                data.Confidence = string.IsNullOrWhiteSpace(confidence) ? "None" : confidence;
            }
            else
            {
                data.Count = Math.Max(0, count ?? 0);
                data.Status = "Available";
                data.Reason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason;
                data.Confidence = string.IsNullOrWhiteSpace(confidence) ? "High" : confidence;
            }

            data.Points = points
                .OrderByDescending(p => p.CreationTime ?? DateTimeOffset.MinValue)
                .ToList();

            return data;
        }

        public async Task<RestorePointCreateResult> CreateRestorePointAsync(string description, CancellationToken cancellationToken = default)
        {
            if (!AdminHelper.IsRunningAsAdmin())
            {
                return new RestorePointCreateResult
                {
                    Success = false,
                    RequiresElevation = true,
                    Message = "Cette action nécessite des droits administrateur."
                };
            }

            var safeDescription = string.IsNullOrWhiteSpace(description)
                ? $"PCDiagnostic_{DateTime.Now:yyyyMMdd_HHmm}"
                : description.Trim();
            safeDescription = safeDescription.Replace("\"", string.Empty).Replace("'", string.Empty);

            // Primary: WMI SystemRestore (same mechanism as "Propriétés système → Créer").
            // This is the native Windows COM/WMI method and matches what Windows shows in the list.
            var wmiResult = await TryCreateViaWmiAsync(safeDescription, cancellationToken).ConfigureAwait(false);
            if (wmiResult.Success || wmiResult.RequiresElevation)
                return wmiResult;

            // Fallback: PowerShell Checkpoint-Computer (calls the same Win32 API internally).
            App.LogMessage($"[RestorePoint] WMI failed ({wmiResult.Message}), trying Checkpoint-Computer fallback.");
            return await TryCreateViaPowerShellAsync(safeDescription, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<RestorePointCreateResult> TryCreateViaWmiAsync(string safeDescription, CancellationToken cancellationToken)
        {
            // Defense-in-depth: strip quotes again even if caller already did so (prevents PS injection).
            safeDescription = (safeDescription ?? string.Empty)
                .Replace("'", string.Empty)
                .Replace("\"", string.Empty);

            // Uses Win32_SystemRestore WMI class – identical to what "Propriétés système" calls.
            var script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;$OutputEncoding=[System.Text.Encoding]::UTF8; " +
                         "$ErrorActionPreference='Stop'; " +
                         "$wmi = [wmiclass]'\\\\localhost\\root\\default:SystemRestore'; " +
                         $"$ret = $wmi.CreateRestorePoint('{safeDescription}', 12, 100); " +
                         "if ($ret.ReturnValue -eq 0) { Write-Output 'WMI_RESTORE_POINT_CREATED' } " +
                         "else { throw \"CreateRestorePoint WMI returned $($ret.ReturnValue)\" }";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            try
            {
                using var process = Process.Start(psi);
                if (process == null)
                    return new RestorePointCreateResult { Success = false, Message = "Impossible de démarrer PowerShell (WMI)." };

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                var output = (await outputTask.ConfigureAwait(false)).Trim();
                var error = (await errorTask.ConfigureAwait(false)).Trim();

                if (process.ExitCode == 0 && output.Contains("WMI_RESTORE_POINT_CREATED", StringComparison.OrdinalIgnoreCase))
                    return new RestorePointCreateResult { Success = true, Message = "Point de restauration créé avec succès (méthode native Windows)." };

                var reason = !string.IsNullOrWhiteSpace(error) ? error : output;
                return new RestorePointCreateResult { Success = false, Message = SanitizeReason(reason.Length > 0 ? reason : "Erreur WMI inconnue.") };
            }
            catch (OperationCanceledException)
            {
                return new RestorePointCreateResult { Success = false, Message = "Création annulée." };
            }
            catch (Exception ex)
            {
                return new RestorePointCreateResult { Success = false, Message = SanitizeReason(ex.Message) };
            }
        }

        private static async Task<RestorePointCreateResult> TryCreateViaPowerShellAsync(string description, CancellationToken cancellationToken)
        {
            var systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
            if (string.IsNullOrWhiteSpace(systemDrive))
                systemDrive = "C:";
            var escapedDrive = systemDrive.Replace("'", string.Empty);

            var script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;$OutputEncoding=[System.Text.Encoding]::UTF8; " +
                         "$ErrorActionPreference='Stop'; " +
                         $"$drive='{escapedDrive}\\\\'; " +
                         "Enable-ComputerRestore -Drive $drive -ErrorAction SilentlyContinue | Out-Null; " +
                         $"Checkpoint-Computer -Description '{description}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop; " +
                         "Write-Output 'RESTORE_POINT_CREATED'";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            try
            {
                using var process = Process.Start(psi);
                if (process == null)
                    return new RestorePointCreateResult { Success = false, Message = "Impossible de démarrer PowerShell." };

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                var output = (await outputTask.ConfigureAwait(false)).Trim();
                var error = (await errorTask.ConfigureAwait(false)).Trim();

                if (process.ExitCode == 0 && output.Contains("RESTORE_POINT_CREATED", StringComparison.OrdinalIgnoreCase))
                    return new RestorePointCreateResult { Success = true, Message = "Point de restauration créé avec succès." };

                var reason = !string.IsNullOrWhiteSpace(error) ? error : output;
                if (string.IsNullOrWhiteSpace(reason))
                    reason = "Erreur inconnue lors de la création du point.";

                return new RestorePointCreateResult { Success = false, Message = SanitizeReason(reason) };
            }
            catch (OperationCanceledException)
            {
                return new RestorePointCreateResult { Success = false, Message = "Création du point annulée." };
            }
            catch (Exception ex)
            {
                return new RestorePointCreateResult { Success = false, Message = SanitizeReason(ex.Message) };
            }
        }

        public async Task<RestorePointDialogData> ReadCurrentRestorePointsAsync(CancellationToken cancellationToken = default)
        {
            var script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;$OutputEncoding=[System.Text.Encoding]::UTF8; " +
                         "$ErrorActionPreference='Stop'; " +
                         "$points = @(Get-ComputerRestorePoint -ErrorAction Stop | Sort-Object -Property CreationTime -Descending); " +
                         "$rows = $points | ForEach-Object { [pscustomobject]@{ sequenceNumber = $_.SequenceNumber; description = $_.Description; creationTime = $_.CreationTime } }; " +
                         "if ($rows.Count -eq 0) { '[]' } else { $rows | ConvertTo-Json -Compress }";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            try
            {
                using var process = Process.Start(psi);
                if (process == null)
                {
                    return new RestorePointDialogData
                    {
                        Count = null,
                        Status = "Unavailable",
                        Reason = "PowerShellUnavailable",
                        Confidence = "None"
                    };
                }

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                var output = (await outputTask.ConfigureAwait(false)).Trim();
                var error = (await errorTask.ConfigureAwait(false)).Trim();
                if (process.ExitCode != 0)
                {
                    var reason = string.IsNullOrWhiteSpace(error) ? output : error;
                    return new RestorePointDialogData
                    {
                        Count = null,
                        Status = "Unavailable",
                        Reason = SanitizeReason(reason),
                        Confidence = "None"
                    };
                }

                if (string.IsNullOrWhiteSpace(output))
                    output = "[]";

                var points = ParsePointsJson(output)
                    .OrderByDescending(p => p.CreationTime ?? DateTimeOffset.MinValue)
                    .ToList();

                return new RestorePointDialogData
                {
                    Count = points.Count,
                    Status = "Available",
                    Reason = string.Empty,
                    Confidence = "High",
                    Points = points
                };
            }
            catch (OperationCanceledException)
            {
                return new RestorePointDialogData
                {
                    Count = null,
                    Status = "Unavailable",
                    Reason = "Cancelled",
                    Confidence = "None"
                };
            }
            catch (Exception ex)
            {
                return new RestorePointDialogData
                {
                    Count = null,
                    Status = "Unavailable",
                    Reason = SanitizeReason(ex.Message),
                    Confidence = "None"
                };
            }
        }

        public bool OpenSystemProtectionSettings(out string message)
        {
            message = string.Empty;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "SystemPropertiesProtection.exe",
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[RestorePointService] SystemPropertiesProtection.exe failed: {ex.Message}");
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "rstrui.exe",
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[RestorePointService] rstrui.exe failed: {ex.Message}");
                message = SanitizeReason(ex.Message);
                return false;
            }
        }

        private static RestorePointEntry ParsePoint(JsonElement point)
        {
            var entry = new RestorePointEntry
            {
                Description = GetString(point, "description") ?? GetString(point, "Description") ?? "Point sans description",
                SequenceNumber = GetInt(point, "sequenceNumber") ?? GetInt(point, "SequenceNumber")
            };

            var dateRaw = GetString(point, "creationTime") ?? GetString(point, "CreationTime") ?? GetString(point, "date");
            if (!string.IsNullOrWhiteSpace(dateRaw) && TryParseRestorePointDate(dateRaw, out var parsed))
                entry.CreationTime = parsed;

            return entry;
        }

        private static List<RestorePointEntry> ParsePointsJson(string payload)
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var points = new List<RestorePointEntry>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in root.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                        continue;

                    points.Add(ParsePoint(element));
                }

                return points;
            }

            if (root.ValueKind == JsonValueKind.Object)
                points.Add(ParsePoint(root));

            return points;
        }

        private static bool TryParseRestorePointDate(string rawDate, out DateTimeOffset parsed)
        {
            if (DateTimeOffset.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
                return true;

            var compact = rawDate.Trim();
            if (compact.Length >= 14 &&
                DateTime.TryParseExact(
                    compact.Substring(0, 14),
                    "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var dt))
            {
                parsed = new DateTimeOffset(dt);
                return true;
            }

            parsed = default;
            return false;
        }

        private static string SanitizeReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return "Erreur non détaillée.";

            var firstLine = reason.Replace("\r", string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim() ?? reason.Trim();

            if (firstLine.Length <= 280)
                return firstLine;

            return firstLine.Substring(0, 280) + "...";
        }

        private static bool TryGetNestedElement(JsonElement element, out JsonElement value, params string[] path)
        {
            value = element;
            foreach (var segment in path)
            {
                if (value.ValueKind != JsonValueKind.Object)
                    return false;

                if (value.TryGetProperty(segment, out var direct))
                {
                    value = direct;
                    continue;
                }

                var matched = false;
                foreach (var prop in value.EnumerateObject())
                {
                    if (!string.Equals(prop.Name, segment, StringComparison.OrdinalIgnoreCase))
                        continue;

                    value = prop.Value;
                    matched = true;
                    break;
                }

                if (!matched)
                    return false;
            }

            return true;
        }

        private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
        {
            value = default;
            if (element.ValueKind != JsonValueKind.Object)
                return false;

            if (element.TryGetProperty(propertyName, out value))
                return true;

            foreach (var property in element.EnumerateObject())
            {
                if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    continue;

                value = property.Value;
                return true;
            }

            return false;
        }

        private static string? GetString(JsonElement element, string propertyName)
        {
            return TryGetProperty(element, propertyName, out var value)
                ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
                : null;
        }

        private static int? GetInt(JsonElement element, string propertyName)
        {
            if (!TryGetProperty(element, propertyName, out var value))
                return null;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return number;

            return null;
        }
    }

    public sealed class RestorePointDialogData
    {
        public int? Count { get; set; }
        public string Status { get; set; } = "Unavailable";
        public string Reason { get; set; } = "NoData";
        public string Confidence { get; set; } = "None";
        public List<RestorePointEntry> Points { get; set; } = new();

        public string CountDisplay => Count.HasValue ? Count.Value.ToString(CultureInfo.InvariantCulture) : "Indisponible";

        public string StatusDisplay
        {
            get
            {
                if (Count.HasValue)
                    return Count.Value == 0 ? "Aucun point de restauration" : "Points de restauration disponibles";

                return string.IsNullOrWhiteSpace(Reason)
                    ? "Indisponible"
                    : $"Indisponible ({Reason})";
            }
        }
    }

    public sealed class RestorePointEntry
    {
        public int? SequenceNumber { get; set; }
        public string Description { get; set; } = string.Empty;
        public DateTimeOffset? CreationTime { get; set; }

        public string DisplayDate => CreationTime.HasValue
            ? CreationTime.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "Date inconnue";

        public string DisplaySequence => SequenceNumber.HasValue
            ? SequenceNumber.Value.ToString(CultureInfo.InvariantCulture)
            : "-";
    }

    public sealed class RestorePointCreateResult
    {
        public bool Success { get; set; }
        public bool RequiresElevation { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
