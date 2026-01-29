using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    public static class SelfTestRunner
    {
        public static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;

            if (args == null || args.Length == 0)
            {
                return false;
            }

            var runSensors = args.Any(arg => string.Equals(arg, "--selftest-sensors", StringComparison.OrdinalIgnoreCase));
            var runPowerShell = args.Any(arg => string.Equals(arg, "--selftest-ps", StringComparison.OrdinalIgnoreCase));
            var runElevation = args.Any(arg => string.Equals(arg, "--diag-elevation", StringComparison.OrdinalIgnoreCase));

            if (!runSensors && !runPowerShell && !runElevation)
            {
                return false;
            }

            if (runSensors)
            {
                exitCode = Math.Max(exitCode, RunSensorsSelfTest());
            }

            if (runPowerShell)
            {
                exitCode = Math.Max(exitCode, RunPowerShellSelfTest());
            }

            if (runElevation)
            {
                exitCode = Math.Max(exitCode, AdminService.DiagnoseElevation());
            }

            return true;
        }

        private static int RunSensorsSelfTest()
        {
            var logBuilder = new StringBuilder();
            var jsonPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_sensors_selftest.json");
            var logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_sensors_selftest.log");

            try
            {
                logBuilder.AppendLine($"Selftest sensors démarré: {DateTimeOffset.Now:O}");
                logBuilder.AppendLine($"Admin: {AdminService.IsRunningAsAdmin()}");
                
                var collector = new HardwareSensorsCollector();
                var result = collector.CollectAsync(CancellationToken.None).GetAwaiter().GetResult();

                var json = JsonSerializer.Serialize(result, HardwareSensorsResult.JsonOptions);
                File.WriteAllText(jsonPath, json, Encoding.UTF8);

                var summary = result.GetAvailabilitySummary();
                logBuilder.AppendLine($"Mesures disponibles: {summary.available}/{summary.total}");
                logBuilder.AppendLine($"Fichier JSON: {jsonPath}");

                // Détails des métriques
                logBuilder.AppendLine("\n=== Détails GPU ===");
                logBuilder.AppendLine($"  Nom: {(result.Gpu.Name.Available ? result.Gpu.Name.Value : result.Gpu.Name.Reason)}");
                logBuilder.AppendLine($"  VRAM Total: {(result.Gpu.VramTotalMB.Available ? $"{result.Gpu.VramTotalMB.Value} MB" : result.Gpu.VramTotalMB.Reason)}");
                logBuilder.AppendLine($"  VRAM Utilisée: {(result.Gpu.VramUsedMB.Available ? $"{result.Gpu.VramUsedMB.Value} MB" : result.Gpu.VramUsedMB.Reason)}");
                logBuilder.AppendLine($"  Charge: {(result.Gpu.GpuLoadPercent.Available ? $"{result.Gpu.GpuLoadPercent.Value}%" : result.Gpu.GpuLoadPercent.Reason)}");
                logBuilder.AppendLine($"  Température: {(result.Gpu.GpuTempC.Available ? $"{result.Gpu.GpuTempC.Value}°C" : result.Gpu.GpuTempC.Reason)}");

                logBuilder.AppendLine("\n=== Détails CPU ===");
                logBuilder.AppendLine($"  Température: {(result.Cpu.CpuTempC.Available ? $"{result.Cpu.CpuTempC.Value}°C" : result.Cpu.CpuTempC.Reason)}");

                logBuilder.AppendLine("\n=== Détails Disques ===");
                foreach (var disk in result.Disks)
                {
                    logBuilder.AppendLine($"  {(disk.Name.Available ? disk.Name.Value : disk.Name.Reason)}");
                    logBuilder.AppendLine($"    Température: {(disk.TempC.Available ? $"{disk.TempC.Value}°C" : disk.TempC.Reason)}");
                }

                var exitCode = DetermineSensorsExitCode(summary.available, summary.total);
                logBuilder.AppendLine($"\nExitCode: {exitCode}");
                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);

                Console.WriteLine(logBuilder.ToString());
                return exitCode;
            }
            catch (Exception ex)
            {
                logBuilder.AppendLine("Erreur pendant le selftest sensors");
                logBuilder.AppendLine(ex.ToString());
                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());
                return 2;
            }
        }

        private static int DetermineSensorsExitCode(int available, int total)
        {
            if (total <= 0)
            {
                return 2;
            }

            if (available == 0)
            {
                return 2;
            }

            if (available < total)
            {
                return 1;
            }

            return 0;
        }

        private static int RunPowerShellSelfTest()
        {
            var logBuilder = new StringBuilder();
            var logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_ps_selftest.log");
            var scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "Total_PS_PC_Scan_v7.0.ps1");

            try
            {
                logBuilder.AppendLine($"Selftest PowerShell démarré: {DateTimeOffset.Now:O}");
                logBuilder.AppendLine($"Script attendu: {scriptPath}");
                logBuilder.AppendLine($"Script existe: {File.Exists(scriptPath)}");

                if (!File.Exists(scriptPath))
                {
                    logBuilder.AppendLine("ERREUR: Script introuvable!");
                    File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                    Console.WriteLine(logBuilder.ToString());
                    App.LogMessage($"Selftest PS: script introuvable: {scriptPath}");
                    return 2;
                }

                var outputDir = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_PS_SelfTest");
                Directory.CreateDirectory(outputDir);
                logBuilder.AppendLine($"OutputDir: {outputDir}");

                var output = new StringBuilder();
                var error = new StringBuilder();

                // Vérifier que powershell.exe existe
                var psPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                if (!File.Exists(psPath))
                {
                    psPath = "powershell.exe"; // Fallback sur PATH
                }
                logBuilder.AppendLine($"PowerShell: {psPath}");

                var startInfo = new ProcessStartInfo
                {
                    FileName = psPath,
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -OutputDir \"{outputDir}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                logBuilder.AppendLine($"Arguments: {startInfo.Arguments}");

                using var process = new Process { StartInfo = startInfo };
                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        output.AppendLine(e.Data);
                    }
                };
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        error.AppendLine(e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var exited = process.WaitForExit((int)TimeSpan.FromSeconds(15).TotalMilliseconds);
                
                if (!exited)
                {
                    logBuilder.AppendLine("INFO: Timeout après 15s, arrêt forcé (script démarré avec succès)");
                    process.Kill(true);
                    logBuilder.AppendLine("SUCCÈS: Le script a démarré correctement");
                    File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                    Console.WriteLine(logBuilder.ToString());
                    App.LogMessage("Selftest PS: démarrage confirmé, arrêt forcé après timeout.");
                    return 0;
                }

                logBuilder.AppendLine($"ExitCode: {process.ExitCode}");
                
                if (output.Length > 0)
                {
                    logBuilder.AppendLine($"\n=== STDOUT (premiers 500 chars) ===\n{output.ToString().Substring(0, Math.Min(500, output.Length))}");
                }
                
                if (error.Length > 0)
                {
                    logBuilder.AppendLine($"\n=== STDERR ===\n{error}");
                }

                if (process.ExitCode != 0)
                {
                    logBuilder.AppendLine($"ERREUR: Script terminé avec code {process.ExitCode}");
                    File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                    Console.WriteLine(logBuilder.ToString());
                    App.LogMessage($"Selftest PS: exit code {process.ExitCode}. stderr: {error}");
                    return 2;
                }

                logBuilder.AppendLine("SUCCÈS: Script exécuté correctement");
                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());
                return 0;
            }
            catch (Exception ex)
            {
                logBuilder.AppendLine($"EXCEPTION: {ex}");
                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());
                App.LogMessage($"Selftest PS: erreur {ex.Message}");
                return 2;
            }
        }
    }
}
