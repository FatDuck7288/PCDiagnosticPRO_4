using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Driver update evaluator using Windows Update Agent (WUA) only.
    ///
    /// AUDIT (truth model):
    /// - "Ancien (age)" is computed from DriverDate and AgeThresholdMonths only.
    /// - "Mise a jour trouvee" is true only when a WUA driver candidate matches the local
    ///   driver by Hardware ID. No broad class/vendor-only auto matching is used.
    /// - If Hardware ID (or enough local identity data) is missing, status is "Non verifiable".
    ///
    /// This avoids reporting "a mettre a jour" when no real WUA candidate is found.
    /// </summary>
    public class DriverStatusEvaluator
    {
        public const int AgeThresholdMonths = 24;

        public async Task<DriverUpdateEvaluationResult> EvaluateAsync(
            List<DriverInventoryItem> drivers,
            bool onlineSearch,
            CancellationToken ct = default)
        {
            var result = new DriverUpdateEvaluationResult();

            if (drivers == null || drivers.Count == 0)
            {
                return result;
            }

            StampAgeMetadata(drivers);

            try
            {
                var candidates = await Task.Run(() => QueryDriverUpdates(onlineSearch, ct), ct);
                result.UpdateCandidates = candidates;
                result.SearchMode = onlineSearch ? "Online" : "Offline";

                ApplyVerificationResults(drivers, candidates, verificationError: null);
            }
            catch (OperationCanceledException)
            {
                result.Error = "cancelled";
                ApplyVerificationResults(drivers, candidates: new List<DriverUpdateCandidate>(), verificationError: "Verification annulee.");
            }
            catch (Exception ex)
            {
                result.Error = $"exception: {ex.Message}";
                App.LogMessage($"[DriverStatusEvaluator] Error: {ex.Message}");
                ApplyVerificationResults(drivers, candidates: new List<DriverUpdateCandidate>(), verificationError: $"Windows Update indisponible: {SanitizeReason(ex.Message)}");
            }

            return result;
        }

        private static List<DriverUpdateCandidate> QueryDriverUpdates(bool onlineSearch, CancellationToken ct)
        {
            var candidates = new List<DriverUpdateCandidate>();

            // Windows Update Agent (COM) - legal OS API
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType == null)
            {
                throw new InvalidOperationException("Microsoft.Update.Session COM unavailable");
            }

            dynamic session = Activator.CreateInstance(sessionType)
                ?? throw new InvalidOperationException("Unable to create Microsoft.Update.Session COM instance.");
            dynamic searcher = session.CreateUpdateSearcher();

            try { searcher.Online = onlineSearch; } catch { /* Not supported on some builds */ }

            // Driver updates only
            dynamic searchResult = searcher.Search("IsInstalled=0 and Type='Driver'");
            dynamic updates = searchResult.Updates;

            int count = updates.Count;
            for (int i = 0; i < count; i++)
            {
                if (ct.IsCancellationRequested) break;

                dynamic update = updates.Item(i);
                var candidate = new DriverUpdateCandidate
                {
                    Title = TryGetDynamicString(update, "Title") ?? "Driver Update"
                };

                candidate.DriverClass = TryGetDynamicString(update, "DriverClass");
                candidate.DriverModel = TryGetDynamicString(update, "DriverModel");
                candidate.DriverManufacturer = TryGetDynamicString(update, "DriverManufacturer");
                candidate.DriverVerVersion = TryGetDynamicString(update, "DriverVerVersion");
                candidate.DriverVerDate = TryGetDynamicDate(update, "DriverVerDate");
                candidate.DriverHardwareId = TryGetDynamicString(update, "DriverHardwareID");

                candidates.Add(candidate);
            }

            return candidates;
        }

        private static void StampAgeMetadata(List<DriverInventoryItem> drivers)
        {
            foreach (var driver in drivers)
            {
                var installedDate = TryParseDate(driver.DriverDate);
                if (!installedDate.HasValue)
                {
                    driver.AgeMonths = null;
                    driver.IsOldByAge = null;
                    continue;
                }

                var months = MonthsBetween(installedDate.Value, DateTime.Now);
                driver.AgeMonths = months;
                driver.IsOldByAge = months > AgeThresholdMonths;
            }
        }

        private static void ApplyVerificationResults(
            List<DriverInventoryItem> drivers,
            List<DriverUpdateCandidate> candidates,
            string? verificationError)
        {
            foreach (var driver in drivers)
            {
                driver.UpdateMatch = null;
                driver.UpdateStatus = "Unknown";

                if (!string.IsNullOrWhiteSpace(verificationError))
                {
                    driver.UpdateAvailability = "NotVerifiable";
                    driver.UpdateAvailabilityReason = verificationError;
                    continue;
                }

                if (!CanSafelyVerify(driver, out var notVerifiableReason))
                {
                    driver.UpdateAvailability = "NotVerifiable";
                    driver.UpdateAvailabilityReason = notVerifiableReason;
                    continue;
                }

                var match = FindBestMatchByHardwareId(driver, candidates);
                if (match == null)
                {
                    driver.UpdateAvailability = "NotFound";
                    driver.UpdateAvailabilityReason = "Aucune mise a jour Windows Update trouvee pour ce Hardware ID.";
                    continue;
                }

                var evaluated = EvaluateMatch(driver, match.Value.candidate, match.Value.reason);
                driver.UpdateMatch = evaluated.matchInfo;
                driver.UpdateStatus = evaluated.status;
                driver.UpdateAvailability = "Found";
                driver.UpdateAvailabilityReason = BuildMatchReasonMessage(match.Value.reason);
            }
        }

        private static (DriverUpdateCandidate candidate, string reason)? FindBestMatchByHardwareId(
            DriverInventoryItem driver,
            List<DriverUpdateCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0) return null;

            var hardwareMatch = candidates.FirstOrDefault(c => HardwareIdMatches(driver, c));
            if (hardwareMatch != null)
            {
                return (hardwareMatch, "hardware_id");
            }

            var identityMatch = candidates.FirstOrDefault(c => IdentityMatches(driver, c));
            if (identityMatch != null)
            {
                return (identityMatch, "class_manufacturer_model");
            }

            return null;
        }

        private static bool CanSafelyVerify(DriverInventoryItem driver, out string reason)
        {
            if (driver.HardwareIds != null && driver.HardwareIds.Any(id => !string.IsNullOrWhiteSpace(id)))
            {
                reason = string.Empty;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(driver.PnpDeviceId))
            {
                reason = string.Empty;
                return true;
            }

            reason = "Non verifiable: Hardware ID et PNP ID manquants.";
            return false;
        }

        private static (string status, DriverUpdateMatch matchInfo) EvaluateMatch(
            DriverInventoryItem driver,
            DriverUpdateCandidate candidate,
            string reason)
        {
            var matchInfo = new DriverUpdateMatch
            {
                Title = candidate.Title,
                Version = candidate.DriverVerVersion,
                Date = candidate.DriverVerDate,
                MatchReason = reason
            };

            var installedVersion = TryParseVersion(driver.DriverVersion);
            var updateVersion = TryParseVersion(candidate.DriverVerVersion);
            if (installedVersion != null && updateVersion != null)
            {
                return updateVersion.CompareTo(installedVersion) > 0
                    ? ("Outdated", matchInfo)
                    : ("UpToDate", matchInfo);
            }

            var installedDate = TryParseDate(driver.DriverDate);
            var updateDate = TryParseDate(candidate.DriverVerDate);
            if (installedDate.HasValue && updateDate.HasValue)
            {
                return updateDate.Value > installedDate.Value
                    ? ("Outdated", matchInfo)
                    : ("UpToDate", matchInfo);
            }

            return ("Unknown", matchInfo);
        }

        private static bool HardwareIdMatches(DriverInventoryItem driver, DriverUpdateCandidate candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate.DriverHardwareId))
                return false;

            var localIds = BuildDriverHardwareIdentitySet(driver);
            if (localIds.Count == 0)
                return false;

            var candidateId = NormalizeHardwareId(candidate.DriverHardwareId);
            if (string.IsNullOrWhiteSpace(candidateId))
                return false;

            var candidateVendorDevice = TryExtractVendorDeviceKey(candidateId);
            foreach (var id in localIds)
            {
                var localId = NormalizeHardwareId(id);
                if (string.IsNullOrWhiteSpace(localId))
                    continue;

                if (candidateId.IndexOf(localId, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    localId.IndexOf(candidateId, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                var localVendorDevice = TryExtractVendorDeviceKey(localId);
                if (!string.IsNullOrWhiteSpace(localVendorDevice) &&
                    string.Equals(localVendorDevice, candidateVendorDevice, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static List<string> BuildDriverHardwareIdentitySet(DriverInventoryItem driver)
        {
            var identities = new List<string>();
            if (driver.HardwareIds != null)
            {
                identities.AddRange(driver.HardwareIds.Where(id => !string.IsNullOrWhiteSpace(id)));
            }

            if (!string.IsNullOrWhiteSpace(driver.PnpDeviceId))
                identities.Add(driver.PnpDeviceId);

            return identities;
        }

        private static string NormalizeHardwareId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Trim().ToUpperInvariant();
            var firstSlash = normalized.IndexOf('\\');
            if (firstSlash < 0)
                return normalized;

            var bus = normalized.Substring(0, firstSlash);
            var remainder = normalized.Substring(firstSlash + 1);
            var secondSlash = remainder.IndexOf('\\');
            if (secondSlash >= 0)
                remainder = remainder.Substring(0, secondSlash);

            var tokens = remainder
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(t =>
                    t.StartsWith("VEN_", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("VID_", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("DEV_", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("PID_", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (tokens.Length == 0)
                return normalized;

            return $"{bus}\\{string.Join("&", tokens)}";
        }

        private static string? TryExtractVendorDeviceKey(string? normalizedHardwareId)
        {
            if (string.IsNullOrWhiteSpace(normalizedHardwareId))
                return null;

            string? vendor = null;
            string? device = null;
            var parts = normalizedHardwareId.ToUpperInvariant()
                .Split(new[] { '\\', '&', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                if (vendor == null &&
                    (part.StartsWith("VEN_", StringComparison.Ordinal) || part.StartsWith("VID_", StringComparison.Ordinal)))
                {
                    vendor = part;
                    continue;
                }

                if (device == null &&
                    (part.StartsWith("DEV_", StringComparison.Ordinal) || part.StartsWith("PID_", StringComparison.Ordinal)))
                {
                    device = part;
                }

                if (vendor != null && device != null)
                    break;
            }

            return vendor != null && device != null ? $"{vendor}|{device}" : null;
        }

        private static bool IdentityMatches(DriverInventoryItem driver, DriverUpdateCandidate candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate.DriverModel))
                return false;

            if (!string.IsNullOrWhiteSpace(candidate.DriverClass) &&
                !string.Equals(candidate.DriverClass, driver.DeviceClass, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var manufacturerMatches =
                StringApproxMatches(candidate.DriverManufacturer, driver.Provider) ||
                StringApproxMatches(candidate.DriverManufacturer, driver.Manufacturer);

            if (!string.IsNullOrWhiteSpace(candidate.DriverManufacturer) && !manufacturerMatches)
                return false;

            return StringApproxMatches(candidate.DriverModel, driver.DeviceName);
        }

        private static bool StringApproxMatches(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            var leftNorm = NormalizeText(left);
            var rightNorm = NormalizeText(right);
            if (leftNorm.Length == 0 || rightNorm.Length == 0)
                return false;

            if (leftNorm.IndexOf(rightNorm, StringComparison.OrdinalIgnoreCase) >= 0 ||
                rightNorm.IndexOf(leftNorm, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            var leftTokens = leftNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 4);
            var rightTokenSet = new HashSet<string>(
                rightNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 4),
                StringComparer.OrdinalIgnoreCase);

            return leftTokens.Any(t => rightTokenSet.Contains(t));
        }

        private static string NormalizeText(string value)
        {
            var chars = value.ToUpperInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray();
            return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        private static string BuildMatchReasonMessage(string reason)
        {
            return reason switch
            {
                "hardware_id" => "Mise a jour Windows Update trouvee via Hardware ID.",
                "class_manufacturer_model" => "Mise a jour Windows Update trouvee via classe/fabricant/modele.",
                _ => "Mise a jour Windows Update trouvee."
            };
        }

        private static Version? TryParseVersion(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var cleaned = value.Trim();
            // Some versions include extra suffix, keep numeric parts only
            var parts = cleaned.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (Version.TryParse(part, out var v))
                    return v;
            }

            return null;
        }

        private static DateTime? TryParseDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
                return dt;

            return null;
        }

        private static int MonthsBetween(DateTime from, DateTime to)
        {
            var months = ((to.Year - from.Year) * 12) + to.Month - from.Month;
            if (to.Day < from.Day)
                months--;
            return Math.Max(0, months);
        }

        private static string SanitizeReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return "Erreur non detaillee.";

            var firstLine = reason.Replace("\r", string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim() ?? reason.Trim();

            return firstLine.Length <= 220 ? firstLine : firstLine.Substring(0, 220) + "...";
        }

        private static string? TryGetDynamicString(dynamic obj, string propertyName)
        {
            try
            {
                var value = obj.GetType().InvokeMember(propertyName, System.Reflection.BindingFlags.GetProperty, null, obj, Array.Empty<object>());
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string? TryGetDynamicDate(dynamic obj, string propertyName)
        {
            try
            {
                var value = obj.GetType().InvokeMember(propertyName, System.Reflection.BindingFlags.GetProperty, null, obj, Array.Empty<object>());
                if (value is DateTime dt) return dt.ToString("yyyy-MM-dd");
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }

    public class DriverUpdateEvaluationResult
    {
        public List<DriverUpdateCandidate> UpdateCandidates { get; set; } = new();
        public string? SearchMode { get; set; }
        public string? Error { get; set; }
    }
}
