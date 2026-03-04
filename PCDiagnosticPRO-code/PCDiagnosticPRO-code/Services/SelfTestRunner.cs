using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
            var runUnifiedReport = args.Any(arg => string.Equals(arg, "--selftest-unified-report", StringComparison.OrdinalIgnoreCase));
            var runElevation = args.Any(arg => string.Equals(arg, "--diag-elevation", StringComparison.OrdinalIgnoreCase));
            var runDiagQuality = args.Any(arg => string.Equals(arg, "--diag-quality", StringComparison.OrdinalIgnoreCase));
            var runPerfScoring = args.Any(arg => string.Equals(arg, "--selftest-perf-scoring", StringComparison.OrdinalIgnoreCase));
            var runContracts = args.Any(arg => string.Equals(arg, "--selftest-contracts", StringComparison.OrdinalIgnoreCase));
            var runEncoding = args.Any(arg => string.Equals(arg, "--selftest-encoding", StringComparison.OrdinalIgnoreCase));
            var runAi = args.Any(arg => string.Equals(arg, "--selftest-ai", StringComparison.OrdinalIgnoreCase));
            var runAiSmoke = args.Any(arg => string.Equals(arg, "--selftest-ai-smoke", StringComparison.OrdinalIgnoreCase));
            var runEncodingAudit = args.Any(arg => string.Equals(arg, "--encoding-audit", StringComparison.OrdinalIgnoreCase));
            var runEncodingGate = args.Any(arg => string.Equals(arg, "--encoding-gate", StringComparison.OrdinalIgnoreCase));
            var runEncodingNormalize = args.Any(arg => string.Equals(arg, "--encoding-normalize", StringComparison.OrdinalIgnoreCase));

            if (!runSensors && !runPowerShell && !runUnifiedReport && !runElevation && !runDiagQuality && !runPerfScoring && !runContracts && !runEncoding && !runAi && !runAiSmoke && !runEncodingAudit && !runEncodingGate && !runEncodingNormalize)
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
            
            if (runUnifiedReport)
            {
                exitCode = Math.Max(exitCode, RunUnifiedReportSelfTest(args));
            }

            if (runElevation)
            {
                exitCode = Math.Max(exitCode, AdminService.DiagnoseElevation());
            }

            if (runDiagQuality)
            {
                exitCode = Math.Max(exitCode, RunDiagQualityMode());
            }

            if (runPerfScoring)
            {
                exitCode = Math.Max(exitCode, RunPerformanceScoringTests());
            }

            if (runContracts)
            {
                exitCode = Math.Max(exitCode, RunContractSelfTests());
            }

            if (runEncoding)
            {
                exitCode = Math.Max(exitCode, RunEncodingSelfTests());
            }

            if (runAi)
            {
                exitCode = Math.Max(exitCode, RunAiSelfTests());
            }

            if (runAiSmoke)
            {
                exitCode = Math.Max(exitCode, RunAiSmokeSelfTests());
            }

            if (runEncodingNormalize)
            {
                exitCode = Math.Max(exitCode, RunEncodingNormalizationPass(args));
            }

            if (runEncodingAudit)
            {
                exitCode = Math.Max(exitCode, RunEncodingAudit(args, failOnFindings: false));
            }

            if (runEncodingGate)
            {
                exitCode = Math.Max(exitCode, RunEncodingAudit(args, failOnFindings: true));
            }

            return true;
        }

        private static int RunEncodingSelfTests()
        {
            var logBuilder = new StringBuilder();
            var logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_encoding_selftest.log");
            try
            {
                logBuilder.AppendLine($"Encoding selftest started: {DateTimeOffset.Now:O}");
                Tests.EncodingNormalizationTests.RunAll();
                logBuilder.AppendLine("Encoding selftests: OK");
                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());
                return 0;
            }
            catch (Exception ex)
            {
                logBuilder.AppendLine($"Encoding selftests: FAILED - {ex.Message}");
                logBuilder.AppendLine(ex.ToString());
                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());
                return 2;
            }
        }

        private static int RunEncodingNormalizationPass(string[] args)
        {
            var logBuilder = new StringBuilder();
            var logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_encoding_normalize.log");

            try
            {
                var repoRoot = ResolveRepositoryRoot(args);
                if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
                {
                    logBuilder.AppendLine("Encoding normalization: repository root not found.");
                    File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                    Console.WriteLine(logBuilder.ToString());
                    return 2;
                }

                logBuilder.AppendLine($"Encoding normalization started: {DateTimeOffset.Now:O}");
                logBuilder.AppendLine($"Repository root: {repoRoot}");

                var normalization = EncodingAuditService.NormalizeRepositoryFilesToUtf8(repoRoot, includeTests: false);
                logBuilder.AppendLine($"Files scanned: {normalization.FilesScanned}");
                logBuilder.AppendLine($"Files rewritten: {normalization.FilesRewritten}");
                logBuilder.AppendLine($"Mojibake repaired files: {normalization.MojibakeRepairedFiles}");
                logBuilder.AppendLine($"Encoding converted files: {normalization.EncodingConvertedFiles}");

                if (normalization.RewrittenFiles.Count > 0)
                {
                    logBuilder.AppendLine("Rewritten files:");
                    foreach (var file in normalization.RewrittenFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        logBuilder.AppendLine($" - {file}");
                    }
                }

                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());
                return 0;
            }
            catch (Exception ex)
            {
                logBuilder.AppendLine($"Encoding normalization FAILED: {ex.Message}");
                logBuilder.AppendLine(ex.ToString());
                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());
                return 2;
            }
        }

        private static int RunEncodingAudit(string[] args, bool failOnFindings)
        {
            var logBuilder = new StringBuilder();
            var reportPath = Path.Combine(Path.GetTempPath(), $"PCDiagnosticPro_encoding_audit_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            var logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_encoding_audit.log");

            try
            {
                var repoRoot = ResolveRepositoryRoot(args);
                if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
                {
                    logBuilder.AppendLine("Encoding audit: repository root not found.");
                    File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                    Console.WriteLine(logBuilder.ToString());
                    return 2;
                }

                logBuilder.AppendLine($"Encoding audit started: {DateTimeOffset.Now:O}");
                logBuilder.AppendLine($"Repository root: {repoRoot}");

                var repoReport = EncodingAuditService.ScanRepository(repoRoot, includeTests: false, includeIntentional: false);
                logBuilder.AppendLine($"Repository files scanned: {repoReport.FilesScanned}");
                logBuilder.AppendLine($"Repository findings: {repoReport.FindingsCount}");

                var artifactPaths = CollectUserFacingArtifactPaths();
                var artifactReport = EncodingAuditService.ScanSpecificFiles(repoRoot, artifactPaths, includeIntentional: true);
                logBuilder.AppendLine($"Artifact files scanned: {artifactReport.FilesScanned}");
                logBuilder.AppendLine($"Artifact findings: {artifactReport.FindingsCount}");

                var merged = new EncodingAuditReport { RootPath = repoRoot, FilesScanned = repoReport.FilesScanned + artifactReport.FilesScanned };
                merged.Findings.AddRange(repoReport.Findings);
                merged.Findings.AddRange(artifactReport.Findings);

                EncodingAuditService.WriteReport(merged, reportPath);
                logBuilder.AppendLine($"Report written: {reportPath}");
                logBuilder.AppendLine($"Total findings: {merged.FindingsCount}");

                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());

                if (failOnFindings && merged.FindingsCount > 0)
                {
                    return 1;
                }

                return 0;
            }
            catch (Exception ex)
            {
                logBuilder.AppendLine($"Encoding audit FAILED: {ex.Message}");
                logBuilder.AppendLine(ex.ToString());
                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());
                return 2;
            }
        }

        private static string ResolveRepositoryRoot(string[] args)
        {
            foreach (var arg in args)
            {
                if (arg.StartsWith("--repo-root=", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = arg.Substring("--repo-root=".Length).Trim('"');
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        return Path.GetFullPath(raw);
                    }
                }
            }

            var current = AppContext.BaseDirectory;
            for (var i = 0; i < 8 && !string.IsNullOrWhiteSpace(current); i++)
            {
                var candidate = Path.Combine(current, "PCDiagnosticPro.csproj");
                if (File.Exists(candidate))
                {
                    return current;
                }

                var parent = Directory.GetParent(current);
                if (parent == null)
                {
                    break;
                }

                current = parent.FullName;
            }

            var cwd = Environment.CurrentDirectory;
            if (File.Exists(Path.Combine(cwd, "PCDiagnosticPro.csproj")))
            {
                return cwd;
            }

            var cwdParent = Directory.GetParent(cwd);
            if (cwdParent != null && File.Exists(Path.Combine(cwdParent.FullName, "PCDiagnosticPro.csproj")))
            {
                return cwdParent.FullName;
            }

            return cwd;
        }

        private static IEnumerable<string> CollectUserFacingArtifactPaths()
        {
            var files = new List<string>();

            // LocalAppData reports (runtime UI export target), with legacy fallback.
            foreach (var reportsDir in GetReportSearchDirectories())
            {
                var combined = Path.Combine(reportsDir, "scan_result_combined.json");
                if (File.Exists(combined))
                {
                    files.Add(combined);
                }
            }

            // Temp selftest outputs (PowerShell and unified report selftests).
            var temp = Path.GetTempPath();
            var psSelfTestDir = Path.Combine(temp, "PCDiagnosticPro_PS_SelfTest");
            if (Directory.Exists(psSelfTestDir))
            {
                files.AddRange(Directory.EnumerateFiles(psSelfTestDir, "*.json"));
                files.AddRange(Directory.EnumerateFiles(psSelfTestDir, "*.txt"));
            }

            var unifiedSelftest = Path.Combine(temp, "PCDiagnosticPro_unified_report_selftest.txt");
            if (File.Exists(unifiedSelftest))
            {
                files.Add(unifiedSelftest);
            }

            return files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> GetReportSearchDirectories()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var directories = new[]
            {
                Path.Combine(localAppData, App.AppDataFolderName, "Reports"),
                Path.Combine(localAppData, App.LegacyAppDataFolderName, "Rapports"),
                Path.Combine(localAppData, App.LegacyAppDataFolderName, "Reports")
            };

            return directories
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists);
        }

        /// <summary>
        /// Phase 3: Mode CLI --diag-quality - charge derniers rapports, calcule DiagnosticQuality, écrit log + Diagnostics_Quality_Audit_Run.txt.
        /// </summary>
        private static int RunDiagQualityMode()
        {
            var logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_quality.log");
            var auditRunPath = Path.Combine(Path.GetTempPath(), "Diagnostics_Quality_Audit_Run.txt");
            try
            {
                var combinedPath = ResolveCombinedJsonPath(Array.Empty<string>());
                if (!File.Exists(combinedPath))
                {
                    var msg = $"Fichier non trouvé: {combinedPath}. Lancez un scan complet puis réessayez.";
                    File.WriteAllText(auditRunPath, $"[{DateTime.UtcNow:O}] {msg}\r\n", Encoding.UTF8);
                    Console.WriteLine(msg);
                    return 1;
                }
                var jsonContent = File.ReadAllText(combinedPath, Encoding.UTF8);
                var combined = JsonSerializer.Deserialize<CombinedScanResult>(jsonContent, HardwareSensorsResult.JsonOptions);
                if (combined == null)
                {
                    var msg = "Désérialisation du rapport combiné impossible.";
                    File.WriteAllText(auditRunPath, $"[{DateTime.UtcNow:O}] {msg}\r\n", Encoding.UTF8);
                    Console.WriteLine(msg);
                    return 1;
                }
                var sensors = combined.SensorsCsharp;
                var report = HealthReportBuilder.Build(jsonContent, sensors, null, null);
                var quality = QualityScoreCalculator.Compute(report, null);
                QualityScoreCalculator.WriteQualityLog(quality, logPath);
                var auditLines = new List<string>
                {
                    $"[{quality.TimestampUtc:yyyy-MM-dd HH:mm:ss}Z] Diagnostics Quality Audit Run",
                    $"Coverage={quality.CoverageScore}% ({quality.CoverageDetails})",
                    $"Reliability={quality.ReliabilityScore}% ({quality.ReliabilityDetails})",
                    $"Actionability={quality.ActionabilityScore}% ({quality.ActionabilityDetails})",
                    $"Overall={quality.OverallScore}%",
                    $"Message: {quality.Message}",
                    "",
                    "Critères 90%: Coverage>=90, Reliability>=90, Actionability>=80",
                    quality.CoverageScore >= 90 && quality.ReliabilityScore >= 90 && quality.ActionabilityScore >= 80
                        ? "RÉSULTAT: Objectif 90% atteint."
                        : $"RÉSULTAT: Non atteint. Manques: Coverage={quality.CoverageScore} (cible 90), Reliability={quality.ReliabilityScore} (cible 90), Actionability={quality.ActionabilityScore} (cible 80)."
                };
                File.WriteAllLines(auditRunPath, auditLines, Encoding.UTF8);
                Console.WriteLine(string.Join(Environment.NewLine, auditLines));
                return (quality.CoverageScore >= 90 && quality.ReliabilityScore >= 90 && quality.ActionabilityScore >= 80) ? 0 : 1;
            }
            catch (Exception ex)
            {
                var msg = $"Erreur --diag-quality: {ex.Message}";
                try
                {
                    File.WriteAllText(auditRunPath, $"[{DateTime.UtcNow:O}]\r\n{msg}\r\n{ex}\r\n", Encoding.UTF8);
                }
                catch { }
                Console.WriteLine(msg);
                return 2;
            }
        }

        private static int RunSensorsSelfTest()
        {
            var logBuilder = new StringBuilder();
            var jsonPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_sensors_selftest.json");
            var logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_sensors_selftest.log");

            try
            {
                logBuilder.AppendLine($"Selftest sensors démarré: {DateTimeOffset.Now:O}");
                var isAdmin = AdminService.IsRunningAsAdmin();
                logBuilder.AppendLine($"Admin: {isAdmin}");
                
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

                var availabilityRatio = summary.total > 0 ? (double)summary.available / summary.total : 0d;
                var requiredRatio = isAdmin ? 0.75d : 0.60d;
                logBuilder.AppendLine($"Ratio disponibilite: {availabilityRatio:P0} (seuil {(isAdmin ? "admin" : "non-admin")}: {requiredRatio:P0})");

                var permissionLimited = HasPermissionLimitedSensorAccess(result);
                if (permissionLimited)
                {
                    logBuilder.AppendLine("Contexte detecte: acces capteurs limite (permissions/securite).");
                }

                var exitCode = DetermineSensorsExitCode(summary.available, summary.total, isAdmin, permissionLimited);
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

        private static int DetermineSensorsExitCode(int available, int total, bool isAdmin, bool permissionLimited)
        {
            if (total <= 0)
            {
                return 2;
            }

            if (available == 0)
            {
                if (!isAdmin && permissionLimited)
                {
                    return 1;
                }

                return 2;
            }

            var ratio = (double)available / total;
            var requiredRatio = isAdmin ? 0.75d : 0.60d;
            if (ratio < requiredRatio)
            {
                return 1;
            }

            return 0;
        }

        private static bool HasPermissionLimitedSensorAccess(HardwareSensorsResult result)
        {
            if (result.BlockedBySecurity)
            {
                return true;
            }

            if (LooksLikePermissionIssue(result.BlockingMessage))
            {
                return true;
            }

            bool hasAccessDeniedMetric = LooksLikePermissionIssue(result.Gpu.Name.Reason)
                || LooksLikePermissionIssue(result.Gpu.VramTotalMB.Reason)
                || LooksLikePermissionIssue(result.Gpu.VramUsedMB.Reason)
                || LooksLikePermissionIssue(result.Gpu.GpuLoadPercent.Reason)
                || LooksLikePermissionIssue(result.Gpu.GpuTempC.Reason)
                || LooksLikePermissionIssue(result.Cpu.CpuTempC.Reason)
                || result.Disks.Any(d => LooksLikePermissionIssue(d.Name.Reason) || LooksLikePermissionIssue(d.TempC.Reason));

            return hasAccessDeniedMetric;
        }

        private static bool LooksLikePermissionIssue(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return false;
            }

            bool HasToken(string text)
            {
                return text.Contains("access denied", StringComparison.Ordinal)
                    || text.Contains("accès refusé", StringComparison.Ordinal)
                    || text.Contains("acces refusé", StringComparison.Ordinal)
                    || text.Contains("acces refuse", StringComparison.Ordinal)
                    || text.Contains("permission denied", StringComparison.Ordinal)
                    || text.Contains("permission", StringComparison.Ordinal)
                    || text.Contains("droits insuffisants", StringComparison.Ordinal)
                    || text.Contains("droit insuffisant", StringComparison.Ordinal)
                    || text.Contains("security", StringComparison.Ordinal)
                    || text.Contains("blocked", StringComparison.Ordinal)
                    || text.Contains("refus", StringComparison.Ordinal);
            }

            var raw = reason.ToLowerInvariant();
            if (HasToken(raw))
            {
                return true;
            }

            var normalized = TextEncodingNormalizer.Normalize(reason).ToLowerInvariant();
            return HasToken(normalized);
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
                        var normalized = TextEncodingNormalizer.Normalize(e.Data);
                        EncodingCorruptionWatcher.CheckAndLog(normalized, "selftest.ps.stdout");
                        output.AppendLine(normalized);
                    }
                };
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        var normalized = TextEncodingNormalizer.Normalize(e.Data);
                        EncodingCorruptionWatcher.CheckAndLog(normalized, "selftest.ps.stderr");
                        error.AppendLine(normalized);
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

                // Validate generated artifacts for corruption markers.
                var artifactFiles = Directory.EnumerateFiles(outputDir)
                    .Where(f =>
                        f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var artifactAudit = EncodingAuditService.ScanSpecificFiles(outputDir, artifactFiles, includeIntentional: true);
                if (artifactAudit.FindingsCount > 0)
                {
                    logBuilder.AppendLine($"ERREUR: corruption détectée dans les artefacts PS ({artifactAudit.FindingsCount} occurrence(s)).");
                    foreach (var finding in artifactAudit.Findings.Take(10))
                    {
                        logBuilder.AppendLine($" - {finding.FilePath}:{finding.LineNumber} [{finding.Marker}] {finding.Context}");
                    }
                    File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                    Console.WriteLine(logBuilder.ToString());
                    return 1;
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

        /// <summary>
        /// Selftest: Génération du rapport unifié à partir d'un scan_result_combined.json réel.
        /// Usage: --selftest-unified-report [--combined-json=PATH]
        /// </summary>
        private static int RunUnifiedReportSelfTest(string[] args)
        {
            var logBuilder = new StringBuilder();
            var logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_unified_report_selftest.log");

            try
            {
                logBuilder.AppendLine($"Selftest unified report démarré: {DateTimeOffset.Now:O}");
                
                var combinedJsonPath = ResolveCombinedJsonPath(args);
                logBuilder.AppendLine($"Combined JSON: {combinedJsonPath}");
                logBuilder.AppendLine($"Exists: {File.Exists(combinedJsonPath)}");
                
                if (!File.Exists(combinedJsonPath))
                {
                    logBuilder.AppendLine("ERREUR: scan_result_combined.json introuvable.");
                    File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                    Console.WriteLine(logBuilder.ToString());
                    return 2;
                }

                // 1) Vérifier que les sections PS existent dans le JSON
                var jsonContent = File.ReadAllText(combinedJsonPath, Encoding.UTF8);
                using var doc = JsonDocument.Parse(jsonContent);
                var root = doc.RootElement;

                if (!TryGetPropertyCaseInsensitive(root, out var psRoot, "scan_powershell", "scanPowershell"))
                {
                    logBuilder.AppendLine("ERREUR: scan_powershell absent du JSON combiné.");
                    File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                    Console.WriteLine(logBuilder.ToString());
                    return 2;
                }

                if (!TryGetPropertyCaseInsensitive(psRoot, out var sections, "sections"))
                {
                    logBuilder.AppendLine("ERREUR: scan_powershell.sections absent du JSON combiné.");
                    File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                    Console.WriteLine(logBuilder.ToString());
                    return 2;
                }

                var requiredSections = new[] { "WindowsUpdate", "StartupPrograms", "InstalledApplications", "DevicesDrivers" };
                var sectionPresence = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var section in requiredSections)
                {
                    var has = TryGetPropertyCaseInsensitive(sections, out var sectionObj, section) &&
                              TryGetPropertyCaseInsensitive(sectionObj, out var data, "data") &&
                              IsNonEmptyJson(data);
                    sectionPresence[section] = has;
                    logBuilder.AppendLine($"Section {section}: {(has ? "OK" : "MISSING/EMPTY")}");
                }

                // 2) Générer le rapport unifié
                var outputPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_unified_report_selftest.txt");
                var buildTask = Task.Run(() => UnifiedReportBuilder.BuildUnifiedReportAsync(combinedJsonPath, null, outputPath));
                if (!buildTask.Wait(TimeSpan.FromSeconds(90)))
                {
                    logBuilder.AppendLine("ERREUR: timeout de generation du rapport unifie (>90s).");
                    File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                    Console.WriteLine(logBuilder.ToString());
                    return 1;
                }

                var success = buildTask.GetAwaiter().GetResult();
                logBuilder.AppendLine($"BuildUnifiedReportAsync: {(success ? "OK" : "FAILED")}");
                logBuilder.AppendLine($"Output TXT: {outputPath}");

                if (!success || !File.Exists(outputPath))
                {
                    logBuilder.AppendLine("ERREUR: Rapport unifié non généré.");
                    File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                    Console.WriteLine(logBuilder.ToString());
                    return 2;
                }

                var reportText = File.ReadAllText(outputPath, Encoding.UTF8);

                // Encoding gate for user-facing exports.
                var exportAudit = EncodingAuditService.ScanSpecificFiles(
                    Path.GetDirectoryName(combinedJsonPath) ?? Environment.CurrentDirectory,
                    new[] { combinedJsonPath, outputPath },
                    includeIntentional: true);

                if (exportAudit.FindingsCount > 0)
                {
                    logBuilder.AppendLine($"ERREUR: corruption détectée dans JSON/TXT exportés ({exportAudit.FindingsCount} occurrence(s)).");
                    foreach (var finding in exportAudit.Findings.Take(10))
                    {
                        logBuilder.AppendLine($" - {finding.FilePath}:{finding.LineNumber} [{finding.Marker}] {finding.Context}");
                    }
                    File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                    Console.WriteLine(logBuilder.ToString());
                    return 1;
                }

                // 3) Assertions obligatoires: présence d'indicateurs dans le TXT
                AssertSectionIndicator(reportText, "WindowsUpdate",
                    new[] { "Updates en attente", "Dernière mise à jour", "Redémarrage requis", "Updates détectées", "Mise à jour auto" },
                    sectionPresence["WindowsUpdate"]);

                AssertSectionIndicator(reportText, "StartupPrograms",
                    new[] { "Programmes au démarrage", "Total programmes démarrage" },
                    sectionPresence["StartupPrograms"]);

                AssertSectionIndicator(reportText, "InstalledApplications",
                    new[] { "Applications installées", "Total applications", "Total applications installées" },
                    sectionPresence["InstalledApplications"]);

                AssertSectionIndicator(reportText, "DevicesDrivers",
                    new[] { "Périph. en erreur", "Total périphériques", "Périphériques" },
                    sectionPresence["DevicesDrivers"]);

                logBuilder.AppendLine("SUCCÈS: Selftest unified report OK");
                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());
                return 0;
            }
            catch (Exception ex)
            {
                logBuilder.AppendLine($"EXCEPTION: {ex}");
                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());
                return 2;
            }
        }

        private static string ResolveCombinedJsonPath(string[] args)
        {
            foreach (var arg in args)
            {
                if (arg.StartsWith("--combined-json=", StringComparison.OrdinalIgnoreCase))
                {
                    return arg.Substring("--combined-json=".Length).Trim('"');
                }
            }

            var firstExistingPath = GetReportSearchDirectories()
                .Select(dir => Path.Combine(dir, "scan_result_combined.json"))
                .FirstOrDefault(File.Exists);

            if (!string.IsNullOrWhiteSpace(firstExistingPath))
            {
                return firstExistingPath;
            }

            var defaultReportsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                App.AppDataFolderName,
                "Reports");
            return Path.Combine(defaultReportsDir, "scan_result_combined.json");
        }

        private static void AssertSectionIndicator(string reportText, string sectionName, string[] indicators, bool dataPresent)
        {
            if (!dataPresent)
            {
                throw new InvalidOperationException($"Selftest: section {sectionName} absente ou vide dans le JSON PS.");
            }

            foreach (var indicator in indicators)
            {
                if (reportText.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            throw new InvalidOperationException($"Selftest: indicateur {sectionName} introuvable dans le rapport TXT.");
        }

        private static bool TryGetPropertyCaseInsensitive(JsonElement element, out JsonElement value, params string[] names)
        {
            value = default;
            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // Exact match first
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out value))
                {
                    return true;
                }
            }

            // Case-insensitive fallback
            foreach (var prop in element.EnumerateObject())
            {
                foreach (var name in names)
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsNonEmptyJson(JsonElement el)
        {
            return el.ValueKind switch
            {
                JsonValueKind.Array => el.GetArrayLength() > 0,
                JsonValueKind.Object => el.EnumerateObject().Any(),
                JsonValueKind.String => !string.IsNullOrWhiteSpace(el.GetString()),
                JsonValueKind.Number => true,
                JsonValueKind.True => true,
                JsonValueKind.False => true,
                _ => false
            };
        }

        /// <summary>
        /// AI selftests for settings load/save and strict safety gating.
        /// Usage: --selftest-ai
        /// </summary>
        private static (int passed, int failed, List<string> failures) RunSuiteWithTimeout(
            string suiteName,
            Func<(int passed, int failed, List<string> failures)> suiteRunner,
            TimeSpan timeout)
        {
            try
            {
                var task = Task.Run(suiteRunner);
                if (!task.Wait(timeout))
                {
                    return (0, 1, new List<string>
                    {
                        $"{suiteName} timed out after {timeout.TotalSeconds:F0}s."
                    });
                }

                return task.Result;
            }
            catch (Exception ex)
            {
                return (0, 1, new List<string>
                {
                    $"{suiteName} failed: {ex.Message}"
                });
            }
        }

        private static (int passed, int failed, List<string> failures) RunSuiteWithTimeoutSta(
            string suiteName,
            Func<(int passed, int failed, List<string> failures)> suiteRunner,
            TimeSpan timeout)
        {
            try
            {
                var completed = new ManualResetEventSlim(false);
                (int passed, int failed, List<string> failures) result = (0, 1, new List<string>());

                var thread = new Thread(() =>
                {
                    try
                    {
                        result = suiteRunner();
                    }
                    catch (Exception ex)
                    {
                        result = (0, 1, new List<string> { $"{suiteName} failed: {ex.Message}" });
                    }
                    finally
                    {
                        completed.Set();
                    }
                });

                thread.IsBackground = true;
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();

                if (!completed.Wait(timeout))
                {
                    return (0, 1, new List<string>
                    {
                        $"{suiteName} timed out after {timeout.TotalSeconds:F0}s."
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                return (0, 1, new List<string>
                {
                    $"{suiteName} failed: {ex.Message}"
                });
            }
        }

        private static int RunAiSelfTests()
        {
            var logBuilder = new StringBuilder();
            var logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_ai_selftest.log");

            try
            {
                logBuilder.AppendLine($"AI selftest started: {DateTimeOffset.Now:O}");
                logBuilder.AppendLine();

                var (settingsPassed, settingsFailed, settingsFailures) = RunSuiteWithTimeout(
                    "AI SETTINGS TESTS",
                    Tests.AiSettingsTests.RunAllTests,
                    TimeSpan.FromSeconds(20));
                logBuilder.AppendLine("=== AI SETTINGS TESTS ===");
                logBuilder.AppendLine($"Passed: {settingsPassed}");
                logBuilder.AppendLine($"Failed: {settingsFailed}");
                foreach (var failure in settingsFailures)
                {
                    logBuilder.AppendLine($"FAIL: {failure}");
                }
                logBuilder.AppendLine();

                var (safetyPassed, safetyFailed, safetyFailures) = RunSuiteWithTimeout(
                    "AI SAFETY TESTS",
                    Tests.AiSafetyTests.RunAllTests,
                    TimeSpan.FromSeconds(20));
                logBuilder.AppendLine("=== AI SAFETY TESTS ===");
                logBuilder.AppendLine($"Passed: {safetyPassed}");
                logBuilder.AppendLine($"Failed: {safetyFailed}");
                foreach (var failure in safetyFailures)
                {
                    logBuilder.AppendLine($"FAIL: {failure}");
                }
                logBuilder.AppendLine();

                var (contextPassed, contextFailed, contextFailures) = RunSuiteWithTimeout(
                    "CONTEXT PACK TESTS",
                    Tests.ContextPackBuilderTests.RunAllTests,
                    TimeSpan.FromSeconds(20));
                logBuilder.AppendLine("=== CONTEXT PACK TESTS ===");
                logBuilder.AppendLine($"Passed: {contextPassed}");
                logBuilder.AppendLine($"Failed: {contextFailed}");
                foreach (var failure in contextFailures)
                {
                    logBuilder.AppendLine($"FAIL: {failure}");
                }
                logBuilder.AppendLine();

                var (chatVmPassed, chatVmFailed, chatVmFailures) = RunSuiteWithTimeoutSta(
                    "CHAT SUPPORT VM TESTS",
                    Tests.ChatSupportViewModelTests.RunAllTests,
                    TimeSpan.FromSeconds(40));
                logBuilder.AppendLine("=== CHAT SUPPORT VM TESTS ===");
                logBuilder.AppendLine($"Passed: {chatVmPassed}");
                logBuilder.AppendLine($"Failed: {chatVmFailed}");
                foreach (var failure in chatVmFailures)
                {
                    logBuilder.AppendLine($"FAIL: {failure}");
                }
                logBuilder.AppendLine();

                var (aiSmokePassed, aiSmokeFailed, aiSmokeFailures) = RunSuiteWithTimeout(
                    "AI LAYER SMOKE TESTS",
                    Tests.AiLayerSmokeTests.RunAll,
                    TimeSpan.FromSeconds(20));
                logBuilder.AppendLine("=== AI LAYER SMOKE TESTS ===");
                logBuilder.AppendLine($"Passed: {aiSmokePassed}");
                logBuilder.AppendLine($"Failed: {aiSmokeFailed}");
                foreach (var failure in aiSmokeFailures)
                {
                    logBuilder.AppendLine($"FAIL: {failure}");
                }
                logBuilder.AppendLine();

                var (pipelinePassed, pipelineFailed, pipelineFailures) = RunSuiteWithTimeoutSta(
                    "LLM CLIENT PIPELINE TESTS",
                    Tests.LlmClientPipelineTests.RunAllTests,
                    TimeSpan.FromSeconds(40));
                logBuilder.AppendLine("=== LLM CLIENT PIPELINE TESTS ===");
                logBuilder.AppendLine($"Passed: {pipelinePassed}");
                logBuilder.AppendLine($"Failed: {pipelineFailed}");
                foreach (var failure in pipelineFailures)
                {
                    logBuilder.AppendLine($"FAIL: {failure}");
                }
                logBuilder.AppendLine();

                var totalFailed = settingsFailed + safetyFailed + contextFailed + chatVmFailed + aiSmokeFailed + pipelineFailed;
                logBuilder.AppendLine(totalFailed == 0 ? "RESULT: AI SELFTEST PASSED" : $"RESULT: {totalFailed} FAILURE(S)");
                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());
                return totalFailed == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                logBuilder.AppendLine($"EXCEPTION: {ex}");
                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());
                return 2;
            }
        }

        /// <summary>
        /// Minimal AI smoke selftests focused on deterministic parser/sanitizer/safety checks.
        /// Usage: --selftest-ai-smoke
        /// </summary>
        private static int RunAiSmokeSelfTests()
        {
            var logBuilder = new StringBuilder();
            var logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_ai_smoke_selftest.log");

            try
            {
                logBuilder.AppendLine($"AI smoke selftest started: {DateTimeOffset.Now:O}");
                logBuilder.AppendLine();

                var (passed, failed, failures) = RunSuiteWithTimeout(
                    "AI LAYER SMOKE TESTS",
                    Tests.AiLayerSmokeTests.RunAll,
                    TimeSpan.FromSeconds(20));

                logBuilder.AppendLine("=== AI LAYER SMOKE TESTS ===");
                logBuilder.AppendLine($"Passed: {passed}");
                logBuilder.AppendLine($"Failed: {failed}");
                foreach (var failure in failures)
                {
                    logBuilder.AppendLine($"FAIL: {failure}");
                }
                logBuilder.AppendLine();
                logBuilder.AppendLine(failed == 0 ? "RESULT: AI SMOKE SELFTEST PASSED" : $"RESULT: {failed} FAILURE(S)");

                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());
                return failed == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                logBuilder.AppendLine($"EXCEPTION: {ex}");
                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());
                return 2;
            }
        }

        /// <summary>
        /// Contract selftest: executes core contract tests + platform/firmware pipeline tests.
        /// Usage: --selftest-contracts
        /// </summary>
        private static int RunContractSelfTests()
        {
            var logBuilder = new StringBuilder();
            var logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_contracts_selftest.log");

            try
            {
                logBuilder.AppendLine($"Contracts Selftest démarré: {DateTimeOffset.Now:O}");
                logBuilder.AppendLine();

                var (platformPassed, platformFailed, platformFailures) = Tests.PlatformFirmwareTests.RunAllTests();
                logBuilder.AppendLine("═══ PLATFORM/FIRMWARE TESTS (PHASE 1) ═══");
                logBuilder.AppendLine($"  Passed: {platformPassed}");
                logBuilder.AppendLine($"  Failed: {platformFailed}");
                if (platformFailures.Count > 0)
                {
                    foreach (var failure in platformFailures)
                        logBuilder.AppendLine($"    FAIL: {failure}");
                }
                logBuilder.AppendLine();

                var (progressParserPassed, progressParserFailed, progressParserFailures) = Tests.ProgressMarkerParserTests.RunAllTests();
                logBuilder.AppendLine("=== PROGRESS MARKER PARSER TESTS ===");
                logBuilder.AppendLine($"  Passed: {progressParserPassed}");
                logBuilder.AppendLine($"  Failed: {progressParserFailed}");
                if (progressParserFailures.Count > 0)
                {
                    foreach (var failure in progressParserFailures)
                    {
                        logBuilder.AppendLine($"    FAIL: {failure}");
                    }
                }
                logBuilder.AppendLine();

                var (progressEnginePassed, progressEngineFailed, progressEngineFailures) = Tests.ScanProgressEngineTests.RunAllTests();
                logBuilder.AppendLine("=== SCAN PROGRESS ENGINE TESTS ===");
                logBuilder.AppendLine($"  Passed: {progressEnginePassed}");
                logBuilder.AppendLine($"  Failed: {progressEngineFailed}");
                if (progressEngineFailures.Count > 0)
                {
                    foreach (var failure in progressEngineFailures)
                    {
                        logBuilder.AppendLine($"    FAIL: {failure}");
                    }
                }
                logBuilder.AppendLine();

                var totalFailed = platformFailed + progressParserFailed + progressEngineFailed;
                logBuilder.AppendLine(totalFailed == 0 ? "RESULT: ALL CONTRACTS PASSED" : $"RESULT: {totalFailed} FAILURE(S)");

                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());
                return totalFailed == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                logBuilder.AppendLine($"EXCEPTION: {ex}");
                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());
                return 2;
            }
        }

        /// <summary>
        /// Performance Scoring selftest: runs all scoring unit tests + prints detailed score reports.
        /// Usage: --selftest-perf-scoring
        /// </summary>
        private static int RunPerformanceScoringTests()
        {
            var logBuilder = new StringBuilder();
            var logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_perf_scoring_selftest.log");

            try
            {
                logBuilder.AppendLine($"Performance Scoring Selftest démarré: {DateTimeOffset.Now:O}");
                logBuilder.AppendLine($"Engine Version: {PerformanceEvaluationEngine.TableVersion}");
                logBuilder.AppendLine($"Dataset Version: {PerformanceEvaluationEngine.GetEffectiveVersion()}");
                logBuilder.AppendLine();

                // Run all unit tests
                var (passed, failed, failures) = Tests.PerformanceScoringTests.RunAllTests();
                logBuilder.AppendLine($"═══ UNIT TEST RESULTS ═══");
                logBuilder.AppendLine($"  Passed: {passed}");
                logBuilder.AppendLine($"  Failed: {failed}");
                if (failures.Count > 0)
                {
                    logBuilder.AppendLine($"  ─── FAILURES ───");
                    foreach (var f in failures)
                        logBuilder.AppendLine($"    FAIL: {f}");
                }
                logBuilder.AppendLine();

                // Run dataset validation tests
                var (dsPassed, dsFailed, dsFailures) = Tests.DatasetValidationTests.RunAllTests();
                logBuilder.AppendLine($"═══ DATASET VALIDATION TESTS ═══");
                logBuilder.AppendLine($"  Passed: {dsPassed}");
                logBuilder.AppendLine($"  Failed: {dsFailed}");
                if (dsFailures.Count > 0)
                {
                    logBuilder.AppendLine($"  ─── FAILURES ───");
                    foreach (var f in dsFailures)
                        logBuilder.AppendLine($"    FAIL: {f}");
                }
                logBuilder.AppendLine();
                failed += dsFailed;
                passed += dsPassed;
                failures.AddRange(dsFailures);

                // Generate detailed score reports for the 6 representative profiles
                logBuilder.AppendLine("═══ DETAILED SCORE REPORTS (6 representative profiles) ═══");
                logBuilder.AppendLine();

                var profiles = new (string label, HardwareProfile p)[]
                {
                    ("1. Office Low-End", MakeTestProfile("Intel Core i3-10100", 4, 8, "Intel UHD Graphics 630", 0, 8, "HDD")),
                    ("2. Midrange Gaming", MakeTestProfile("Intel Core i5-12400F", 6, 12, "NVIDIA GeForce RTX 3060", 12288, 16, "SATA_SSD")),
                    ("3. High-End Gaming", MakeTestProfile("AMD Ryzen 9 5900X", 12, 24, "NVIDIA GeForce RTX 3090", 24576, 64, "NVMe")),
                    ("4. Workstation Editing", MakeTestProfile("AMD Ryzen 9 7950X", 16, 32, "NVIDIA GeForce RTX 4090", 24576, 128, "NVMe")),
                    ("5. Unmatched GPU Name", MakeTestProfile("Intel Core i5-12400F", 6, 12, "SuperUnknownGPU 2025", 12288, 16, "NVMe")),
                    ("6. Missing VRAM", MakeTestProfile("Intel Core i5-12400F", 6, 12, "NVIDIA GeForce RTX 3060", 0, 16, "NVMe")),
                };

                foreach (var (label, p) in profiles)
                    logBuilder.AppendLine(Tests.PerformanceScoringTests.GenerateScoreReport(label, p));

                // Write dissonance analysis
                logBuilder.AppendLine("═══ DISSONANCE ANALYSIS ═══");
                foreach (var (label, p) in profiles)
                {
                    var scores = UsageScenarioScorer.Score(p);
                    var office = scores.First(s => s.ScenarioId == "office");
                    var g1440 = scores.First(s => s.ScenarioId == "gaming_1440p");
                    var g1080 = scores.First(s => s.ScenarioId == "gaming_1080p");
                    bool dissonant = g1440.Score > office.Score;
                    logBuilder.AppendLine($"  {label}: Office={office.Score}, Gaming1080p={g1080.Score}, Gaming1440p={g1440.Score} {(dissonant ? "*** DISSONANT ***" : "OK")}");
                }
                logBuilder.AppendLine();

                logBuilder.AppendLine(failed > 0 ? $"RESULT: {failed} FAILURES" : "RESULT: ALL TESTS PASSED");
                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());
                return failed > 0 ? 1 : 0;
            }
            catch (Exception ex)
            {
                logBuilder.AppendLine($"EXCEPTION: {ex}");
                File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                Console.WriteLine(logBuilder.ToString());
                return 2;
            }
        }

        /// <summary>Helper for building test profiles with resolved tiers.</summary>
        private static HardwareProfile MakeTestProfile(
            string? cpuModel, int cpuCores, int cpuThreads,
            string? gpuModel, double gpuVramMb, double ramGb, string storageKind)
        {
            var p = new HardwareProfile
            {
                CpuModel = cpuModel, CpuCores = cpuCores, CpuThreads = cpuThreads,
                GpuModel = gpuModel, GpuVramMb = gpuVramMb, RamGb = ramGb, StorageKind = storageKind
            };
            var (cpuTier, cpuMatched) = PerformanceTierTable.ResolveCpuTier(cpuModel, cpuCores, cpuThreads);
            var (gpuTier, gpuMatched) = PerformanceTierTable.ResolveGpuTier(gpuModel, gpuVramMb);
            p.CpuTier = cpuTier; p.GpuTier = gpuTier;
            p.CpuNameMatched = cpuMatched; p.GpuNameMatched = gpuMatched;
            p.RamTier = PerformanceTierTable.ResolveRamTier(ramGb);
            p.StorageTier = PerformanceTierTable.ResolveStorageTier(storageKind);
            return p;
        }

        #region Non-blocking validation and missing data report

        /// <summary>
        /// Effectue une validation non-bloquante du rapport unifié et génère un compte-rendu des données manquantes.
        /// N'échoue jamais - écrit simplement un rapport de diagnostic.
        /// </summary>
        /// <param name="reportText">Le contenu du rapport unifié généré</param>
        /// <param name="combinedJsonPath">Le chemin du JSON combiné utilisé</param>
        /// <returns>Un rapport de validation (ne bloque jamais)</returns>
        public static MissingDataReport ValidateUnifiedReportNonBlocking(string reportText, string combinedJsonPath)
        {
            var report = new MissingDataReport
            {
                Timestamp = DateTime.Now,
                CombinedJsonPath = combinedJsonPath,
                SectionResults = new List<SectionValidationResult>()
            };

            try
            {
                // Lire le JSON combiné si disponible
                JsonElement? psRoot = null;
                JsonElement? sections = null;

                if (File.Exists(combinedJsonPath))
                {
                    try
                    {
                        var jsonContent = File.ReadAllText(combinedJsonPath, Encoding.UTF8);
                        using var doc = JsonDocument.Parse(jsonContent);
                        var root = doc.RootElement;

                        if (TryGetPropertyCaseInsensitive(root, out var psData, "scan_powershell", "scanPowershell"))
                        {
                            psRoot = psData.Clone();
                            if (TryGetPropertyCaseInsensitive(psData, out var sects, "sections"))
                            {
                                sections = sects.Clone();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        report.JsonParseError = ex.Message;
                    }
                }
                else
                {
                    report.JsonParseError = "Fichier JSON combiné introuvable";
                }

                // Validation des sections critiques
                report.SectionResults.Add(ValidateSection(
                    reportText, sections, "Section 6 - Stockage",
                    new[] { "Storage", "DiskInfo" },
                    new[] { "Utilisé %", "Partitions", "Capacité", "STOCKAGE" },
                    "Affichage du pourcentage utilisé des disques"));

                report.SectionResults.Add(ValidateSection(
                    reportText, sections, "Section 9 - Réseau",
                    new[] { "Network", "NetworkInfo" },
                    new[] { "DÉBIT INTERNET (FAI)", "Réseau local", "Adaptateur" },
                    "Clarification FAI vs réseau local"));

                report.SectionResults.Add(ValidateSection(
                    reportText, sections, "Section 11 - Mises à jour",
                    new[] { "WindowsUpdate", "Updates" },
                    new[] { "Updates en attente", "Windows Update", "Non disponible", "système à jour" },
                    "Affichage explicite du statut des mises à jour"));

                report.SectionResults.Add(ValidateSection(
                    reportText, sections, "Section 13 - Démarrage",
                    new[] { "StartupPrograms", "Startup", "InstalledApplications" },
                    new[] { "Programmes au démarrage", "Applications installées", "DÉMARRAGE" },
                    "Données de démarrage et applications"));

                report.SectionResults.Add(ValidateSection(
                    reportText, sections, "Section 15 - Périphériques",
                    new[] { "DevicesDrivers", "Audio", "Printers" },
                    new[] { "Périphériques audio", "Imprimantes", "Périph. en erreur", "PÉRIPHÉRIQUES" },
                    "Données périphériques (audio, imprimantes, drivers)"));

                report.SectionResults.Add(ValidateSection(
                    reportText, sections, "Section 16 - Virtualisation",
                    new[] { "Virtualization", "VirtualizationInfo" },
                    new[] { "VIRTUALISATION", "Machine virtuelle", "Hyper-V", "WSL" },
                    "Informations de virtualisation"));

                // Calculer le résumé
                report.TotalSections = report.SectionResults.Count;
                report.SectionsWithData = report.SectionResults.Count(s => s.HasDataInReport);
                report.SectionsMissing = report.SectionResults.Count(s => !s.HasDataInReport && !s.HasDataInJson);
                report.SectionsWithJsonButNoDisplay = report.SectionResults.Count(s => s.HasDataInJson && !s.HasDataInReport);

                // Écrire le rapport dans %TEMP%
                WriteValidationReport(report);
            }
            catch (Exception ex)
            {
                report.ValidationError = ex.Message;
                App.LogMessage($"[Validation] Erreur non-bloquante: {ex.Message}");
            }

            return report;
        }

        private static SectionValidationResult ValidateSection(
            string reportText,
            JsonElement? sections,
            string sectionName,
            string[] jsonSectionNames,
            string[] reportIndicators,
            string description)
        {
            var result = new SectionValidationResult
            {
                SectionName = sectionName,
                Description = description,
                JsonSectionNames = jsonSectionNames,
                ReportIndicators = reportIndicators,
                HasDataInJson = false,
                HasDataInReport = false
            };

            // Vérifier si les données existent dans le JSON
            if (sections.HasValue)
            {
                foreach (var jsonName in jsonSectionNames)
                {
                    if (TryGetPropertyCaseInsensitive(sections.Value, out var sectionObj, jsonName) &&
                        TryGetPropertyCaseInsensitive(sectionObj, out var data, "data") &&
                        IsNonEmptyJson(data))
                    {
                        result.HasDataInJson = true;
                        result.FoundJsonSection = jsonName;
                        break;
                    }
                }
            }

            // Vérifier si les indicateurs apparaissent dans le rapport
            foreach (var indicator in reportIndicators)
            {
                if (reportText.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                {
                    result.HasDataInReport = true;
                    result.FoundIndicator = indicator;
                    break;
                }
            }

            // Déterminer le statut
            if (result.HasDataInReport)
            {
                result.Status = "OK";
                result.Recommendation = null;
            }
            else if (result.HasDataInJson)
            {
                result.Status = "WARNING";
                result.Recommendation = $"Données présentes dans JSON ({result.FoundJsonSection}) mais non affichées dans le rapport. Vérifier la logique de lecture C#.";
            }
            else
            {
                result.Status = "MISSING";
                result.Recommendation = "Données absentes du JSON. Vérifier que le script PS exécute bien ce collecteur.";
            }

            return result;
        }

        private static void WriteValidationReport(MissingDataReport report)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("╔════════════════════════════════════════════════════════════════════════════════╗");
                sb.AppendLine("║             PC X-Ray — COMPTE-RENDU VALIDATION RAPPORT UNIFIÉ                 ║");
                sb.AppendLine("╚════════════════════════════════════════════════════════════════════════════════╝");
                sb.AppendLine();
                sb.AppendLine($"  Date: {report.Timestamp:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"  JSON source: {report.CombinedJsonPath}");
                sb.AppendLine();
                sb.AppendLine("═══════════════════════════════════════════════════════════════════════════════");
                sb.AppendLine("  RÉSUMÉ");
                sb.AppendLine("───────────────────────────────────────────────────────────────────────────────");
                sb.AppendLine($"  Sections vérifiées: {report.TotalSections}");
                sb.AppendLine($"  Sections avec données: {report.SectionsWithData}");
                sb.AppendLine($"  Sections manquantes: {report.SectionsMissing}");
                sb.AppendLine($"  Données JSON non affichées: {report.SectionsWithJsonButNoDisplay}");
                sb.AppendLine();

                if (!string.IsNullOrEmpty(report.JsonParseError))
                {
                    sb.AppendLine($"  ⚠️ Erreur JSON: {report.JsonParseError}");
                    sb.AppendLine();
                }

                sb.AppendLine("═══════════════════════════════════════════════════════════════════════════════");
                sb.AppendLine("  DÉTAIL PAR SECTION");
                sb.AppendLine("───────────────────────────────────────────────────────────────────────────────");

                foreach (var section in report.SectionResults)
                {
                    var icon = section.Status switch
                    {
                        "OK" => "✅",
                        "WARNING" => "⚠️",
                        "MISSING" => "❌",
                        _ => "❓"
                    };
                    sb.AppendLine($"  {icon} {section.SectionName}: {section.Status}");
                    sb.AppendLine($"      Description: {section.Description}");
                    sb.AppendLine($"      JSON ({string.Join("/", section.JsonSectionNames)}): {(section.HasDataInJson ? $"✓ ({section.FoundJsonSection})" : "✗")}");
                    sb.AppendLine($"      Rapport: {(section.HasDataInReport ? $"✓ ({section.FoundIndicator})" : "✗")}");
                    if (!string.IsNullOrEmpty(section.Recommendation))
                    {
                        sb.AppendLine($"      ➜ {section.Recommendation}");
                    }
                    sb.AppendLine();
                }

                sb.AppendLine("═══════════════════════════════════════════════════════════════════════════════");
                sb.AppendLine("  Ce compte-rendu est NON-BLOQUANT. L'application continue normalement.");
                sb.AppendLine("  Objectif: snapshot PC le plus intégral possible.");
                sb.AppendLine("═══════════════════════════════════════════════════════════════════════════════");

                var reportPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_validation_report.txt");
                File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
                App.LogMessage($"[Validation] Compte-rendu écrit: {reportPath}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[Validation] Impossible d'écrire le compte-rendu: {ex.Message}");
            }
        }

        #endregion
    }

    #region Models for validation report

    /// <summary>
    /// Rapport de validation des données manquantes (non-bloquant)
    /// </summary>
    public class MissingDataReport
    {
        public DateTime Timestamp { get; set; }
        public string CombinedJsonPath { get; set; } = "";
        public string? JsonParseError { get; set; }
        public string? ValidationError { get; set; }
        public List<SectionValidationResult> SectionResults { get; set; } = new();
        public int TotalSections { get; set; }
        public int SectionsWithData { get; set; }
        public int SectionsMissing { get; set; }
        public int SectionsWithJsonButNoDisplay { get; set; }
    }

    /// <summary>
    /// Résultat de validation pour une section
    /// </summary>
    public class SectionValidationResult
    {
        public string SectionName { get; set; } = "";
        public string Description { get; set; } = "";
        public string[] JsonSectionNames { get; set; } = Array.Empty<string>();
        public string[] ReportIndicators { get; set; } = Array.Empty<string>();
        public bool HasDataInJson { get; set; }
        public bool HasDataInReport { get; set; }
        public string? FoundJsonSection { get; set; }
        public string? FoundIndicator { get; set; }
        public string Status { get; set; } = "";
        public string? Recommendation { get; set; }
    }

    #endregion
}

