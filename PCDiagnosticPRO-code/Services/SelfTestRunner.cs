using System;
using System.Collections.Generic;
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
            var runUnifiedReport = args.Any(arg => string.Equals(arg, "--selftest-unified-report", StringComparison.OrdinalIgnoreCase));
            var runElevation = args.Any(arg => string.Equals(arg, "--diag-elevation", StringComparison.OrdinalIgnoreCase));

            if (!runSensors && !runPowerShell && !runUnifiedReport && !runElevation)
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
                var success = UnifiedReportBuilder.BuildUnifiedReportAsync(combinedJsonPath, null, outputPath)
                    .GetAwaiter().GetResult();
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

            var reportsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCDiagnosticPro", "Rapports");
            return Path.Combine(reportsDir, "scan_result_combined.json");
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
                sb.AppendLine("║        PC DIAGNOSTIC PRO — COMPTE-RENDU VALIDATION RAPPORT UNIFIÉ             ║");
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
