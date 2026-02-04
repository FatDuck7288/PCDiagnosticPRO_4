using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Adds and verifies Windows Defender exclusion at MACHINE level (all users).
    /// Requires admin. Used when user enables "Surveillance matérielle" (LibreHardwareMonitor).
    /// </summary>
    public static class WindowsDefenderExclusionService
    {
        /// <summary>
        /// Recommended path to exclude: the application directory (contains the exe and LibreHardwareMonitor DLLs).
        /// In dev this is typically bin\Debug\net8.0-windows\ (minimal folder), not the whole solution.
        /// </summary>
        public static string GetDefaultExclusionPath()
        {
            var baseDir = AppContext.BaseDirectory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "";
            if (string.IsNullOrEmpty(baseDir))
                baseDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName) ?? "";
            return string.IsNullOrEmpty(baseDir) ? "" : Path.GetFullPath(baseDir);
        }

        /// <summary>
        /// Adds a machine-level Defender exclusion. Requires admin.
        /// Path is quoted for spaces/accents. Logs and returns (success, user-visible message).
        /// </summary>
        public static async Task<(bool Success, string Message)> AddMachineExclusionAsync(string exclusionPath)
        {
            if (string.IsNullOrWhiteSpace(exclusionPath))
            {
                var msg = "Chemin d'exclusion vide.";
                App.LogMessage($"[DefenderExclusion] {msg}");
                return (false, msg);
            }

            var fullPath = Path.GetFullPath(exclusionPath);
            if (!Directory.Exists(fullPath))
            {
                var msg = "Le dossier à exclure n'existe pas.";
                App.LogMessage($"[DefenderExclusion] {msg} Path={fullPath}");
                return (false, msg);
            }

            // PowerShell: Add-MpPreference -ExclusionPath 'path' (machine-level when run as admin)
            // Escape single quotes inside path for PowerShell
            var escapedPath = fullPath.Replace("'", "''", StringComparison.Ordinal);
            var command = $@"Add-MpPreference -ExclusionPath '{escapedPath}'";
            App.LogMessage($"[DefenderExclusion] Executing (machine-level): Add-MpPreference -ExclusionPath '<path>'");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    var msg = "Impossible de démarrer PowerShell.";
                    App.LogMessage($"[DefenderExclusion] {msg}");
                    return (false, msg);
                }

                var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    var err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    err = (err ?? "").Trim();
                    if (string.IsNullOrEmpty(err))
                        err = "Erreur inconnue (vérifiez les stratégies de sécurité ou Tamper Protection).";
                    App.LogMessage($"[DefenderExclusion] Échec ExitCode={process.ExitCode}: {err}");
                    return (false, err);
                }

                var verified = await VerifyExclusionAsync(fullPath).ConfigureAwait(false);
                if (verified)
                {
                    App.LogMessage("[DefenderExclusion] Exception Windows Defender ajoutée (tous les usagers).");
                    return (true, "Exception Windows Defender ajoutée (tous les usagers).");
                }

                App.LogMessage("[DefenderExclusion] Commande réussie mais vérification négative.");
                return (true, "Exception ajoutée (vérification non confirmée).");
            }
            catch (Exception ex)
            {
                var msg = ex.Message ?? "Erreur inconnue.";
                App.LogMessage($"[DefenderExclusion] Exception: {msg}");
                return (false, msg);
            }
        }

        /// <summary>
        /// Adds process-based exclusions for scan executables. Requires admin.
        /// Excludes PCDiagnosticPro.exe and librespeed-cli.exe from Windows Defender scanning.
        /// </summary>
        public static async Task<(bool Success, string Message)> AddProcessExclusionsAsync()
        {
            var processes = new[] { "PCDiagnosticPro.exe", "librespeed-cli.exe" };
            var failedProcesses = new System.Collections.Generic.List<string>();

            foreach (var proc in processes)
            {
                var escapedProc = proc.Replace("'", "''", StringComparison.Ordinal);
                var command = $@"Add-MpPreference -ExclusionProcess '{escapedProc}'";
                App.LogMessage($"[DefenderExclusion] Adding process exclusion: {proc}");

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };

                    using var process = Process.Start(psi);
                    if (process == null)
                    {
                        failedProcesses.Add(proc);
                        continue;
                    }

                    var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    await process.WaitForExitAsync().ConfigureAwait(false);

                    if (process.ExitCode != 0)
                    {
                        App.LogMessage($"[DefenderExclusion] Failed to add process exclusion {proc}: {stderr}");
                        failedProcesses.Add(proc);
                    }
                    else
                    {
                        App.LogMessage($"[DefenderExclusion] Process exclusion added: {proc}");
                    }
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[DefenderExclusion] Exception adding process {proc}: {ex.Message}");
                    failedProcesses.Add(proc);
                }
            }

            if (failedProcesses.Count == 0)
            {
                return (true, "Exclusions de processus ajoutées avec succès.");
            }
            else if (failedProcesses.Count < processes.Length)
            {
                return (true, $"Exclusions partielles (échec: {string.Join(", ", failedProcesses)}).");
            }
            else
            {
                return (false, "Impossible d'ajouter les exclusions de processus.");
            }
        }

        /// <summary>
        /// Verifies that a process is excluded from Windows Defender scanning.
        /// </summary>
        public static async Task<bool> VerifyProcessExclusionAsync(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) return false;

            try
            {
                var command = "Get-MpPreference | Select-Object -ExpandProperty ExclusionProcess";
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = Process.Start(psi);
                if (process == null) return false;

                var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0) return false;

                var lines = (output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (string.Equals(line.Trim(), processName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Verifies that the given path is present in MpPreference.ExclusionPath (machine).
        /// </summary>
        public static async Task<bool> VerifyExclusionAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            try
            {
                var command = "Get-MpPreference | Select-Object -ExpandProperty ExclusionPath";
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = Process.Start(psi);
                if (process == null) return false;

                var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0) return false;

                var lines = (output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var p = line.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
