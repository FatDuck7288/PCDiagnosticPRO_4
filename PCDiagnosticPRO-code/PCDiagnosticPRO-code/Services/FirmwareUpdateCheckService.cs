using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Vérifie si une mise à jour BIOS/firmware UEFI est disponible via Windows Update.
    /// Utilise Microsoft.Update.Session (COM) via PowerShell — aucun binaire externe.
    /// </summary>
    public static class FirmwareUpdateCheckService
    {
        public sealed class FirmwareCheckResult
        {
            public bool Success { get; set; }
            public bool UpdateAvailable { get; set; }
            public string Source { get; set; } = "Windows Update";
            public string Details { get; set; } = string.Empty;
            public string ErrorMessage { get; set; } = string.Empty;
        }

        /// <summary>
        /// Lance une recherche de mises à jour firmware via Windows Update (COM).
        /// Retourne un résultat structuré — ne modifie pas le système.
        /// </summary>
        public static async Task<FirmwareCheckResult> CheckViaWindowsUpdateAsync(CancellationToken cancellationToken = default)
        {
            // Script PowerShell : recherche de mises à jour UEFI/BIOS via Windows Update Agent COM.
            // IsInstalled=0 → non encore installé ; filtre sur le titre contenant UEFI/BIOS/Firmware/System Firmware.
            const string script = @"
$ErrorActionPreference = 'Stop'
try {
    $session  = New-Object -ComObject 'Microsoft.Update.Session'
    $searcher = $session.CreateUpdateSearcher()
    $result   = $searcher.Search('IsInstalled=0 AND Type=''Driver''')
    $firmware = @($result.Updates | Where-Object {
        $_.Title -match 'UEFI|BIOS|Firmware|System Firmware'
    })
    if ($firmware.Count -gt 0) {
        $titles = $firmware | ForEach-Object { $_.Title }
        Write-Output ""FIRMWARE_UPDATES_FOUND:$($firmware.Count)""
        $titles | ForEach-Object { Write-Output ""TITLE:$_"" }
    } else {
        Write-Output 'FIRMWARE_NO_UPDATES'
    }
} catch {
    Write-Output ""FIRMWARE_CHECK_ERROR:$($_.Exception.Message)""
}
";
            // -EncodedCommand (Base64 UTF-16LE) évite tout problème d'échappement avec les scripts complexes.
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                using var process = Process.Start(psi);
                if (process == null)
                    return Error("Impossible de démarrer PowerShell.");

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask  = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                var output = (await outputTask.ConfigureAwait(false)).Trim();
                var stderr = (await errorTask.ConfigureAwait(false)).Trim();

                return ParseOutput(output, stderr);
            }
            catch (OperationCanceledException)
            {
                return Error("Vérification annulée.");
            }
            catch (Exception ex)
            {
                return Error($"Erreur inattendue : {ex.Message}");
            }
        }

        private static FirmwareCheckResult ParseOutput(string output, string stderr)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                var reason = string.IsNullOrWhiteSpace(stderr) ? "Aucune réponse de Windows Update." : TruncateLine(stderr);
                return Error(reason);
            }

            if (output.StartsWith("FIRMWARE_CHECK_ERROR:", StringComparison.OrdinalIgnoreCase))
            {
                var msg = output.Substring("FIRMWARE_CHECK_ERROR:".Length).Trim();
                return Error(string.IsNullOrWhiteSpace(msg) ? "Erreur Windows Update." : TruncateLine(msg));
            }

            if (output.StartsWith("FIRMWARE_NO_UPDATES", StringComparison.OrdinalIgnoreCase))
            {
                return new FirmwareCheckResult
                {
                    Success = true,
                    UpdateAvailable = false,
                    Source = "Windows Update",
                    Details = "Aucune mise à jour BIOS/UEFI disponible via Windows Update pour ce système."
                };
            }

            if (output.StartsWith("FIRMWARE_UPDATES_FOUND:", StringComparison.OrdinalIgnoreCase))
            {
                var lines = output.Split('\n');
                var sb = new StringBuilder();
                int count = 0;
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("FIRMWARE_UPDATES_FOUND:", StringComparison.OrdinalIgnoreCase))
                    {
                        var countStr = trimmed.Substring("FIRMWARE_UPDATES_FOUND:".Length).Trim();
                        int.TryParse(countStr, out count);
                    }
                    else if (trimmed.StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine("• " + trimmed.Substring("TITLE:".Length).Trim());
                    }
                }

                return new FirmwareCheckResult
                {
                    Success = true,
                    UpdateAvailable = true,
                    Source = "Windows Update",
                    Details = $"{count} mise(s) à jour firmware disponible(s) :\n{sb.ToString().TrimEnd()}"
                };
            }

            // Format inattendu
            return Error($"Réponse Windows Update non reconnue : {TruncateLine(output)}");
        }

        private static FirmwareCheckResult Error(string message) => new FirmwareCheckResult
        {
            Success = false,
            ErrorMessage = message
        };

        private static string TruncateLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var first = s.Split('\n')[0].Trim();
            return first.Length > 300 ? first.Substring(0, 300) + "..." : first;
        }

    }
}
