using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCDiagnosticPro.DiagnosticsSignals.Collectors;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// PARTIE 3: Génère le rapport TXT UNIFIÉ final avec 15 sections.
    /// Chaque section contient un tableau unique user-friendly.
    /// Plus de tableaux secondaires.
    /// </summary>
    public static class UnifiedReportBuilder
    {
        private const string SEPARATOR = "════════════════════════════════════════════════════════════════════════════════";
        private const string SUBSEPARATOR = "────────────────────────────────────────────────────────────────────────────────";

        /// <summary>
        /// Génère le rapport TXT unifié depuis le JSON combiné - 15 sections
        /// </summary>
        public static async Task<bool> BuildUnifiedReportAsync(
            string combinedJsonPath,
            string? originalTxtPath,
            string outputPath,
            HealthReport? healthReport = null)
        {
            try
            {
                var sb = new StringBuilder();
                HardwareSensorsResult? sensors = null;
                JsonElement? psData = null;
                JsonElement? combinedRoot = null;
                JsonElement? diagnosticSnapshot = null;

                // 1. Lire le JSON combiné
                if (File.Exists(combinedJsonPath))
                {
                    var jsonContent = await File.ReadAllTextAsync(combinedJsonPath, Encoding.UTF8);
                    using var doc = JsonDocument.Parse(jsonContent);
                    combinedRoot = doc.RootElement.Clone();
                    var root = doc.RootElement;

                    // Chercher capteurs C# avec fallback snake_case → camelCase
                    JsonElement sensorsElement = default;
                    if (TryGetPropertyRobust(root, out sensorsElement, "sensors_csharp", "sensorsCsharp"))
                    {
                        try
                        {
                            sensors = JsonSerializer.Deserialize<HardwareSensorsResult>(sensorsElement.GetRawText());
                        }
                        catch (Exception ex)
                        {
                            App.LogMessage($"[UnifiedReport] Erreur désérialisation capteurs: {ex.Message}");
                        }
                    }

                    // Chercher données PS
                    JsonElement psElement = default;
                    if (TryGetPropertyRobust(root, out psElement, "scan_powershell", "scanPowershell"))
                    {
                        psData = psElement.Clone();
                    }
                    
                    // Chercher diagnostic_snapshot
                    JsonElement snapshotElement = default;
                    if (TryGetPropertyRobust(root, out snapshotElement, "diagnostic_snapshot", "diagnosticSnapshot"))
                    {
                        diagnosticSnapshot = snapshotElement.Clone();
                    }
                    
                    // FIX E: Debug logging to %TEMP% for report generation diagnostics
                    LogReportDataAvailability(root, psData, sensors, combinedJsonPath);
                }

                // === GÉNÉRATION DES 15 SECTIONS ===
                
                // Section 1: Résumé global
                BuildSection1_ResumeGlobal(sb, healthReport, sensors);

                // Section 2: Infos générales
                BuildSection2_InfosGenerales(sb, psData, healthReport);

                // Section 3: Matériel principal (Hardware)
                BuildSection3_MaterielPrincipal(sb, psData, sensors);

                // Section 4: Performance activité
                BuildSection4_PerformanceActivite(sb, psData, combinedRoot, healthReport);

                // Section 5: Mémoire RAM
                BuildSection5_MemoireRam(sb, psData);

                // Section 6: Stockage et Disques
                BuildSection6_StockageDisques(sb, psData, sensors, combinedRoot);

                // Section 7: Températures et Refroidissement
                BuildSection7_Temperatures(sb, sensors, psData);

                // Section 8: Batterie et Alimentation
                BuildSection8_Batterie(sb, psData);

                // Section 9: Réseau et Internet
                BuildSection9_Reseau(sb, psData, combinedRoot);

                // Section 10: Sécurité
                BuildSection10_Securite(sb, psData);

                // Section 11: Mises à jour
                BuildSection11_MisesAJour(sb, psData, diagnosticSnapshot, combinedRoot);

                // Section 12: Pilotes (Drivers)
                BuildSection12_Pilotes(sb, psData, combinedRoot);

                // Section 13: Démarrage et Applications
                BuildSection13_Demarrage(sb, psData, diagnosticSnapshot);

                // Section 14: Santé système et Erreurs
                BuildSection14_SanteSysteme(sb, psData, healthReport, combinedRoot);

                // Section 15: Périphériques
                BuildSection15_Peripheriques(sb, psData, diagnosticSnapshot);

                // Section 16: Virtualisation (NEW)
                BuildSection16_Virtualisation(sb, psData, diagnosticSnapshot);

                // Section 17: Limitations et données manquantes
                BuildSection17_Limitations(sb, healthReport);

                // Section 18: Couverture des données PS -> rapport unifié
                BuildSection18_DataCoverage(sb, psData);

                // Bloc PS brut (extrait lisible)
                BuildPowerShellRawBlock(sb, psData, diagnosticSnapshot);

                // Footer
                BuildFooter(sb);

                // Écrire le fichier
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var normalizedReport = TextEncodingNormalizer.NormalizeIfCorrupted(sb.ToString());
                await File.WriteAllTextAsync(outputPath, normalizedReport, Encoding.UTF8);
                App.LogMessage($"[UnifiedReport] TXT unifié généré: {outputPath}");

                // === VALIDATION: Vérifier que le rapport unifié est un SUPERSET du PS brut ===
                await ValidateReportCompletenessAsync(normalizedReport, originalTxtPath, combinedRoot, combinedJsonPath);

                return true;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[UnifiedReport] ERREUR: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Valide que le rapport unifié contient au moins autant d'info que le PS brut.
        /// Loggue une erreur si des données PS sont manquantes.
        /// </summary>
        /// <param name="combinedRoot">Root du JSON combiné déjà lu (évite une seconde lecture disque).</param>
        /// <param name="combinedJsonPath">Chemin du JSON (pour ValidateUnifiedReportNonBlocking).</param>
        private static Task ValidateReportCompletenessAsync(string unifiedContent, string? psTxtPath, JsonElement? combinedRoot, string combinedJsonPath)
        {
            try
            {
                var missingCategories = new List<string>();
                
                // 1. Vérifier les catégories clés présentes dans PS JSON (utilise le root déjà chargé)
                if (combinedRoot.HasValue)
                {
                    var root = combinedRoot.Value;
                    
                    // Chercher psData
                    JsonElement psData = default;
                    if (TryGetPropertyRobust(root, out psData, "scan_powershell", "scanPowershell") && 
                        psData.TryGetProperty("sections", out var sectionsEl))
                    {
                        foreach (var sectionName in GetSectionsWithData(sectionsEl))
                        {
                            var map = MapPsSectionToUnifiedSection(sectionName);
                            if (!map.Represented)
                            {
                                missingCategories.Add(sectionName);
                            }
                        }
                    }
                }
                
                // Loguer les résultats
                if (missingCategories.Count > 0)
                {
                    App.LogMessage($"[VALIDATION] ⚠️ ATTENTION: {missingCategories.Count} section(s) PS sans mapping explicite vers le rapport unifié:");
                    foreach (var cat in missingCategories)
                    {
                        App.LogMessage($"  - {cat}");
                    }
                }
                else
                {
                    App.LogMessage("[VALIDATION] ✅ Rapport unifié complet - toutes les données PS sont représentées");
                }
                
                // FIX: Appeler la validation non-bloquante avec compte-rendu détaillé
                _ = SelfTestRunner.ValidateUnifiedReportNonBlocking(unifiedContent, combinedJsonPath);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[VALIDATION] Erreur validation: {ex.Message}");
            }
            return Task.CompletedTask;
        }
        
        /// <summary>
        /// Détermine le mapping d'une section PowerShell vers une section du rapport unifié.
        /// Cette table est utilisée à la fois pour l'affichage de couverture et la validation.
        /// </summary>
        private static (bool Represented, string UnifiedSection, string Notes) MapPsSectionToUnifiedSection(string psSectionName)
        {
            var mappings = new Dictionary<string, (string UnifiedSection, string Notes)>(StringComparer.OrdinalIgnoreCase)
            {
                { "OS", ("2", "Infos générales") },
                { "OSInfo", ("2", "Infos générales") },
                { "MachineIdentity", ("2", "Infos générales") },
                { "SystemInfo", ("2", "Infos générales") },
                { "CPU", ("3/4/7", "Matériel + activité + températures") },
                { "GPU", ("3/7", "Matériel + températures") },
                { "Memory", ("5", "Mémoire RAM") },
                { "Storage", ("6", "Stockage et disques") },
                { "Temperatures", ("7", "Températures et refroidissement") },
                { "Battery", ("8", "Batterie et alimentation") },
                { "Power", ("8", "Batterie et alimentation") },
                { "Network", ("9", "Réseau et Internet") },
                { "NetworkLatency", ("9", "Réseau et Internet") },
                { "Security", ("10", "Sécurité") },
                { "WindowsUpdate", ("11", "Mises à jour") },
                { "DevicesDrivers", ("12/15", "Pilotes + périphériques") },
                { "InstalledApplications", ("13", "Démarrage et applications") },
                { "StartupPrograms", ("13", "Démarrage et applications") },
                { "Services", ("13", "Démarrage et applications") },
                { "EventLogs", ("14", "Santé système et erreurs") },
                { "HealthChecks", ("14", "Santé système et erreurs") },
                { "ReliabilityHistory", ("14", "Santé système et erreurs") },
                { "PerformanceCounters", ("4", "Performance activité") },
                { "DynamicSignals", ("4", "Performance activité") },
                { "Processes", ("4/13", "Performance + applications") },
                { "Audio", ("15", "Périphériques") },
                { "Printers", ("15", "Périphériques") },
                { "Virtualization", ("16", "Virtualisation") }
            };
            
            if (mappings.TryGetValue(psSectionName, out var map))
            {
                return (true, map.UnifiedSection, map.Notes);
            }

            return (false, "-", "Section non mappée");
        }

        private static List<string> GetSectionsWithData(JsonElement sectionsEl)
        {
            var result = new List<string>();
            if (sectionsEl.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var sectionProp in sectionsEl.EnumerateObject())
            {
                var dataElement = sectionProp.Value;
                if (sectionProp.Value.ValueKind == JsonValueKind.Object &&
                    sectionProp.Value.TryGetProperty("data", out var nestedData))
                {
                    dataElement = nestedData;
                }

                if (dataElement.ValueKind != JsonValueKind.Null &&
                    dataElement.ValueKind != JsonValueKind.Undefined)
                {
                    result.Add(sectionProp.Name);
                }
            }

            return result;
        }

        #region Section 1: Résumé global

        private static void BuildSection1_ResumeGlobal(StringBuilder sb, HealthReport? healthReport, HardwareSensorsResult? sensors)
        {
            sb.AppendLine(SEPARATOR);
            sb.AppendLine("                    PC DIAGNOSTIC PRO - RAPPORT UNIFIÉ");
            sb.AppendLine(SEPARATOR);
            sb.AppendLine();
            sb.AppendLine("  ▶ SECTION 1 : RÉSUMÉ GLOBAL");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            var rows = new List<(string field, string value)>();

            // Score et statut
            if (healthReport != null)
            {
                if (healthReport.InsufficientDataForDiagnostic)
                {
                    rows.Add(("Score santé global", "N/A"));
                    rows.Add(("Statut", "❌ ERREUR COLLECTE - DONNÉES INSUFFISANTES POUR DIAGNOSTIC"));
                }
                else
                {
                    var emoji = healthReport.GlobalScore >= 90 ? "✅" :
                                healthReport.GlobalScore >= 70 ? "⚠️" :
                                healthReport.GlobalScore >= 50 ? "🔶" : "❌";
                    var status = healthReport.GlobalScore >= 90 ? "OK" :
                                 healthReport.GlobalScore >= 70 ? "Avertissement" :
                                 healthReport.GlobalScore >= 50 ? "Dégradé" : "Critique";

                    rows.Add(("Score santé global", $"{healthReport.GlobalScore}/100 (Grade {healthReport.Grade})"));
                    rows.Add(("Statut", $"{emoji} {status}"));
                }

                // Points clés (3-5 premiers findings)
                if (healthReport.Recommendations.Count > 0)
                {
                    rows.Add(("", "")); // Ligne vide
                    rows.Add(("Points clés", ""));
                    var count = 0;
                    foreach (var rec in healthReport.Recommendations.Take(5))
                    {
                        count++;
                        var icon = rec.Priority == HealthSeverity.Critical ? "🔴" :
                                   rec.Priority == HealthSeverity.Degraded ? "🟠" :
                                   rec.Priority == HealthSeverity.Warning ? "🟡" : "🟢";
                        rows.Add(($"  {count}.", $"{icon} {rec.Title}"));
                    }
                }
            }
            else
            {
                rows.Add(("Score santé global", "Non calculé"));
                rows.Add(("Statut", "Données insuffisantes"));
            }

            WriteTable(sb, rows);
            sb.AppendLine();
        }

        #endregion

        #region Section 17: Limitations et données manquantes

        /// <summary>
        /// Section 17: Limitations - liste consolidée des erreurs, données manquantes et fiabilité.
        /// </summary>
        private static void BuildSection17_Limitations(StringBuilder sb, Models.HealthReport? healthReport)
        {
            sb.AppendLine("  ▶ SECTION 17 : LIMITATIONS ET DONNÉES MANQUANTES");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            if (healthReport == null)
            {
                sb.AppendLine("  Rapport de santé non disponible.");
                sb.AppendLine();
                return;
            }

            // Collection status
            sb.AppendLine($"  Statut de collecte : {healthReport.CollectionStatus}");
            sb.AppendLine($"  Score de confiance : {healthReport.ConfidenceModel?.ConfidenceScore ?? 0}/100 ({healthReport.ConfidenceModel?.ConfidenceLevel ?? "N/A"})");
            sb.AppendLine($"  Score de fiabilité données : {healthReport.DataReliabilityScore}/100");
            sb.AppendLine($"  Erreurs collecteur : {healthReport.CollectorErrorsLogical}");
            sb.AppendLine();

            // Errors with French explanations
            if (healthReport.Errors.Count > 0)
            {
                sb.AppendLine("  ERREURS DÉTECTÉES :");
                sb.AppendLine();
                int num = 1;
                foreach (var error in healthReport.Errors)
                {
                    var code = string.IsNullOrWhiteSpace(error.Code) ? "UNKNOWN" : error.Code.ToUpperInvariant();
                    var explanation = GetLimitationExplanation(error);
                    sb.AppendLine($"  {num}) {code}");
                    sb.AppendLine($"     {explanation}");
                    if (!string.IsNullOrWhiteSpace(error.Section))
                        sb.AppendLine($"     Section : {error.Section}");
                    sb.AppendLine();
                    num++;
                }
            }
            else
            {
                sb.AppendLine("  Aucune erreur de collecte détectée.");
                sb.AppendLine();
            }

            // Missing data
            if (healthReport.MissingData.Count > 0)
            {
                sb.AppendLine("  DONNÉES MANQUANTES :");
                sb.AppendLine();
                foreach (var item in healthReport.MissingData)
                {
                    var parts = item.Split(';');
                    var name = parts.Length > 0 ? parts[0].Trim() : item;
                    sb.AppendLine($"  - {name}");
                    for (int i = 1; i < parts.Length; i++)
                    {
                        var detail = parts[i].Trim();
                        if (!string.IsNullOrWhiteSpace(detail))
                            sb.AppendLine($"    Détails : {detail}");
                    }
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("  Aucune donnée manquante.");
                sb.AppendLine();
            }

            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();
        }

        /// <summary>
        /// Section 18: matrice explicite de couverture des sections PowerShell.
        /// Permet d'identifier immédiatement les sections présentes mais non mappées.
        /// </summary>
        private static void BuildSection18_DataCoverage(StringBuilder sb, JsonElement? psData)
        {
            sb.AppendLine("  ▶ SECTION 18 : COUVERTURE DES DONNÉES (PS → RAPPORT)");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            if (!psData.HasValue || !psData.Value.TryGetProperty("sections", out var sectionsEl) || sectionsEl.ValueKind != JsonValueKind.Object)
            {
                sb.AppendLine("  Sections PowerShell non disponibles.");
                sb.AppendLine();
                return;
            }

            var rows = new List<(string field, string value)>();
            foreach (var sectionName in GetSectionsWithData(sectionsEl).OrderBy(s => s))
            {
                var map = MapPsSectionToUnifiedSection(sectionName);
                var status = map.Represented ? "OK" : "NON MAPPÉ";
                rows.Add((sectionName, $"{status} | Section unifiée: {map.UnifiedSection} | {map.Notes}"));
            }

            if (rows.Count == 0)
            {
                sb.AppendLine("  Aucune section PowerShell exploitable détectée.");
                sb.AppendLine();
                return;
            }

            WriteTable(sb, rows);
            sb.AppendLine();
        }

        private static string GetLimitationExplanation(Models.ScanErrorInfo error)
        {
            var code = (error.Code ?? "").ToUpperInvariant();
            var msg = (error.Message ?? "").Trim();
            if (code.Contains("WMI") || msg.Contains("WMI", StringComparison.OrdinalIgnoreCase))
                return "Échec WMI (Windows Management Instrumentation). Service indisponible ou droits insuffisants.";
            if (code.Contains("TIMEOUT") || msg.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                return "Délai d'attente dépassé. L'opération a pris trop de temps.";
            if (code.Contains("ACCESS") || code.Contains("PERMISSION"))
                return "Accès refusé. Droits administrateur requis.";
            if (string.IsNullOrWhiteSpace(msg) || msg.Equals("Unknown error", StringComparison.OrdinalIgnoreCase))
                return "Erreur inattendue. La cause n'a pas pu être identifiée.";
            return msg;
        }

        #endregion

        #region Bloc PowerShell (brut)

        private static void BuildPowerShellRawBlock(StringBuilder sb, JsonElement? psData, JsonElement? diagnosticSnapshot)
        {
            sb.AppendLine("  ▶ DONNÉES POWERSHELL (BRUT)");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            if (!psData.HasValue)
            {
                sb.AppendLine("  Données PowerShell : Non disponibles");
                sb.AppendLine();
                return;
            }

            var sections = RenderIfPresent(psData, new[] { "sections" });
            if (!sections.HasValue || sections.Value.ValueKind != JsonValueKind.Object)
            {
                sb.AppendLine("  Sections PowerShell : Non disponibles");
                sb.AppendLine();
                return;
            }

            var keySections = new[]
            {
                "WindowsUpdate", "StartupPrograms", "InstalledApplications",
                "DevicesDrivers", "Printers", "Audio"
            };

            foreach (var sectionName in keySections)
            {
                sb.AppendLine($"  [{sectionName}]");
                var data = RenderIfPresent(psData, new[] { "sections", sectionName, "data" });
                if (!data.HasValue)
                {
                    sb.AppendLine("    (section absente)");
                    sb.AppendLine();
                    continue;
                }

                AppendJsonSnippet(sb, data.Value, 8);
                sb.AppendLine();
            }
        }

        private static void AppendJsonSnippet(StringBuilder sb, JsonElement data, int maxLines)
        {
            int lines = 0;

            if (data.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in data.EnumerateObject())
                {
                    if (lines++ >= maxLines)
                    {
                        sb.AppendLine("    ...");
                        break;
                    }
                    sb.AppendLine($"    {prop.Name}: {FormatJsonValue(prop.Value)}");
                }
                return;
            }

            if (data.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine($"    [Array] {data.GetArrayLength()} item(s)");
                foreach (var item in data.EnumerateArray().Take(Math.Min(5, maxLines - 1)))
                {
                    sb.AppendLine($"    - {FormatJsonValue(item)}");
                }
                return;
            }

            sb.AppendLine($"    {FormatJsonValue(data)}");
        }

        private static string FormatJsonValue(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String:
                    return el.GetString() ?? "";
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return el.GetRawText();
                case JsonValueKind.Object:
                    if (TryGetPropertyRobust(el, out var nameEl, "name", "Name", "displayName", "DisplayName"))
                    {
                        var name = nameEl.ValueKind == JsonValueKind.String ? nameEl.GetString() : nameEl.ToString();
                        return $"{{name: {name}}}";
                    }
                    return $"{{object {el.EnumerateObject().Count()} keys}}";
                case JsonValueKind.Array:
                    return $"[Array {el.GetArrayLength()}]";
                default:
                    return el.ToString();
            }
        }

        #endregion

        #region Section 2: Infos générales

        private static void BuildSection2_InfosGenerales(StringBuilder sb, JsonElement? psData, HealthReport? healthReport)
        {
            sb.AppendLine("  ▶ SECTION 2 : INFOS GÉNÉRALES");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            var rows = new List<(string field, string value)>();
            rows.Add(("Nom PC", Environment.MachineName));
            rows.Add(("Utilisateur", Environment.UserName));
            rows.Add(("Date/heure scan", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));

            // Uptime
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            rows.Add(("Uptime", $"{uptime.Days}j {uptime.Hours}h {uptime.Minutes}m"));

            // OS info
            rows.Add(("Version OS", Environment.OSVersion.ToString()));
            
            if (psData.HasValue)
            {
                var os = GetNestedString(psData.Value, "sections", "OSInfo", "data", "Caption");
                if (!string.IsNullOrEmpty(os)) rows.Add(("Édition Windows", os));
                
                var build = GetNestedString(psData.Value, "sections", "OSInfo", "data", "BuildNumber");
                if (!string.IsNullOrEmpty(build)) rows.Add(("Build", build));
            }

            rows.Add(("Architecture", Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit"));
            rows.Add(("Mode Admin", AdminHelper.IsRunningAsAdmin() ? "OUI" : "NON"));

            WriteTable(sb, rows);
            sb.AppendLine();
        }

        #endregion

        #region Section 3: Matériel principal

        private static void BuildSection3_MaterielPrincipal(StringBuilder sb, JsonElement? psData, HardwareSensorsResult? sensors)
        {
            sb.AppendLine("  ▶ SECTION 3 : MATÉRIEL PRINCIPAL (HARDWARE)");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            var rows = new List<(string field, string value)>();

            if (psData.HasValue)
            {
                // Machine model
                var model = GetNestedString(psData.Value, "sections", "SystemInfo", "data", "Model");
                var manufacturer = GetNestedString(psData.Value, "sections", "SystemInfo", "data", "Manufacturer");
                if (!string.IsNullOrEmpty(model))
                    rows.Add(("Modèle machine", $"{manufacturer} {model}".Trim()));

                // Motherboard
                var mbProduct = GetNestedString(psData.Value, "sections", "SystemInfo", "data", "MotherboardProduct");
                if (!string.IsNullOrEmpty(mbProduct))
                    rows.Add(("Carte mère", mbProduct));

                // BIOS
                var biosVersion = GetNestedString(psData.Value, "sections", "BIOSInfo", "data", "SMBIOSBIOSVersion");
                if (!string.IsNullOrEmpty(biosVersion))
                    rows.Add(("Version BIOS", biosVersion));

                // CPU
                var cpuName = GetNestedString(psData.Value, "sections", "CPUInfo", "data", "Name");
                var cpuCores = GetNestedInt(psData.Value, "sections", "CPUInfo", "data", "NumberOfCores");
                var cpuThreads = GetNestedInt(psData.Value, "sections", "CPUInfo", "data", "NumberOfLogicalProcessors");
                if (!string.IsNullOrEmpty(cpuName))
                    rows.Add(("CPU", $"{cpuName} ({cpuCores}C/{cpuThreads}T)"));
            }

            // GPU from C# sensors
            if (sensors?.Gpu != null && sensors.Gpu.Name.Available)
            {
                rows.Add(("GPU", sensors.Gpu.Name.Value ?? "Détecté"));
                if (sensors.Gpu.VramTotalMB.Available)
                    rows.Add(("VRAM totale", $"{sensors.Gpu.VramTotalMB.Value:F0} MB"));
            }
            else if (psData.HasValue)
            {
                var gpuName = GetNestedString(psData.Value, "sections", "GPUInfo", "data", "Name");
                if (!string.IsNullOrEmpty(gpuName))
                    rows.Add(("GPU", gpuName));
            }

            // RAM total
            if (psData.HasValue)
            {
                var totalRam = GetNestedDouble(psData.Value, "sections", "MemoryInfo", "data", "TotalPhysicalMemoryGB");
                if (totalRam > 0)
                    rows.Add(("RAM totale", $"{totalRam:F1} GB"));
            }

            if (rows.Count == 0)
                rows.Add(("Matériel", "Données non disponibles"));

            WriteTable(sb, rows);
            sb.AppendLine();
        }

        #endregion

        #region Section 4: Performance activité

        private static void BuildSection4_PerformanceActivite(StringBuilder sb, JsonElement? psData, JsonElement? combinedRoot, HealthReport? healthReport)
        {
            sb.AppendLine("  ▶ SECTION 4 : PERFORMANCE ACTIVITÉ");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            var rows = new List<(string field, string value)>();

            // CPU - plusieurs sources : CPU.cpus, DynamicSignals.cpu, CPUInfo.LoadPercentage
            double cpuUsage = -1;
            if (psData.HasValue)
            {
                var cpuData = GetNestedElement(psData.Value, "sections", "CPU", "data");
                if (!cpuData.HasValue) cpuData = GetNestedElement(psData.Value, "sections", "CPUInfo", "data");
                if (cpuData.HasValue && cpuData.Value.TryGetProperty("cpus", out var cpusEl) && cpusEl.ValueKind == JsonValueKind.Array)
                {
                    var firstCpu = cpusEl.EnumerateArray().FirstOrDefault();
                    if (firstCpu.ValueKind != JsonValueKind.Undefined)
                    {
                        var maxSpeedMhz = firstCpu.TryGetProperty("maxClockSpeed", out var mcs) ? SafeGetDouble(mcs, -1) : -1;
                        var currentSpeedMhz = firstCpu.TryGetProperty("currentClockSpeed", out var ccs) ? SafeGetDouble(ccs, maxSpeedMhz) : maxSpeedMhz;
                        if (maxSpeedMhz > 0)
                        {
                            rows.Add(("Fréquence CPU (max)", $"{maxSpeedMhz / 1000:F2} GHz"));
                            if (currentSpeedMhz > 0 && Math.Abs(currentSpeedMhz - maxSpeedMhz) > 1)
                                rows.Add(("Fréquence CPU (instantanée)", $"{currentSpeedMhz / 1000:F2} GHz"));
                        }
                        cpuUsage = firstCpu.TryGetProperty("currentLoad", out var cl) ? SafeGetDouble(cl, -1) : -1;
                    }
                }
                if (cpuUsage < 0) cpuUsage = GetNestedDouble(psData.Value, "sections", "CPUInfo", "data", "LoadPercentage");
                if (cpuUsage < 0)
                {
                    var dynSignals = GetNestedElement(psData.Value, "sections", "DynamicSignals", "data");
                    if (dynSignals.HasValue && dynSignals.Value.TryGetProperty("cpu", out var cpuEl))
                        cpuUsage = cpuEl.TryGetProperty("average", out var avg) ? SafeGetDouble(avg, -1) : -1;
                }
                if (cpuUsage >= 0)
                    rows.Add(("Charge CPU", $"{cpuUsage:F0}%"));
            }

            // RAM usage - plusieurs sources : Memory (totalGB, freeGB, usedPercent), MemoryInfo, DynamicSignals
            if (psData.HasValue)
            {
                double usedRam = -1;
                var memData = GetNestedElement(psData.Value, "sections", "Memory", "data");
                if (memData.HasValue)
                {
                    usedRam = GetNestedDouble(psData.Value, "sections", "Memory", "data", "usedPercent");
                    if (usedRam < 0)
                    {
                        var totalRam = GetNestedDouble(psData.Value, "sections", "Memory", "data", "totalGB");
                        var freeGB = GetNestedDouble(psData.Value, "sections", "Memory", "data", "freeGB");
                        if (totalRam > 0 && freeGB >= 0)
                            usedRam = ((totalRam - freeGB) / totalRam) * 100;
                    }
                }
                if (usedRam < 0)
                {
                    var totalRam = GetNestedDouble(psData.Value, "sections", "MemoryInfo", "data", "TotalPhysicalMemoryGB");
                    var usedGB = GetNestedDouble(psData.Value, "sections", "MemoryInfo", "data", "UsedMemoryGB");
                    if (totalRam > 0 && usedGB >= 0)
                        usedRam = (usedGB / totalRam) * 100;
                }
                if (usedRam < 0)
                {
                    var dynMem = GetNestedElement(psData.Value, "sections", "DynamicSignals", "data");
                    if (dynMem.HasValue && dynMem.Value.TryGetProperty("memory", out var memEl))
                        usedRam = memEl.TryGetProperty("usedPercent", out var up) ? SafeGetDouble(up, -1) : -1;
                }
                if (usedRam >= 0 && !double.IsNaN(usedRam))
                    rows.Add(("Utilisation RAM", $"{usedRam:F0}%"));
            }

            // Disk activity - PerformanceCounters ou DynamicSignals
            if (psData.HasValue)
            {
                var diskQueue = GetNestedDouble(psData.Value, "sections", "PerformanceCounters", "data", "diskQueueLength");
                if (diskQueue < 0) diskQueue = GetNestedDouble(psData.Value, "sections", "PerformanceCounters", "data", "DiskQueueLength");
                if (diskQueue >= 0 && !double.IsNaN(diskQueue))
                    rows.Add(("File d'attente disque", $"{diskQueue:F1}"));
                var diskRead = GetNestedDouble(psData.Value, "sections", "PerformanceCounters", "data", "diskReadMBs");
                var diskWrite = GetNestedDouble(psData.Value, "sections", "PerformanceCounters", "data", "diskWriteMBs");
                if (diskRead >= 0 || diskWrite >= 0)
                    rows.Add(("Activité disque", $"R:{diskRead:F1} MB/s W:{diskWrite:F1} MB/s"));
            }

            // Network throughput - network_diagnostics ou DynamicSignals
            if (combinedRoot.HasValue && TryGetPropertyRobust(combinedRoot.Value, out var netDiag, "network_diagnostics", "networkDiagnostics"))
            {
                JsonElement tp = default;
                if (TryGetPropertyRobust(netDiag, out tp, "throughput", "Throughput"))
                {
                    JsonElement dm = default;
                    if (TryGetPropertyRobust(tp, out dm, "downloadMbpsMedian", "DownloadMbpsMedian"))
                    {
                        var mbps = SafeGetDouble(dm, 0);
                        if (mbps > 0) rows.Add(("Débit réseau (test HTTP)", $"{mbps:F1} Mbps"));
                    }
                }
            }
            if (rows.All(r => !r.field.Contains("réseau")) && psData.HasValue)
            {
                var dynData = GetNestedElement(psData.Value, "sections", "DynamicSignals", "data");
                if (dynData.HasValue && dynData.Value.TryGetProperty("network", out var netEl) && netEl.TryGetProperty("throughputMbps", out var tm))
                {
                    var mbps = SafeGetDouble(tm, 0);
                    if (mbps >= 0) rows.Add(("Débit réseau (samples)", $"{mbps:F1} Mbps"));
                }
            }

            // Performance timeseries (C#) - add-only: moyennes et pics sur la fenêtre d'échantillonnage
            if (combinedRoot.HasValue && combinedRoot.Value.TryGetProperty("performance_timeseries_summary", out var pts) && pts.ValueKind == JsonValueKind.Object)
            {
                var interval = pts.TryGetProperty("interval_seconds", out var isec) ? isec.GetInt32() : 0;
                if (pts.TryGetProperty("cpu_percent", out var cpuAgg) && cpuAgg.ValueKind == JsonValueKind.Object)
                {
                    var avg = cpuAgg.TryGetProperty("avg", out var a) ? SafeGetDouble(a, -1) : -1;
                    var max = cpuAgg.TryGetProperty("max", out var m) ? SafeGetDouble(m, -1) : -1;
                    if (avg >= 0 || max >= 0) rows.Add(("CPU (moy/pic sur " + interval + " s)", $"moy: {avg:F0}% / pic: {max:F0}%"));
                }
                if (pts.TryGetProperty("memory_available_mb", out var memAgg) && memAgg.ValueKind == JsonValueKind.Object)
                {
                    var min = memAgg.TryGetProperty("min", out var mn) ? SafeGetDouble(mn, -1) : -1;
                    var avg = memAgg.TryGetProperty("avg", out var a) ? SafeGetDouble(a, -1) : -1;
                    if (min >= 0 || avg >= 0) rows.Add(("RAM dispo (min/moy MB)", $"min: {min:F0} / moy: {avg:F0}"));
                }
                if (pts.TryGetProperty("disk_read_bytes_per_sec", out var dr) && dr.ValueKind == JsonValueKind.Object && pts.TryGetProperty("disk_write_bytes_per_sec", out var dw) && dw.ValueKind == JsonValueKind.Object)
                {
                    var rAvg = dr.TryGetProperty("avg", out var ra) ? SafeGetDouble(ra, -1) : -1;
                    var wAvg = dw.TryGetProperty("avg", out var wa) ? SafeGetDouble(wa, -1) : -1;
                    if (rAvg >= 0 || wAvg >= 0) rows.Add(("Disque R/W (moy B/s)", $"R: {rAvg:F0} / W: {wAvg:F0}"));
                }
                if (pts.TryGetProperty("disk_queue_length", out var dq) && dq.ValueKind == JsonValueKind.Object)
                {
                    var max = dq.TryGetProperty("max", out var mx) ? SafeGetDouble(mx, -1) : -1;
                    if (max >= 0) rows.Add(("File d'attente disque (pic)", $"{max:F1}"));
                }
                if (pts.TryGetProperty("network_bytes_per_sec", out var netAgg) && netAgg.ValueKind == JsonValueKind.Object)
                {
                    var avg = netAgg.TryGetProperty("avg", out var a) ? SafeGetDouble(a, -1) : -1;
                    if (avg >= 0) rows.Add(("Réseau (moy B/s)", $"{avg:F0}"));
                }
            }

            WriteTable(sb, rows);
            sb.AppendLine();

            // Top 5 CPU et Top 5 RAM - process_telemetry (PascalCase/camelCase) ou scan_powershell.sections.Processes/DynamicSignals
            // FIX A: Multiple aliases + case-insensitive lookup
            JsonElement? topCpuArr = null;
            JsonElement? topMemArr = null;
            bool usedCSharpProcessFallback = false;
            
            // Source 1: C# ProcessTelemetry (root level)
            if (combinedRoot.HasValue)
            {
                JsonElement procTelemetry = default;
                if (TryGetPropertyRobust(combinedRoot.Value, out procTelemetry, "process_telemetry", "processTelemetry", "ProcessTelemetry"))
                {
                    // Support multiple naming conventions: TopByCpu, topByCpu, topCpuProcesses, TopCpu, etc.
                    if (TryGetPropertyRobust(procTelemetry, out var topCpu, "TopByCpu", "topByCpu", "topCpuProcesses", "TopCpu", "topCpu", "processesCpu"))
                    { topCpuArr = topCpu; usedCSharpProcessFallback = true; }
                    if (TryGetPropertyRobust(procTelemetry, out var topMem, "TopByMemory", "topByMemory", "topRamProcesses", "TopMemory", "topMemory", "processesMemory", "topRam"))
                    { topMemArr = topMem; usedCSharpProcessFallback = true; }
                }
            }
            
            // Source 2: PS DynamicSignals.data
            if (!topCpuArr.HasValue && !topMemArr.HasValue && psData.HasValue)
            {
                var dynData = GetNestedElement(psData.Value, "sections", "DynamicSignals", "data");
                if (dynData.HasValue)
                {
                    if (TryGetPropertyRobust(dynData.Value, out var tc, "topCpu", "TopCpu", "topCpuProcesses", "TopByCpu"))
                        topCpuArr = tc;
                    if (TryGetPropertyRobust(dynData.Value, out var tm, "topMemory", "TopMemory", "topRamProcesses", "TopByMemory", "topRam"))
                        topMemArr = tm;
                }
            }
            
            // Source 3: PS Processes.data
            if (!topCpuArr.HasValue && psData.HasValue)
            {
                var procData = GetNestedElement(psData.Value, "sections", "Processes", "data");
                if (procData.HasValue)
                {
                    // Handle both direct array and object with nested array
                    if (procData.Value.ValueKind == JsonValueKind.Array)
                    {
                        topCpuArr = procData;
                    }
                    else if (TryGetPropertyRobust(procData.Value, out var tc, "topCpu", "TopCpu", "processes", "items", "list"))
                    {
                        topCpuArr = tc;
                    }
                }
            }
            
            // Source 4: PS ProcessList.data (alternative section name)
            if (!topCpuArr.HasValue && psData.HasValue)
            {
                var procListData = GetNestedElement(psData.Value, "sections", "ProcessList", "data");
                if (procListData.HasValue)
                {
                    if (procListData.Value.ValueKind == JsonValueKind.Array)
                        topCpuArr = procListData;
                    else if (TryGetPropertyRobust(procListData.Value, out var pl, "processes", "items", "list", "topCpu"))
                        topCpuArr = pl;
                }
            }

            if (topCpuArr.HasValue && topCpuArr.Value.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine("  Top 5 Processus (CPU):");
                if (usedCSharpProcessFallback)
                    sb.AppendLine("  (source : collecte C# fallback)");
                sb.AppendLine("  ┌────────────────────────────┬──────────┬────────────┐");
                sb.AppendLine("  │ Processus                  │ CPU %    │ RAM (MB)   │");
                sb.AppendLine("  ├────────────────────────────┼──────────┼────────────┤");
                int count = 0;
                foreach (var proc in topCpuArr.Value.EnumerateArray())
                {
                    if (count++ >= 5) break;
                    var name = TryGetString(proc, "Name", "name") ?? "?";
                    var cpuPct = proc.TryGetProperty("CpuPercent", out var c) ? SafeGetDouble(c, 0) : proc.TryGetProperty("cpuPercent", out var c2) ? SafeGetDouble(c2, 0) : 0;
                    var ram = proc.TryGetProperty("WorkingSetMB", out var r) ? SafeGetDouble(r, 0) : proc.TryGetProperty("workingSetMB", out var r2) ? SafeGetDouble(r2, 0) : proc.TryGetProperty("memoryMB", out var m) ? SafeGetDouble(m, 0) : 0;
                    name = name.Length > 26 ? name.Substring(0, 23) + "..." : name;
                    sb.AppendLine($"  │ {name,-26} │ {cpuPct,7:F1}% │ {ram,9:F1} │");
                }
                sb.AppendLine("  └────────────────────────────┴──────────┴────────────┘");
                sb.AppendLine();
            }
            if (topMemArr.HasValue && topMemArr.Value.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine("  Top 5 Processus (RAM):");
                if (usedCSharpProcessFallback)
                    sb.AppendLine("  (source : collecte C# fallback)");
                sb.AppendLine("  ┌────────────────────────────┬────────────┬──────────┐");
                sb.AppendLine("  │ Processus                  │ RAM (MB)   │ CPU %    │");
                sb.AppendLine("  ├────────────────────────────┼────────────┼──────────┤");
                int count = 0;
                foreach (var proc in topMemArr.Value.EnumerateArray())
                {
                    if (count++ >= 5) break;
                    var name = TryGetString(proc, "Name", "name") ?? "?";
                    var ram = proc.TryGetProperty("WorkingSetMB", out var r) ? SafeGetDouble(r, 0) : proc.TryGetProperty("workingSetMB", out var r2) ? SafeGetDouble(r2, 0) : proc.TryGetProperty("memoryMB", out var m) ? SafeGetDouble(m, 0) : 0;
                    var cpuPct = proc.TryGetProperty("CpuPercent", out var c) ? SafeGetDouble(c, 0) : proc.TryGetProperty("cpuPercent", out var c2) ? SafeGetDouble(c2, 0) : 0;
                    name = name.Length > 26 ? name.Substring(0, 23) + "..." : name;
                    sb.AppendLine($"  │ {name,-26} │ {ram,9:F1} │ {cpuPct,7:F1}% │");
                }
                sb.AppendLine("  └────────────────────────────┴────────────┴──────────┘");
                sb.AppendLine();
            }
            if ((!topCpuArr.HasValue || topCpuArr.Value.ValueKind != JsonValueKind.Array) && (!topMemArr.HasValue || topMemArr.Value.ValueKind != JsonValueKind.Array))
            {
                sb.AppendLine("  Top processus : Données non disponibles");
                sb.AppendLine();
            }
        }

        #endregion

        #region Section 5: Mémoire RAM

        private static void BuildSection5_MemoireRam(StringBuilder sb, JsonElement? psData)
        {
            sb.AppendLine("  ▶ SECTION 5 : MÉMOIRE RAM");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            var rows = new List<(string field, string value)>();
            bool foundData = false;
            double totalRam = 0, usedRam = 0, availRam = 0;
            double totalVirt = 0, availVirt = 0;
            double totalPage = 0, availPage = 0;
            double commitPct = 0;

            // FIX 3: Try multiple paths for RAM data
            if (psData.HasValue)
            {
                totalRam = GetNestedDouble(psData.Value, "sections", "MemoryInfo", "data", "TotalPhysicalMemoryGB");
                usedRam = GetNestedDouble(psData.Value, "sections", "MemoryInfo", "data", "UsedMemoryGB");
                availRam = GetNestedDouble(psData.Value, "sections", "MemoryInfo", "data", "AvailableMemoryGB");
                if (totalRam <= 0)
                {
                    totalRam = GetNestedDouble(psData.Value, "sections", "Memory", "data", "totalGB");
                    availRam = GetNestedDouble(psData.Value, "sections", "Memory", "data", "freeGB");
                    if (totalRam > 0 && availRam >= 0) usedRam = totalRam - availRam;
                }
                if (totalRam <= 0)
                {
                    totalRam = GetNestedDouble(psData.Value, "sections", "Memory", "TotalPhysicalMemoryGB");
                    usedRam = GetNestedDouble(psData.Value, "sections", "Memory", "UsedMemoryGB");
                    availRam = GetNestedDouble(psData.Value, "sections", "Memory", "AvailableMemoryGB");
                }
                commitPct = GetNestedDouble(psData.Value, "sections", "PerformanceCounters", "data", "CommittedBytesPercent");
                if (commitPct <= 0) commitPct = GetNestedDouble(psData.Value, "PerformanceCounters", "CommittedBytesPercent");

                if (totalRam > 0)
                    foundData = true;
            }

            // C# fallback pour données complètes (barres mémoire physique + virtuelle + pagefile)
            MemoryInfoResult? memResult = null;
            if (!foundData || totalVirt == 0)
            {
                try
                {
                    var memCollector = new MemoryInfoCollector();
                    memResult = memCollector.CollectAsync(CancellationToken.None).GetAwaiter().GetResult();
                    if (memResult.Available && memResult.TotalGB > 0)
                    {
                        if (!foundData)
                        {
                            foundData = true;
                            totalRam = memResult.TotalGB;
                            usedRam = memResult.UsedGB;
                            availRam = memResult.AvailableGB;
                            commitPct = memResult.CommitPercent;
                        }
                        totalVirt = memResult.TotalVirtualGB;
                        availVirt = memResult.AvailableVirtualGB;
                        totalPage = memResult.TotalPageFileGB;
                        availPage = memResult.AvailablePageFileGB;
                        if (commitPct <= 0) commitPct = memResult.CommitPercent;
                    }
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[UnifiedReport] RAM C# fallback failed: {ex.Message}");
                }
            }

            // Barres mémoire vivantes : chaque catégorie avec sa valeur
            if (foundData)
            {
                sb.AppendLine("  Répartition mémoire (instantanée) :");
                sb.AppendLine("  ┌─────────────────────────────┬────────────┬────────────┬──────────┐");
                sb.AppendLine("  │ Catégorie                   │ Valeur     │ Total      │ %        │");
                sb.AppendLine("  ├─────────────────────────────┼────────────┼────────────┼──────────┤");
                
                if (totalRam > 0)
                {
                    var usedPct = totalRam > 0 ? (usedRam / totalRam * 100) : 0;
                    sb.AppendLine($"  │ Physique : Utilisée          │ {usedRam,8:F2} GB │ {totalRam,8:F2} GB │ {usedPct,6:F0}% │");
                    sb.AppendLine($"  │ Physique : Disponible        │ {availRam,8:F2} GB │ {totalRam,8:F2} GB │ {(availRam/totalRam*100),6:F0}% │");
                }
                if (totalVirt > 0)
                {
                    var usedVirt = totalVirt - availVirt;
                    sb.AppendLine($"  │ Virtuelle : Utilisée         │ {usedVirt,8:F2} GB │ {totalVirt,8:F2} GB │ {(usedVirt/totalVirt*100),6:F0}% │");
                    sb.AppendLine($"  │ Virtuelle : Disponible       │ {availVirt,8:F2} GB │ {totalVirt,8:F2} GB │ {(availVirt/totalVirt*100),6:F0}% │");
                }
                if (totalPage > 0)
                {
                    var usedPage = totalPage - availPage;
                    sb.AppendLine($"  │ Fichier pagination : Utilisé │ {usedPage,8:F2} GB │ {totalPage,8:F2} GB │ {commitPct,6:F0}% │");
                    sb.AppendLine($"  │ Fichier pagination : Libre   │ {availPage,8:F2} GB │ {totalPage,8:F2} GB │ {(availPage/totalPage*100),6:F0}% │");
                }
                sb.AppendLine("  └─────────────────────────────┴────────────┴────────────┴──────────┘");
                sb.AppendLine();
                if (commitPct > 85)
                    sb.AppendLine("  ⚠️ Alerte : Commit > 85% - Pression mémoire élevée");
                if (memResult != null)
                    sb.AppendLine($"  Source : C# ({memResult.Source})");
                sb.AppendLine();
            }
            else
            {
                rows.Add(("Mémoire", "Données non disponibles"));
                WriteTable(sb, rows);
            }
            sb.AppendLine();
        }

        #endregion

        #region Section 6: Stockage et Disques

        private static void BuildSection6_StockageDisques(StringBuilder sb, JsonElement? psData, HardwareSensorsResult? sensors, JsonElement? combinedRoot = null)
        {
            sb.AppendLine("  ▶ SECTION 6 : STOCKAGE ET DISQUES");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            // Disques logiques (partitions) - PS keys: Storage, DiskInfo
            // FIX: Ajout colonne "Utilisé %" pour afficher le pourcentage utilisé
            if (psData.HasValue && psData.Value.TryGetProperty("sections", out var sections))
            {
                JsonElement diskData = default;
                if (sections.TryGetProperty("Storage", out var storageEl) && storageEl.TryGetProperty("data", out diskData)) { }
                else if (sections.TryGetProperty("DiskInfo", out var diskInfo) && diskInfo.TryGetProperty("data", out diskData)) { }
                
                if (diskData.ValueKind == JsonValueKind.Array || diskData.ValueKind == JsonValueKind.Object)
                {
                    sb.AppendLine("  Partitions:");
                    sb.AppendLine("  ┌───────┬────────────┬────────────┬────────────┬───────────┬──────────┐");
                    sb.AppendLine("  │ Lettre│ Capacité   │ Libre      │ Utilisé    │ Utilisé % │ Alerte   │");
                    sb.AppendLine("  ├───────┼────────────┼────────────┼────────────┼───────────┼──────────┤");

                    // PS Storage has data.volumes[] with letter, totalGB, freeGB, usedPercent
                    JsonElement volumesEl = default;
                    if (diskData.ValueKind == JsonValueKind.Array)
                        volumesEl = diskData;
                    else if (diskData.TryGetProperty("volumes", out volumesEl) || diskData.TryGetProperty("Volumes", out volumesEl)) { }
                    
                    if (volumesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var vol in volumesEl.EnumerateArray())
                        {
                            var letter = vol.TryGetProperty("letter", out var l) ? l.GetString() ?? "?" : 
                                         vol.TryGetProperty("DeviceID", out var l2) ? l2.GetString() ?? "?" : "?";
                            var sizeGb = vol.TryGetProperty("totalGB", out var s) ? SafeGetDouble(s, 0) : 
                                         vol.TryGetProperty("SizeGB", out var s2) ? SafeGetDouble(s2, 0) : 0;
                            var freeGb = vol.TryGetProperty("freeGB", out var f) ? SafeGetDouble(f, 0) : 
                                         vol.TryGetProperty("FreeSpaceGB", out var f2) ? SafeGetDouble(f2, 0) : 0;
                            var usedGb = sizeGb - freeGb;
                            // FIX: Calcul du pourcentage utilisé
                            var usedPct = sizeGb > 0 ? ((usedGb / sizeGb) * 100) : 0;
                            // Essayer d'utiliser usedPercent du PS si disponible
                            if (vol.TryGetProperty("usedPercent", out var upEl) || vol.TryGetProperty("UsedPercent", out upEl))
                            {
                                usedPct = SafeGetDouble(upEl, usedPct);
                            }
                            var freePct = sizeGb > 0 ? (freeGb / sizeGb * 100) : 0;
                            var alert = freePct < 15 ? "⚠️ <15%" : "OK";

                            sb.AppendLine($"  │ {letter,-5} │ {sizeGb,8:F1} GB │ {freeGb,8:F1} GB │ {usedGb,8:F1} GB │ {usedPct,7:F1} %  │ {alert,-8} │");
                        }
                    }
                    else if (diskData.ValueKind == JsonValueKind.Object && !diskData.TryGetProperty("volumes", out _))
                    {
                        var letter = diskData.TryGetProperty("letter", out var l) ? l.GetString() ?? "?" : 
                                     diskData.TryGetProperty("DeviceID", out var l2) ? l2.GetString() ?? "?" : "?";
                        var sizeGb = diskData.TryGetProperty("totalGB", out var s) ? s.GetDouble() : 
                                     diskData.TryGetProperty("SizeGB", out var s2) ? s2.GetDouble() : 0;
                        var freeGb = diskData.TryGetProperty("freeGB", out var f) ? f.GetDouble() : 
                                     diskData.TryGetProperty("FreeSpaceGB", out var f2) ? f2.GetDouble() : 0;
                        var usedGb = sizeGb - freeGb;
                        // FIX: Calcul du pourcentage utilisé
                        var usedPct = sizeGb > 0 ? ((usedGb / sizeGb) * 100) : 0;
                        if (diskData.TryGetProperty("usedPercent", out var upEl) || diskData.TryGetProperty("UsedPercent", out upEl))
                        {
                            if (upEl.ValueKind == JsonValueKind.Number)
                                usedPct = upEl.GetDouble();
                        }
                        var freePct = sizeGb > 0 ? (freeGb / sizeGb * 100) : 0;
                        var alert = freePct < 15 ? "⚠️ <15%" : "OK";
                        sb.AppendLine($"  │ {letter,-5} │ {sizeGb,8:F1} GB │ {freeGb,8:F1} GB │ {usedGb,8:F1} GB │ {usedPct,7:F1} %  │ {alert,-8} │");
                    }

                    sb.AppendLine("  └───────┴────────────┴────────────┴────────────┴───────────┴──────────┘");
                    sb.AppendLine();
                }
            }

            // SMART status
            sb.AppendLine("  Statut SMART:");
            var smartRows = new List<(string field, string value)>();
            
            if (sensors?.Disks != null && sensors.Disks.Count > 0)
            {
                foreach (var disk in sensors.Disks)
                {
                    var name = disk.Name.Value ?? "Disque";
                    smartRows.Add((name, "Détecté par capteurs C#"));
                }
            }
            else
            {
                smartRows.Add(("SMART", "Données non disponibles via capteurs"));
            }

            WriteTable(sb, smartRows);
            sb.AppendLine();

            // SMART détaillé (C#) - attributs par disque (add-only)
            if (combinedRoot.HasValue && combinedRoot.Value.TryGetProperty("smart_attributes", out var smartArr) && smartArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var disk in smartArr.EnumerateArray())
                {
                    var instanceName = disk.TryGetProperty("instance_name", out var inEl) ? inEl.GetString() ?? "?" : disk.TryGetProperty("instanceName", out inEl) ? inEl.GetString() ?? "?" : "?";
                    var predictFailure = disk.TryGetProperty("predict_failure", out var pf) && pf.ValueKind == JsonValueKind.True || disk.TryGetProperty("predictFailure", out pf) && pf.ValueKind == JsonValueKind.True;
                    sb.AppendLine($"  SMART - {instanceName}: PredictFailure = {(predictFailure ? "Oui" : "Non")}");
                    if (disk.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
                    {
                        sb.AppendLine("  ┌─────────────────────────────────────┬─────────┬───────┬───────┬─────────┬───────────┐");
                        sb.AppendLine("  │ Attribut                            │ Current │ Worst │ Raw   │ Seuil   │           │");
                        sb.AppendLine("  ├─────────────────────────────────────┼─────────┼───────┼───────┼─────────┼───────────┤");
                        foreach (var a in attrs.EnumerateArray())
                        {
                            var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : a.TryGetProperty("Name", out n) ? n.GetString() ?? "?" : "?";
                            var cur = a.TryGetProperty("current", out var c) ? c.GetInt32() : a.TryGetProperty("Current", out c) ? c.GetInt32() : 0;
                            var worst = a.TryGetProperty("worst", out var w) ? w.GetInt32() : a.TryGetProperty("Worst", out w) ? w.GetInt32() : 0;
                            var raw = a.TryGetProperty("raw", out var r) ? r.GetUInt64() : a.TryGetProperty("Raw", out r) ? r.GetUInt64() : 0UL;
                            var thresh = a.TryGetProperty("threshold", out var t) ? t.GetInt32() : a.TryGetProperty("Threshold", out t) ? t.GetInt32() : 0;
                            name = name.Length > 35 ? name.Substring(0, 32) + "..." : name;
                            sb.AppendLine($"  │ {name,-35} │ {cur,7} │ {worst,5} │ {raw,5} │ {thresh,7} │           │");
                        }
                        sb.AppendLine("  └─────────────────────────────────────┴─────────┴───────┴───────┴─────────┴───────────┘");
                    }
                    sb.AppendLine();
                }
            }
        }

        #endregion

        #region Section 7: Températures et Refroidissement

        private static void BuildSection7_Temperatures(StringBuilder sb, HardwareSensorsResult? sensors, JsonElement? psData)
        {
            sb.AppendLine("  ▶ SECTION 7 : TEMPÉRATURES ET REFROIDISSEMENT");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            var rows = new List<(string field, string value)>();

            if (sensors != null)
            {
                // CPU Temperature
                var cpuValid = MetricValidation.ValidateCpuTemp(sensors.Cpu.CpuTempC);
                if (cpuValid.Validity == MetricValidity.Valid)
                    rows.Add(("Temp CPU", $"{cpuValid.Value:F1}°C"));
                else
                    rows.Add(("Temp CPU", "Non disponible sur ce matériel (capteur non exposé par firmware)"));

                // GPU Temperature
                var gpuValid = MetricValidation.ValidateGpuTemp(sensors.Gpu.GpuTempC);
                if (gpuValid.Validity == MetricValidity.Valid)
                {
                    rows.Add(("Temp GPU", $"{gpuValid.Value:F1}°C"));
                    if (gpuValid.Value > 83)
                        rows.Add(("⚠️ Alerte GPU", "Température > 83°C - Surchauffe possible"));
                }
                else
                    rows.Add(("Temp GPU", $"Non disponible ({gpuValid.Reason ?? "capteur absent"})"));

                // Disk Temperatures
                if (sensors.Disks.Count > 0)
                {
                    double maxDiskTemp = 0;
                    foreach (var disk in sensors.Disks)
                    {
                        if (disk.TempC.Available && disk.TempC.Value > maxDiskTemp)
                            maxDiskTemp = disk.TempC.Value;
                    }

                    if (maxDiskTemp > 0)
                    {
                        rows.Add(("Temp max disques", $"{maxDiskTemp:F0}°C"));
                        if (maxDiskTemp > 60)
                            rows.Add(("⚠️ Alerte disque", "Température > 60°C - Surchauffe possible"));
                    }
                    else
                    {
                        rows.Add(("Temp disques", "Non disponible"));
                    }
                }

                // Statut surchauffe global
                var cpuTemp = cpuValid.Validity == MetricValidity.Valid ? cpuValid.Value : 0;
                var gpuTemp = gpuValid.Validity == MetricValidity.Valid ? gpuValid.Value : 0;
                
                if (cpuTemp > 90 || gpuTemp > 90)
                    rows.Add(("Statut thermique", "🔴 CRITIQUE - Surchauffe détectée"));
                else if (cpuTemp > 80 || gpuTemp > 83)
                    rows.Add(("Statut thermique", "🟠 ATTENTION - Températures élevées"));
                else if (cpuTemp > 0 || gpuTemp > 0)
                    rows.Add(("Statut thermique", "✅ Normal"));
            }
            else
            {
                rows.Add(("Températures", "Capteurs non disponibles"));
            }

            WriteTable(sb, rows);
            sb.AppendLine();
        }

        #endregion

        #region Section 8: Batterie et Alimentation

        private static void BuildSection8_Batterie(StringBuilder sb, JsonElement? psData)
        {
            sb.AppendLine("  ▶ SECTION 8 : BATTERIE ET ALIMENTATION");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            var rows = new List<(string field, string value)>();

            if (psData.HasValue)
            {
                var batteryData = GetNestedElement(psData.Value, "sections", "Battery", "data");
                if (!batteryData.HasValue) batteryData = GetNestedElement(psData.Value, "sections", "BatteryInfo", "data");
                
                if (batteryData.HasValue && batteryData.Value.ValueKind != JsonValueKind.Null)
                {
                    var status = batteryData.Value.TryGetProperty("BatteryStatus", out var bs) ? bs.GetString() : null;
                    var chargeRemaining = batteryData.Value.TryGetProperty("EstimatedChargeRemaining", out var ecr) ? ecr.GetInt32() : -1;
                    
                    if (!string.IsNullOrEmpty(status) || chargeRemaining >= 0)
                    {
                        rows.Add(("Batterie détectée", "Oui"));
                        if (!string.IsNullOrEmpty(status))
                            rows.Add(("État", status));
                        if (chargeRemaining >= 0)
                            rows.Add(("Charge restante", $"{chargeRemaining}%"));
                    }
                    else
                    {
                        rows.Add(("Batterie", "Pas de batterie détectée"));
                    }
                }
                else
                {
                    rows.Add(("Batterie", "Pas de batterie détectée"));
                }

                // Power plan - PS: PowerSettings.data.ActivePowerPlan
                var powerPlan = GetNestedString(psData.Value, "sections", "PowerSettings", "data", "ActivePowerPlan");
                if (string.IsNullOrEmpty(powerPlan)) powerPlan = GetNestedString(psData.Value, "sections", "PowerInfo", "data", "ActivePowerPlan");
                if (!string.IsNullOrEmpty(powerPlan))
                    rows.Add(("Mode alimentation", powerPlan));
            }
            else
            {
                rows.Add(("Alimentation", "Données non disponibles"));
            }

            WriteTable(sb, rows);
            sb.AppendLine();
        }

        #endregion

        #region Section 9: Réseau et Internet

        private static void BuildSection9_Reseau(StringBuilder sb, JsonElement? psData, JsonElement? combinedRoot)
        {
            sb.AppendLine("  ▶ SECTION 9 : RÉSEAU ET INTERNET");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            var rows = new List<(string field, string value)>();

            // FIX: Clarifier "Débit Internet (FAI)" vs "Réseau local / configuration"
            // Résultats complets du test HTTP (speedtest.tele2.net / proof.ovh.net) = vitesse INTERNET
            if (combinedRoot.HasValue && TryGetPropertyRobust(combinedRoot.Value, out var netDiagEl, "network_diagnostics", "networkDiagnostics"))
            {
                sb.AppendLine("  ═ DÉBIT INTERNET (FAI) ═");
                sb.AppendLine();
                
                if (netDiagEl.TryGetProperty("throughput", out var throughput))
                {
                    var downMbps = throughput.TryGetProperty("downloadMbpsMedian", out var dm) ? dm.GetDouble() : -1;
                    var upMbps = throughput.TryGetProperty("uploadMbpsMedian", out var um) ? um.GetDouble() : -1;
                    
                    sb.AppendLine("  Débit mesuré (vers Internet) :");
                    if (downMbps > 0 && !double.IsNaN(downMbps))
                    {
                        sb.AppendLine($"    Débit descendant : {downMbps:F1} Mbps");
                        var verdict = downMbps >= 100 ? "Excellente" : downMbps >= 20 ? "Bonne" : downMbps >= 5 ? "Moyenne" : "Lente";
                        sb.AppendLine($"    Verdict          : {verdict}");
                    }
                    if (upMbps > 0 && !double.IsNaN(upMbps))
                        sb.AppendLine($"    Débit montant    : {upMbps:F1} Mbps");
                    if (downMbps <= 0 && throughput.TryGetProperty("reason", out var tr))
                        sb.AppendLine($"    Non disponible   : {tr.GetString() ?? "-"}");
                    sb.AppendLine("  Source : speedtest.tele2.net / proof.ovh.net (test FAI)");
                    sb.AppendLine();
                }
                
                sb.AppendLine("  Latence et qualité :");
                if (TryGetPropertyRobust(netDiagEl, out var lat, "overallLatencyMsP50", "OverallLatencyMsP50") && lat.GetDouble() > 0)
                    sb.AppendLine($"    Latence P50    : {lat.GetDouble():F1} ms");
                if (TryGetPropertyRobust(netDiagEl, out var lat95, "overallLatencyMsP95", "OverallLatencyMsP95") && lat95.GetDouble() > 0)
                    sb.AppendLine($"    Latence P95    : {lat95.GetDouble():F1} ms");
                if (TryGetPropertyRobust(netDiagEl, out var loss, "overallLossPercent", "OverallLossPercent") && loss.GetDouble() >= 0)
                    sb.AppendLine($"    Perte paquets  : {loss.GetDouble():F1}%");
                if (TryGetPropertyRobust(netDiagEl, out var jitter, "overallJitterMsP95", "OverallJitterMsP95") && jitter.GetDouble() > 0)
                    sb.AppendLine($"    Jitter P95     : {jitter.GetDouble():F2} ms");
                if (TryGetPropertyRobust(netDiagEl, out var gw, "gateway", "Gateway") && !string.IsNullOrEmpty(gw.GetString()))
                    sb.AppendLine($"    Gateway        : {gw.GetString()}");
                if (TryGetPropertyRobust(netDiagEl, out var dnsP95, "dnsP95Ms", "DnsP95Ms") && dnsP95.GetDouble() > 0)
                    sb.AppendLine($"    DNS P95        : {dnsP95.GetDouble():F0} ms");
                
                // Détails Ping par cible
                if (netDiagEl.TryGetProperty("internetTargets", out var targets) && targets.ValueKind == JsonValueKind.Array)
                {
                    sb.AppendLine();
                    sb.AppendLine("  Ping par cible :");
                    foreach (var t in targets.EnumerateArray())
                    {
                        var target = t.TryGetProperty("target", out var tg) ? tg.GetString() : null;
                        if (string.IsNullOrEmpty(target)) target = t.TryGetProperty("Target", out var tg2) ? tg2.GetString() : null;
                        var latVal = t.TryGetProperty("latencyMsP50", out var lp50) ? lp50.GetDouble() : 
                                     t.TryGetProperty("latencyMs", out var lm) ? lm.GetDouble() : -1;
                        var lossVal = t.TryGetProperty("lossPercent", out var lpv) ? lpv.GetDouble() : -1;
                        var ok = t.TryGetProperty("available", out var av) && av.GetBoolean();
                        if (!string.IsNullOrEmpty(target))
                            sb.AppendLine($"    {target} : {(ok ? $"{latVal:F1} ms" : "échec")}{(lossVal >= 0 ? $" | perte {lossVal:F1}%" : "")}");
                    }
                }
                sb.AppendLine();
            }

            // FIX: Clarifier "Réseau local / configuration" (infos adaptateur)
            // Basic network info from PS (sections.Network.data.adapters[] avec name, ip[], gateway[])
            if (psData.HasValue)
            {
                var netData = GetNestedElement(psData.Value, "sections", "Network", "data");
                if (!netData.HasValue) netData = GetNestedElement(psData.Value, "sections", "NetworkInfo", "data");
                
                if (netData.HasValue)
                {
                    rows.Add(("═ Réseau local / configuration ═", ""));
                    
                    // PS format: data.adapters[] with name, ip (array), gateway (array)
                    if (netData.Value.TryGetProperty("adapters", out var adaptersEl) && adaptersEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var adapter in adaptersEl.EnumerateArray())
                        {
                            var name = TryGetString(adapter, "name", "Description") ?? "Adaptateur";
                            string? ip = null;
                            string? gateway = null;
                            if (adapter.TryGetProperty("ip", out var ipProp))
                            {
                                if (ipProp.ValueKind == JsonValueKind.Array)
                                {
                                    var first = ipProp.EnumerateArray().FirstOrDefault();
                                    ip = first.ValueKind == JsonValueKind.String ? first.GetString() : null;
                                }
                                else if (ipProp.ValueKind == JsonValueKind.String)
                                    ip = ipProp.GetString();
                            }
                            if (adapter.TryGetProperty("gateway", out var gwProp))
                            {
                                if (gwProp.ValueKind == JsonValueKind.Array)
                                {
                                    var first = gwProp.EnumerateArray().FirstOrDefault();
                                    gateway = first.ValueKind == JsonValueKind.String ? first.GetString() : null;
                                }
                                else if (gwProp.ValueKind == JsonValueKind.String)
                                    gateway = gwProp.GetString();
                            }
                            
                            rows.Add(("Adaptateur actif", name));
                            if (!string.IsNullOrEmpty(ip)) rows.Add(("IP locale", ip));
                            if (!string.IsNullOrEmpty(gateway)) rows.Add(("Gateway", gateway));
                            break; // Premier adaptateur actif
                        }
                    }
                    // Format alternatif: tableau direct avec Description, NetConnectionStatus, IPAddress
                    else if (netData.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var adapter in netData.Value.EnumerateArray())
                        {
                            var status = adapter.TryGetProperty("NetConnectionStatus", out var s) ? s.GetInt32() : 0;
                            if (status != 2) continue;
                            var name = adapter.TryGetProperty("Description", out var d) ? d.GetString() : "Adaptateur";
                            var ip = adapter.TryGetProperty("IPAddress", out var ipProp) ? ipProp.GetString() : null;
                            var gateway = adapter.TryGetProperty("DefaultIPGateway", out var gw) ? gw.GetString() : null;
                            rows.Add(("Adaptateur actif", name ?? "Inconnu"));
                            if (!string.IsNullOrEmpty(ip)) rows.Add(("IP locale", ip));
                            if (!string.IsNullOrEmpty(gateway)) rows.Add(("Gateway", gateway));
                            break;
                        }
                    }
                }
            }

            // Network quality from diagnostic signals
            if (combinedRoot.HasValue)
            {
                JsonElement signals = default;
                if (TryGetPropertyRobust(combinedRoot.Value, out signals, "diagnostic_signals", "diagnosticSignals"))
                {
                    if (signals.TryGetProperty("networkQuality", out var netQuality) &&
                        netQuality.TryGetProperty("value", out var netValue))
                    {
                        var linkSpeed = netValue.TryGetProperty("linkSpeedMbps", out var ls) ? ls.GetInt64() : 0;
                        var verdict = netValue.TryGetProperty("connectionVerdict", out var v) ? v.GetString() : null;
                        var latency = netValue.TryGetProperty("latencyMsP95", out var lat) ? lat.GetDouble() : 0;
                        var loss = netValue.TryGetProperty("packetLossPercent", out var ploss) ? ploss.GetDouble() : 0;
                        var jitter = netValue.TryGetProperty("jitterMsP95", out var j) ? j.GetDouble() : 0;

                        rows.Add(("", "")); // Separator
                        rows.Add(("═ Test qualité local ═", ""));
                        if (linkSpeed > 0) rows.Add(("Vitesse lien", $"{linkSpeed} Mbps"));
                        if (latency > 0) rows.Add(("Latence P95", $"{latency:F1} ms"));
                        if (loss >= 0) rows.Add(("Perte paquets", $"{loss:F1}%"));
                        if (jitter > 0) rows.Add(("Jitter P95", $"{jitter:F1} ms"));

                        if (!string.IsNullOrEmpty(verdict))
                        {
                            var icon = verdict switch
                            {
                                "Excellent" => "✅",
                                "Bon" => "👍",
                                "Moyen" => "⚠️",
                                "Mauvais" => "❌",
                                _ => "❓"
                            };
                            rows.Add(("VERDICT CONNEXION", $"{icon} {verdict}"));
                        }

                        var reason = netValue.TryGetProperty("verdictReason", out var vr) ? vr.GetString() : null;
                        if (!string.IsNullOrEmpty(reason))
                            rows.Add(("Détails", reason));
                    }
                }
            }

            if (rows.Count == 0)
                rows.Add(("Réseau", "Données non disponibles"));

            WriteTable(sb, rows);
            
            sb.AppendLine();
        }

        #endregion

        #region Section 10: Sécurité

        private static void BuildSection10_Securite(StringBuilder sb, JsonElement? psData)
        {
            sb.AppendLine("  ▶ SECTION 10 : SÉCURITÉ");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            var rows = new List<(string field, string value)>();
            bool foundData = false;

            if (psData.HasValue)
            {
                // FIX 9: Chercher dans plusieurs chemins PS possibles
                var secPaths = new[]
                {
                    GetNestedElement(psData.Value, "sections", "SecurityInfo", "data"),
                    GetNestedElement(psData.Value, "sections", "Security", "data"),
                    GetNestedElement(psData.Value, "SecurityInfo"),
                    GetNestedElement(psData.Value, "Security"),
                    GetNestedElement(psData.Value, "sections", "SecurityInfo"),
                    GetNestedElement(psData.Value, "sections", "Security")
                };
                
                JsonElement? secData = null;
                foreach (var path in secPaths)
                {
                    if (path.HasValue && path.Value.ValueKind == JsonValueKind.Object)
                    {
                        secData = path;
                        break;
                    }
                }
                
                if (secData.HasValue)
                {
                    foundData = true;
                    
                    // Windows Defender - PS: defenderEnabled, defenderRTP
                    var defender = TryGetBool(secData.Value, "defenderEnabled", "defenderRTP", "WindowsDefenderEnabled", "DefenderEnabled", "Defender", "AMSIEnabled");
                    if (defender.HasValue)
                        rows.Add(("Windows Defender", defender.Value ? "✅ Actif" : "❌ Inactif"));
                    
                    // Pare-feu - PS: firewall = { Domain: bool, Private: bool, Public: bool }
                    bool? firewall = TryGetBool(secData.Value, "FirewallEnabled", "Firewall", "WindowsFirewall");
                    bool? fwDomain = null, fwPrivate = null, fwPublic = null;
                    if (secData.Value.TryGetProperty("firewall", out var fwObj))
                    {
                        fwDomain = TryGetBool(fwObj, "Domain");
                        fwPrivate = TryGetBool(fwObj, "Private");
                        fwPublic = TryGetBool(fwObj, "Public");
                        if (!firewall.HasValue && (fwDomain ?? fwPrivate ?? fwPublic).HasValue)
                            firewall = (fwDomain ?? false) || (fwPrivate ?? false) || (fwPublic ?? false);
                    }
                    if (!fwDomain.HasValue) fwDomain = TryGetBool(secData.Value, "FirewallDomainEnabled", "DomainFirewall");
                    if (!fwPrivate.HasValue) fwPrivate = TryGetBool(secData.Value, "FirewallPrivateEnabled", "PrivateFirewall");
                    if (!fwPublic.HasValue) fwPublic = TryGetBool(secData.Value, "FirewallPublicEnabled", "PublicFirewall");
                    if (firewall.HasValue)
                        rows.Add(("Pare-feu", firewall.Value ? "✅ Actif" : "❌ Inactif"));
                    if (fwDomain.HasValue || fwPrivate.HasValue || fwPublic.HasValue)
                    {
                        var status = $"Dom:{(fwDomain ?? false ? "✓" : "✗")} Priv:{(fwPrivate ?? false ? "✓" : "✗")} Pub:{(fwPublic ?? false ? "✓" : "✗")}";
                        rows.Add(("  Profils", status));
                    }
                    
                    // UAC - PS: uacEnabled
                    var uac = TryGetBool(secData.Value, "uacEnabled", "UACEnabled", "UAC");
                    if (uac.HasValue)
                        rows.Add(("UAC", uac.Value ? "✅ Actif" : "⚠️ Désactivé"));
                    
                    // Secure Boot
                    var secureBoot = TryGetBool(secData.Value, "SecureBootEnabled", "SecureBoot");
                    if (secureBoot.HasValue)
                        rows.Add(("Secure Boot", secureBoot.Value ? "✅ Actif" : "⚠️ Inactif"));
                    
                    // TPM
                    var tpm = TryGetBool(secData.Value, "TPMEnabled", "TPM", "TPMPresent", "TPMReady");
                    if (tpm.HasValue)
                        rows.Add(("TPM", tpm.Value ? "✅ Présent" : "❓ Non détecté"));
                    
                    // Version TPM si dispo
                    var tpmVersion = TryGetString(secData.Value, "TPMVersion", "TPMSpecVersion");
                    if (!string.IsNullOrEmpty(tpmVersion))
                        rows.Add(("  Version TPM", tpmVersion));
                    
                    // BitLocker
                    var bitlocker = TryGetBool(secData.Value, "BitLockerEnabled", "BitLocker");
                    if (bitlocker.HasValue)
                        rows.Add(("BitLocker", bitlocker.Value ? "✅ Actif" : "Non activé"));
                    
                    // Antivirus - PS: antivirusProducts[] array
                    var avName = TryGetString(secData.Value, "AntivirusName", "AVName", "ThirdPartyAV");
                    if (string.IsNullOrEmpty(avName) && secData.Value.TryGetProperty("antivirusProducts", out var avArr) && avArr.ValueKind == JsonValueKind.Array)
                    {
                        var first = avArr.EnumerateArray().FirstOrDefault();
                        avName = first.ValueKind == JsonValueKind.String ? first.GetString() : null;
                    }
                    if (!string.IsNullOrEmpty(avName) && !avName.Contains("Windows Defender", StringComparison.OrdinalIgnoreCase))
                        rows.Add(("Antivirus", avName));
                }
            }

            // RDP et SMBv1 depuis security_info_csharp (collecté par SecurityInfoCollector)
            try
            {
                if (psData.HasValue)
                {
                    var secCsharp = GetNestedElement(psData.Value, "security_info_csharp");
                    if (secCsharp.HasValue && secCsharp.Value.ValueKind == JsonValueKind.Object)
                    {
                        var rdp = TryGetBool(secCsharp.Value, "RdpEnabled");
                        if (rdp.HasValue)
                            rows.Add(("Bureau à distance (RDP)", rdp.Value ? "Activé (vérifier si nécessaire)" : "Désactivé"));
                        var smb1 = TryGetBool(secCsharp.Value, "Smb1Enabled");
                        if (smb1.HasValue)
                            rows.Add(("SMBv1", smb1.Value ? "⚠ Activé (risque de sécurité)" : "✅ Désactivé (recommandé)"));
                        foundData = true;
                    }
                }
            }
            catch { /* fallback: RDP/SMBv1 non disponibles */ }

            if (!foundData || rows.Count == 0)
                rows.Add(("Sécurité", "Données non disponibles"));

            WriteTable(sb, rows);
            sb.AppendLine();
        }

        #endregion

        #region Section 11: Mises à jour

        private static void BuildSection11_MisesAJour(StringBuilder sb, JsonElement? psData, JsonElement? diagnosticSnapshot, JsonElement? combinedRoot)
        {
            sb.AppendLine("  ▶ SECTION 11 : MISES À JOUR");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            var rows = new List<(string field, string value)>();
            bool foundData = false;
            bool renderedData = false;

            // 1) Snapshot (priority)
            if (diagnosticSnapshot.HasValue)
            {
                var snapUpdates = RenderIfPresent(diagnosticSnapshot, new[] { "psSummary", "updates" });
                if (snapUpdates.HasValue)
                {
                    foundData = true;
                    var pending = TryGetInt(snapUpdates.Value, "pendingCount", "PendingCount");
                    if (pending >= 0)
                    {
                        rows.Add(("Updates en attente", pending.ToString()));
                        renderedData = true;
                    }

                    var reboot = TryGetBool(snapUpdates.Value, "rebootRequired", "RebootRequired");
                    if (reboot.HasValue)
                    {
                        rows.Add(("Redémarrage requis", reboot.Value ? "⚠️ OUI" : "Non"));
                        renderedData = true;
                    }

                    var lastUpdate = TryGetString(snapUpdates.Value, "lastUpdate", "LastUpdate", "lastInstallDate", "LastInstallDate");
                    if (!string.IsNullOrEmpty(lastUpdate))
                    {
                        rows.Add(("Dernière mise à jour", lastUpdate));
                        renderedData = true;
                    }
                }
            }

            // 2) PS raw sections fallback
            if (!renderedData && psData.HasValue)
            {
                var updateData = RenderIfPresent(psData,
                    new[] { "sections", "WindowsUpdate", "data" },
                    new[] { "sections", "WindowsUpdateInfo", "data" },
                    new[] { "sections", "Updates", "data" },
                    new[] { "WindowsUpdate" },
                    new[] { "Updates" });

                if (updateData.HasValue)
                {
                    foundData = true;

                    if (updateData.Value.ValueKind == JsonValueKind.Object)
                    {
                        var pending = TryGetInt(updateData.Value, "pendingCount", "PendingCount", "PendingUpdatesCount", "Pending", "pending_count");
                        if (pending >= 0)
                        {
                            rows.Add(("Updates en attente", pending.ToString()));
                            renderedData = true;
                        }

                        var lastUpdate = TryGetString(updateData.Value, "lastUpdateDate", "LastUpdateDate", "lastInstallDate", "LastInstallDate",
                            "LastInstalled", "LastCheck", "lastCheck", "last_update_date");
                        if (!string.IsNullOrEmpty(lastUpdate))
                        {
                            rows.Add(("Dernière mise à jour", lastUpdate));
                            renderedData = true;
                        }

                        var errors = TryGetInt(updateData.Value, "failedCount", "FailedCount", "FailedUpdatesCount", "ErrorCount", "failed_count");
                        if (errors > 0)
                        {
                            rows.Add(("⚠️ Échecs récents", errors.ToString()));
                            renderedData = true;
                        }

                        var autoUpdate = TryGetBool(updateData.Value, "autoUpdateEnabled", "AutoUpdateEnabled", "AutoUpdate", "auto_update_enabled");
                        if (autoUpdate.HasValue)
                        {
                            rows.Add(("Mise à jour auto", autoUpdate.Value ? "Activée" : "Désactivée"));
                            renderedData = true;
                        }

                        var reboot = TryGetBool(updateData.Value, "rebootRequired", "RebootRequired", "RebootPending", "NeedsReboot", "reboot_required");
                        if (reboot.HasValue && !rows.Any(r => r.field.Contains("Redémarrage")))
                        {
                            rows.Add(("Redémarrage requis", reboot.Value ? "⚠️ OUI" : "Non"));
                            renderedData = true;
                        }

                        if (TryGetPropertyRobust(updateData.Value, out var pendingList, "pendingUpdates", "PendingUpdates", "updates", "Updates", "list"))
                        {
                            if (pendingList.ValueKind == JsonValueKind.Array)
                            {
                                var updateCount = pendingList.GetArrayLength();
                                if (updateCount > 0 && pending < 0)
                                {
                                    rows.Add(("Updates en attente", updateCount.ToString()));
                                    renderedData = true;
                                }
                            }
                        }
                    }
                    else if (updateData.Value.ValueKind == JsonValueKind.Array)
                    {
                        var updateCount = updateData.Value.GetArrayLength();
                        rows.Add(("Updates détectées", updateCount.ToString()));

                        int shown = 0;
                        foreach (var update in updateData.Value.EnumerateArray())
                        {
                            if (shown++ >= 3) break;
                            var title = TryGetString(update, "title", "Title", "name", "Name", "KB", "kb");
                            var date = TryGetString(update, "date", "Date", "installedOn", "InstalledOn");
                            if (!string.IsNullOrEmpty(title))
                            {
                                var displayTitle = title.Length > 50 ? title.Substring(0, 47) + "..." : title;
                                rows.Add(($"  • {displayTitle}", date ?? ""));
                            }
                        }
                        if (updateCount > 3)
                            rows.Add(("  ...", $"(+{updateCount - 3} autres)"));
                    }
                }

                // HealthChecks rebootRequired fallback
                var healthData = RenderIfPresent(psData, new[] { "sections", "HealthChecks", "data" });
                if (healthData.HasValue)
                {
                    foundData = true;
                    if (!rows.Any(r => r.field.Contains("Redémarrage")))
                    {
                        var reboot = TryGetBool(healthData.Value, "rebootRequired", "RebootRequired", "RebootPending", "NeedsReboot");
                        if (reboot.HasValue)
                        {
                            rows.Add(("Redémarrage requis", reboot.Value ? "⚠️ OUI" : "Non"));
                            renderedData = true;
                        }
                    }
                    if (healthData.Value.TryGetProperty("rebootReasons", out var reasons) && reasons.ValueKind == JsonValueKind.Array)
                    {
                        var reasonsList = reasons.EnumerateArray().Select(r => r.GetString()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                        if (reasonsList.Count > 0)
                        {
                            rows.Add(("  Raisons", string.Join(", ", reasonsList)));
                            renderedData = true;
                        }
                    }
                }
            }

            // 3) C# Windows Update fallback (combined JSON)
            if (!renderedData && combinedRoot.HasValue)
            {
                if (TryGetPropertyRobust(combinedRoot.Value, out var updatesCsharp, "updates_csharp", "updatesCsharp") &&
                    updatesCsharp.ValueKind == JsonValueKind.Object)
                {
                    foundData = true;
                    var available = TryGetBool(updatesCsharp, "available", "Available");
                    var pending = TryGetInt(updatesCsharp, "pendingCount", "PendingCount");
                    var reboot = TryGetBool(updatesCsharp, "rebootRequired", "RebootRequired");

                    if (available.HasValue && !available.Value)
                    {
                        rows.Add(("Windows Update (C#)", "Non disponible"));
                        renderedData = true;
                    }
                    else
                    {
                        if (pending >= 0)
                        {
                            rows.Add(("Updates en attente (C#)", pending.ToString()));
                            renderedData = true;
                        }
                        if (reboot.HasValue)
                        {
                            rows.Add(("Redémarrage requis", reboot.Value ? "⚠️ OUI" : "Non"));
                            renderedData = true;
                        }
                    }
                }
            }

            // FIX: Affichage explicite du statut Windows Update
            if (!renderedData)
            {
                if (foundData)
                {
                    // Données trouvées mais rien de significatif à afficher
                    // Vérifions si on peut déduire "0 updates en attente"
                    rows.Add(("Updates en attente", "0 (système à jour)"));
                    rows.Add(("Statut Windows Update", "✅ Aucune mise à jour en attente"));
                }
                else
                {
                    // Pas de données du tout (COM indisponible, erreur, ou scan rapide)
                    rows.Add(("Windows Update", "Non disponible"));
                    rows.Add(("Raison possible", "COM/WMI indisponible ou scan rapide"));
                }
            }
            else if (rows.Count > 0 && !rows.Any(r => r.field.Contains("Updates en attente")))
            {
                // On a rendu quelque chose mais pas le count - afficher explicitement 0
                rows.Insert(0, ("Updates en attente", "0 (système à jour)"));
            }

            WriteTable(sb, rows);
            sb.AppendLine();
        }
        
        /// <summary>Helper: essaie plusieurs noms de propriétés pour un booléen</summary>
        private static bool? TryGetBool(JsonElement el, params string[] names)
        {
            foreach (var name in names)
            {
                if (TryGetPropertyRobust(el, out var prop, name))
                {
                    if (prop.ValueKind == JsonValueKind.True) return true;
                    if (prop.ValueKind == JsonValueKind.False) return false;
                    if (prop.ValueKind == JsonValueKind.Number) return prop.GetInt32() != 0;
                    if (prop.ValueKind == JsonValueKind.String)
                    {
                        var s = prop.GetString()?.ToLower();
                        if (s == "true" || s == "1" || s == "yes") return true;
                        if (s == "false" || s == "0" || s == "no") return false;
                    }
                }
            }
            return null;
        }
        
        /// <summary>Helper: essaie plusieurs noms de propriétés pour un string</summary>
        private static string? TryGetString(JsonElement el, params string[] names)
        {
            foreach (var name in names)
            {
                if (TryGetPropertyRobust(el, out var prop, name) && prop.ValueKind == JsonValueKind.String)
                {
                    var val = TextEncodingNormalizer.Normalize(prop.GetString());
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }
            return null;
        }
        
        /// <summary>Helper: essaie plusieurs noms de propriétés pour un int</summary>
        private static int TryGetInt(JsonElement el, params string[] names)
        {
            foreach (var name in names)
            {
                if (TryGetPropertyRobust(el, out var prop, name))
                {
                    if (prop.ValueKind == JsonValueKind.Number) return prop.GetInt32();
                    if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var i)) return i;
                }
            }
            return -1;
        }
        
        /// <summary>Helper: essaie plusieurs noms de propriétés pour un double</summary>
        private static double TryGetDoubleValue(JsonElement el, params string[] names)
        {
            foreach (var name in names)
            {
                if (TryGetPropertyRobust(el, out var prop, name))
                {
                    if (prop.ValueKind == JsonValueKind.Number) return prop.GetDouble();
                    if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), out var d)) return d;
                }
            }
            return -1;
        }
        
        /// <summary>Helper: extrait un double depuis un JsonElement de façon sûre (Number ou String)</summary>
        private static double SafeGetDouble(JsonElement el, double defaultValue = 0)
        {
            if (el.ValueKind == JsonValueKind.Number) return el.GetDouble();
            if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), out var d)) return d;
            return defaultValue;
        }

        #endregion

        #region Section 12: Pilotes

        private static void BuildSection12_Pilotes(StringBuilder sb, JsonElement? psData, JsonElement? combinedRoot)
        {
            sb.AppendLine("  ▶ SECTION 12 : PILOTES (DRIVERS)");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            var rows = new List<(string field, string value)>();
            bool foundData = false;

            // ===== C# DRIVER INVENTORY (combined JSON) =====
            if (combinedRoot.HasValue &&
                TryGetPropertyRobust(combinedRoot.Value, out var driverInv, "driver_inventory", "driverInventory") &&
                driverInv.ValueKind == JsonValueKind.Object)
            {
                foundData = true;

                var inventoryRows = new List<(string field, string value)>();
                var total = TryGetInt(driverInv, "totalCount", "TotalCount");
                var signed = TryGetInt(driverInv, "signedCount", "SignedCount");
                var unsigned = TryGetInt(driverInv, "unsignedCount", "UnsignedCount");
                var problem = TryGetInt(driverInv, "problemCount", "ProblemCount");

                if (total >= 0) inventoryRows.Add(("Pilotes détectés (C#)", total.ToString()));
                if (signed >= 0) inventoryRows.Add(("Signés", signed.ToString()));
                if (unsigned > 0) inventoryRows.Add(("Non signés", unsigned.ToString()));
                if (problem > 0) inventoryRows.Add(("Périph. en erreur", problem.ToString()));

                if (inventoryRows.Count > 0)
                {
                    WriteTable(sb, inventoryRows);
                    sb.AppendLine();
                }

                if (TryGetPropertyRobust(driverInv, out var driversArr, "drivers", "Drivers") &&
                    driversArr.ValueKind == JsonValueKind.Array)
                {
                    var drivers = driversArr.EnumerateArray().ToList();
                    if (drivers.Count > 0)
                    {
                        sb.AppendLine("  Inventaire pilotes (C# - WMI) :");
                        sb.AppendLine("  ┌──────────────┬──────────────────────────────────────┬────────────┬────────────┐");
                        sb.AppendLine("  │ Classe       │ Périphérique                         │ Version    │ Date       │");
                        sb.AppendLine("  ├──────────────┼──────────────────────────────────────┼────────────┼────────────┤");

                        int shown = 0;
                        foreach (var d in drivers)
                        {
                            if (shown++ >= 15) break;

                            var cls = TryGetString(d, "deviceClass", "DeviceClass") ?? "?";
                            var name = TryGetString(d, "deviceName", "DeviceName") ?? "?";
                            var provider = TryGetString(d, "provider", "Provider", "driverProviderName", "DriverProviderName");
                            var inf = TryGetString(d, "infName", "InfName");
                            var version = TryGetString(d, "driverVersion", "DriverVersion") ?? "-";
                            var date = TryGetString(d, "driverDate", "DriverDate") ?? "-";
                            var status = TryGetString(d, "updateStatus", "UpdateStatus") ?? TryGetString(d, "status", "Status");
                            if (string.Equals(status, "Outdated", StringComparison.OrdinalIgnoreCase)) status = "Obsolète";
                            if (string.Equals(status, "UpToDate", StringComparison.OrdinalIgnoreCase)) status = "À jour";

                            var deviceDisplay = string.IsNullOrEmpty(provider) ? name : $"{name} ({provider})";
                            deviceDisplay = deviceDisplay.Length > 36 ? deviceDisplay.Substring(0, 33) + "..." : deviceDisplay;
                            var shortVer = version.Length > 10 ? version.Substring(0, 7) + "..." : version;
                            var shortDate = date.Length > 10 ? date.Substring(0, 10) : date;

                            sb.AppendLine($"  │ {cls,-12} │ {deviceDisplay,-36} │ {shortVer,-10} │ {shortDate,-10} │");
                            if (!string.IsNullOrEmpty(inf) || !string.IsNullOrEmpty(status))
                            {
                                var info = $"    INF: {inf ?? "-"}";
                                if (!string.IsNullOrEmpty(status))
                                    info += $" | Statut: {status}";
                                sb.AppendLine($"  {info}");
                            }
                        }
                        sb.AppendLine("  └──────────────┴──────────────────────────────────────┴────────────┴────────────┘");
                        sb.AppendLine();
                    }
                }
            }

            // Pilotes essentiels Windows (Display, Net, Media, System, HDC, Bluetooth, USB)
            sb.AppendLine("  Pilotes essentiels installés :");
            sb.AppendLine("  ┌────────────────────┬──────────────────────────────────────────┬─────────────┬────────────┐");
            sb.AppendLine("  │ Classe             │ Périphérique                              │ Version     │ Date       │");
            sb.AppendLine("  ├────────────────────┼──────────────────────────────────────────┼─────────────┼────────────┤");
            
            var essentialDrivers = GetEssentialDriversFromWmi();
            if (essentialDrivers.Count > 0)
            {
                foundData = true;
                foreach (var (cls, name, version, date) in essentialDrivers.Take(15))
                {
                    var shortName = (name ?? "").Length > 38 ? (name ?? "").Substring(0, 35) + "..." : (name ?? "");
                    var shortVer = (version ?? "").Length > 11 ? (version ?? "").Substring(0, 8) + "..." : (version ?? "");
                    var shortDate = string.IsNullOrEmpty(date) ? "-" : (date.Length > 10 ? date.Substring(0, 10) : date);
                    sb.AppendLine($"  │ {cls,-18} │ {shortName,-40} │ {shortVer,-11} │ {shortDate,-10} │");
                }
                sb.AppendLine("  └────────────────────┴──────────────────────────────────────────┴─────────────┴────────────┘");
            }
            else
            {
                sb.AppendLine("  │ (WMI non disponible ou erreur)                                                    │");
                sb.AppendLine("  └────────────────────┴──────────────────────────────────────────┴─────────────┴────────────┘");
            }
            sb.AppendLine();

            if (psData.HasValue)
            {
                // FIX 8: GPU driver - plusieurs chemins
                var gpuPaths = new[]
                {
                    GetNestedElement(psData.Value, "sections", "GPUInfo", "data"),
                    GetNestedElement(psData.Value, "sections", "GPU", "data"),
                    GetNestedElement(psData.Value, "GPUInfo"),
                    GetNestedElement(psData.Value, "GPU")
                };
                
                foreach (var gpuData in gpuPaths)
                {
                    if (gpuData.HasValue)
                    {
                        var driver = TryGetString(gpuData.Value, "DriverVersion", "Driver", "Version");
                        if (!string.IsNullOrEmpty(driver))
                        {
                            rows.Add(("Pilote GPU", driver));
                            foundData = true;
                        }
                        var gpuName = TryGetString(gpuData.Value, "Name", "GPUName", "DeviceName");
                        if (!string.IsNullOrEmpty(gpuName))
                            rows.Add(("  GPU", gpuName));
                        var driverDate = TryGetString(gpuData.Value, "DriverDate", "Date");
                        if (!string.IsNullOrEmpty(driverDate))
                            rows.Add(("  Date pilote", driverDate));
                        break;
                    }
                }
                
                // Network driver
                var netPaths = new[]
                {
                    GetNestedElement(psData.Value, "sections", "NetworkAdapterInfo", "data"),
                    GetNestedElement(psData.Value, "sections", "NetworkInfo", "data"),
                    GetNestedElement(psData.Value, "NetworkAdapterInfo")
                };
                
                foreach (var netData in netPaths)
                {
                    if (netData.HasValue)
                    {
                        if (netData.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var adapter in netData.Value.EnumerateArray())
                            {
                                var dv = TryGetString(adapter, "DriverVersion", "Driver");
                                var name = TryGetString(adapter, "Name", "Description", "DeviceName");
                                if (!string.IsNullOrEmpty(dv) && !string.IsNullOrEmpty(name))
                                {
                                    rows.Add(("Pilote réseau", $"{name.Substring(0, Math.Min(30, name.Length))}... v{dv}"));
                                    foundData = true;
                                    break;
                                }
                            }
                        }
                        else if (netData.Value.ValueKind == JsonValueKind.Object)
                        {
                            var dv = TryGetString(netData.Value, "DriverVersion", "Driver");
                            if (!string.IsNullOrEmpty(dv))
                            {
                                rows.Add(("Pilote réseau", dv));
                                foundData = true;
                            }
                        }
                        break;
                    }
                }

                // FIX: DevicesDrivers.data.problemDevices est le chemin PS correct
                var devDriversData = GetNestedElement(psData.Value, "sections", "DevicesDrivers", "data");
                JsonElement? problemDevicesArr = null;
                int problemDeviceCount = 0;
                
                if (devDriversData.HasValue)
                {
                    foundData = true;
                    problemDeviceCount = TryGetInt(devDriversData.Value, "problemDeviceCount", "ProblemDeviceCount");
                    if (devDriversData.Value.TryGetProperty("problemDevices", out var pd) && pd.ValueKind == JsonValueKind.Array)
                        problemDevicesArr = pd;
                }
                
                // Fallback: anciens chemins
                if (!problemDevicesArr.HasValue)
                {
                    var devicePaths = new[]
                    {
                        GetNestedElement(psData.Value, "sections", "DevicesInfo", "data"),
                        GetNestedElement(psData.Value, "sections", "Devices", "data"),
                        GetNestedElement(psData.Value, "DevicesInfo")
                    };
                    foreach (var path in devicePaths)
                    {
                        if (path.HasValue && path.Value.ValueKind == JsonValueKind.Array)
                        {
                            problemDevicesArr = path;
                            foundData = true;
                            break;
                        }
                    }
                }
                
                if (problemDevicesArr.HasValue && problemDevicesArr.Value.ValueKind == JsonValueKind.Array)
                {
                    foundData = true;
                    var errorDevices = new List<(string name, string status, string? cls, string? driver)>();
                    
                    foreach (var device in problemDevicesArr.Value.EnumerateArray())
                    {
                        var name = TryGetString(device, "name", "Name", "FriendlyName", "DeviceName") ?? "Périphérique";
                        var status = TryGetString(device, "status", "Status") ?? "Error";
                        var cls = TryGetString(device, "class", "Class", "DeviceClass");
                        var driver = TryGetString(device, "DriverVersion", "Driver");
                        errorDevices.Add((name, status, cls, driver));
                    }
                    
                    if (problemDeviceCount > 0 || errorDevices.Count > 0)
                        rows.Add(("⚠️ Périph. en erreur", (problemDeviceCount > 0 ? problemDeviceCount : errorDevices.Count).ToString()));
                    else
                        rows.Add(("Périph. en erreur", "0 (OK)"));
                    
                    if (errorDevices.Count > 0)
                    {
                        rows.Add(("", ""));
                        
                        sb.AppendLine("  Périphériques en erreur (Top 10):");
                        sb.AppendLine("  ┌────────────────────────────────────────┬────────────┬─────────────────────┐");
                        sb.AppendLine("  │ Périphérique                           │ Status     │ Classe              │");
                        sb.AppendLine("  ├────────────────────────────────────────┼────────────┼─────────────────────┤");
                        
                        foreach (var (name, stat, cls, driver) in errorDevices.Take(10))
                        {
                            var shortName = name.Length > 38 ? name.Substring(0, 35) + "..." : name;
                            var shortStatus = stat.Length > 10 ? stat.Substring(0, 10) : stat;
                            var shortCls = (cls ?? "N/A").Length > 19 ? (cls ?? "N/A").Substring(0, 16) + "..." : (cls ?? "N/A");
                            sb.AppendLine($"  │ {shortName,-38} │ {shortStatus,-10} │ {shortCls,-19} │");
                        }
                        sb.AppendLine("  └────────────────────────────────────────┴────────────┴─────────────────────┘");
                    }
                }
                else if (problemDeviceCount <= 0)
                {
                    rows.Add(("Périph. en erreur", "0 (OK)"));
                }
            }

            if (!foundData)
                rows.Add(("Pilotes", "Données non disponibles"));

            WriteTable(sb, rows);
            sb.AppendLine();
        }

        #endregion

        #region Section 13: Démarrage et Applications

        private static void BuildSection13_Demarrage(StringBuilder sb, JsonElement? psData, JsonElement? diagnosticSnapshot)
        {
            sb.AppendLine("  ▶ SECTION 13 : DÉMARRAGE ET APPLICATIONS");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            bool foundData = false;
            bool renderedData = false;

            // ===== STARTUP PROGRAMS =====
            bool startupRendered = false;
            bool startupDataPresent = false;

            // Snapshot priority
            if (diagnosticSnapshot.HasValue)
            {
                var snapStartup = RenderIfPresent(diagnosticSnapshot, new[] { "psSummary", "startup" });
                if (snapStartup.HasValue)
                {
                    startupDataPresent = true;
                    var startupRows = new List<(string field, string value)>();
                    var count = TryGetInt(snapStartup.Value, "count", "Count");
                    if (count >= 0)
                    {
                        startupRows.Add(("Total programmes démarrage", count.ToString()));
                        startupRendered = true;
                    }

                    if (startupRows.Count > 0)
                    {
                        WriteTable(sb, startupRows);
                        sb.AppendLine();
                    }

                    if (TryGetPropertyRobust(snapStartup.Value, out var topItems, "topItems", "TopItems") &&
                        topItems.ValueKind == JsonValueKind.Array)
                    {
                        sb.AppendLine("  Programmes au démarrage (extrait):");
                        sb.AppendLine("  ┌────────────────────────────────────────┐");
                        sb.AppendLine("  │ Programme                              │");
                        sb.AppendLine("  ├────────────────────────────────────────┤");
                        foreach (var item in topItems.EnumerateArray().Take(10))
                        {
                            var name = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
                            if (string.IsNullOrEmpty(name)) continue;
                            name = name.Length > 38 ? name.Substring(0, 35) + "..." : name;
                            sb.AppendLine($"  │ {name,-38} │");
                        }
                        sb.AppendLine("  └────────────────────────────────────────┘");
                        startupRendered = true;
                    }
                }
            }

            // PS fallback
            if (!startupRendered && psData.HasValue)
            {
                var startupData = RenderIfPresent(psData,
                    new[] { "sections", "StartupPrograms", "data" },
                    new[] { "sections", "StartupInfo", "data" },
                    new[] { "sections", "Startup", "data" },
                    new[] { "StartupPrograms" },
                    new[] { "Startup" });

                JsonElement? startupItemsArr = null;
                int startupCount = 0;

                if (startupData.HasValue)
                {
                    startupDataPresent = true;
                    startupCount = TryGetInt(startupData.Value, "startupCount", "StartupCount", "count", "Count", "total", "Total");

                    if (startupData.Value.ValueKind == JsonValueKind.Array)
                    {
                        startupItemsArr = startupData;
                    }
                    else if (startupData.Value.ValueKind == JsonValueKind.Object &&
                             TryGetPropertyRobust(startupData.Value, out var itemsEl,
                                 "startupItems", "StartupItems", "items", "Items", "apps", "Apps",
                                 "programs", "Programs", "list", "List", "entries", "Entries"))
                    {
                        if (itemsEl.ValueKind == JsonValueKind.Array)
                            startupItemsArr = itemsEl;
                    }
                }

                if (startupItemsArr.HasValue)
                {
                    var items = startupItemsArr.Value.EnumerateArray().ToList();
                    var rows = new List<(string field, string value)>
                    {
                        ("Total programmes démarrage", (startupCount > 0 ? startupCount : items.Count).ToString())
                    };
                    WriteTable(sb, rows);
                    sb.AppendLine();

                    sb.AppendLine("  Programmes au démarrage:");
                    sb.AppendLine("  ┌────────────────────────────────────────┬────────────┐");
                    sb.AppendLine("  │ Programme                              │ Scope      │");
                    sb.AppendLine("  ├────────────────────────────────────────┼────────────┤");

                    int count = 0;
                    foreach (var item in items)
                    {
                        if (count++ >= 15) break;
                        var name = TryGetString(item, "name", "Name", "DisplayName", "Command") ?? "?";
                        var scope = TryGetString(item, "scope", "Scope") ?? "N/A";
                        name = name.Length > 38 ? name.Substring(0, 35) + "..." : name;
                        scope = scope.Length > 10 ? scope.Substring(0, 10) : scope;
                        sb.AppendLine($"  │ {name,-38} │ {scope,-10} │");
                    }
                    sb.AppendLine("  └────────────────────────────────────────┴────────────┘");

                    if (items.Count > 10)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"  💡 Suggestion: {items.Count} programmes au démarrage.");
                        sb.AppendLine("     Désactivez ceux non essentiels pour accélérer le boot.");
                    }
                    startupRendered = true;
                }
                else if (startupCount > 0)
                {
                    var rows = new List<(string field, string value)>();
                    rows.Add(("Total programmes démarrage", startupCount.ToString()));
                    WriteTable(sb, rows);
                    startupRendered = true;
                }
            }

            if (startupDataPresent || startupRendered)
            {
                foundData = true;
                renderedData = renderedData || startupRendered;
            }

            // ===== INSTALLED APPLICATIONS =====
            bool appsRendered = false;
            bool appsDataPresent = false;

            if (diagnosticSnapshot.HasValue)
            {
                var snapApps = RenderIfPresent(diagnosticSnapshot, new[] { "psSummary", "apps" });
                if (snapApps.HasValue)
                {
                    appsDataPresent = true;
                    var appRows = new List<(string field, string value)>();
                    var count = TryGetInt(snapApps.Value, "installedCount", "InstalledCount", "count");
                    if (count >= 0)
                    {
                        appRows.Add(("Total applications installées", count.ToString()));
                        appsRendered = true;
                    }
                    var lastInstalled = TryGetString(snapApps.Value, "lastInstalled", "LastInstalled", "lastInstallDate");
                    if (!string.IsNullOrEmpty(lastInstalled))
                    {
                        appRows.Add(("Dernière installation", lastInstalled));
                        appsRendered = true;
                    }
                    if (appRows.Count > 0)
                    {
                        WriteTable(sb, appRows);
                        sb.AppendLine();
                    }
                }
            }

            if (!appsRendered && psData.HasValue)
            {
                var appsData = RenderIfPresent(psData,
                    new[] { "sections", "InstalledApplications", "data" },
                    new[] { "sections", "Applications", "data" },
                    new[] { "InstalledApplications" },
                    new[] { "Applications" });

                if (appsData.HasValue)
                {
                    appsDataPresent = true;
                    var appRows = new List<(string field, string value)>();
                    var total = TryGetInt(appsData.Value, "totalCount", "TotalCount", "installedCount", "InstalledCount", "count", "total");
                    if (total >= 0)
                    {
                        appRows.Add(("Total applications installées", total.ToString()));
                        appsRendered = true;
                    }
                    var lastInstalled = TryGetString(appsData.Value, "lastInstallDate", "LastInstallDate", "lastInstalled", "LastInstalled");
                    if (!string.IsNullOrEmpty(lastInstalled))
                    {
                        appRows.Add(("Dernière installation", lastInstalled));
                        appsRendered = true;
                    }
                    if (appRows.Count > 0)
                    {
                        WriteTable(sb, appRows);
                        sb.AppendLine();
                    }

                    if (TryGetPropertyRobust(appsData.Value, out var appsList, "apps", "Applications", "applications", "items", "list") &&
                        appsList.ValueKind == JsonValueKind.Array)
                    {
                        sb.AppendLine("  Applications (extrait):");
                        sb.AppendLine("  ┌────────────────────────────────────────┐");
                        sb.AppendLine("  │ Application                            │");
                        sb.AppendLine("  ├────────────────────────────────────────┤");
                        foreach (var app in appsList.EnumerateArray().Take(10))
                        {
                            var name = TryGetString(app, "name", "Name", "displayName", "DisplayName") ?? app.ToString();
                            if (string.IsNullOrEmpty(name)) continue;
                            name = name.Length > 38 ? name.Substring(0, 35) + "..." : name;
                            sb.AppendLine($"  │ {name,-38} │");
                        }
                        sb.AppendLine("  └────────────────────────────────────────┘");
                        appsRendered = true;
                    }
                }
            }

            if (appsDataPresent || appsRendered)
            {
                foundData = true;
                renderedData = renderedData || appsRendered;
            }

            // ===== SERVICES (PS) =====
            if (psData.HasValue)
            {
                var servicesData = RenderIfPresent(psData,
                    new[] { "sections", "ServicesInfo", "data" },
                    new[] { "sections", "Services", "data" },
                    new[] { "ServicesInfo" },
                    new[] { "Services" });

                if (servicesData.HasValue)
                {
                    foundData = true;
                    sb.AppendLine();

                    if (servicesData.Value.ValueKind == JsonValueKind.Object)
                    {
                        var total = TryGetInt(servicesData.Value, "TotalServices", "totalServices", "Total");
                        var running = TryGetInt(servicesData.Value, "RunningServices", "runningServices", "Running");
                        var stopped = TryGetInt(servicesData.Value, "StoppedServices", "stoppedServices", "Stopped");
                        var auto = TryGetInt(servicesData.Value, "AutoStartServices", "autoStartServices", "AutoStart");

                        if (total > 0 || running > 0)
                        {
                            var svcRows = new List<(string field, string value)>();
                            svcRows.Add(("═ Services Windows ═", ""));
                            if (total > 0) svcRows.Add(("Total services", total.ToString()));
                            if (running > 0) svcRows.Add(("En cours", running.ToString()));
                            if (stopped > 0) svcRows.Add(("Arrêtés", stopped.ToString()));
                            if (auto > 0) svcRows.Add(("Démarrage auto", auto.ToString()));
                            WriteTable(sb, svcRows);
                            renderedData = true;
                        }
                    }
                    else if (servicesData.Value.ValueKind == JsonValueKind.Array)
                    {
                        var services = servicesData.Value.EnumerateArray().ToList();
                        var running = services.Count(s => TryGetString(s, "Status", "State")?.ToLower() == "running");
                        var svcRows = new List<(string field, string value)>();
                        svcRows.Add(("═ Services Windows ═", ""));
                        svcRows.Add(("Total services", services.Count.ToString()));
                        svcRows.Add(("En cours", running.ToString()));
                        WriteTable(sb, svcRows);
                    }
                }
            }

            if (!renderedData)
            {
                if (foundData)
                    sb.AppendLine("  Données présentes (voir bloc PowerShell brut)");
                else
                    sb.AppendLine("  Démarrage et applications : Données non disponibles");
            }

            sb.AppendLine();
        }

        #endregion

        #region Section 14: Santé système et Erreurs

        private static void BuildSection14_SanteSysteme(StringBuilder sb, JsonElement? psData, HealthReport? healthReport, JsonElement? combinedRoot = null)
        {
            sb.AppendLine("  ▶ SECTION 14 : SANTÉ SYSTÈME ET ERREURS");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            var rows = new List<(string field, string value)>();

            // Errors from HealthReport
            if (healthReport != null)
            {
                var errorCount = healthReport.Errors?.Count ?? 0;
                var collectionStatus = healthReport.CollectionStatus;
                
                rows.Add(("Statut collecte", collectionStatus));
                rows.Add(("Erreurs collecteur", errorCount.ToString()));

                if (healthReport.Errors != null && healthReport.Errors.Count > 0)
                {
                    rows.Add(("", ""));
                    rows.Add(("Détail erreurs:", ""));
                    foreach (var err in healthReport.Errors.Take(5))
                    {
                        var msg = err.Message.Length > 50 ? err.Message.Substring(0, 47) + "..." : err.Message;
                        rows.Add(($"  [{err.Code}]", msg));
                    }
                }
            }

            // FIX 6: WMI Errors from CollectorDiagnostics - actionnable, never "Unknown"
            if (combinedRoot.HasValue)
            {
                JsonElement collectorDiag = default;
                if (TryGetPropertyRobust(combinedRoot.Value, out collectorDiag, "collector_diagnostics", "collectorDiagnostics"))
                {
                    if (collectorDiag.TryGetProperty("wmi_errors", out var wmiErrors) && 
                        wmiErrors.ValueKind == JsonValueKind.Array)
                    {
                        var errCount = 0;
                        foreach (var _ in wmiErrors.EnumerateArray()) errCount++;
                        
                        if (errCount > 0)
                        {
                            rows.Add(("", ""));
                            rows.Add(("═ Erreurs WMI ═", ""));
                            rows.Add(("Total erreurs WMI", errCount.ToString()));
                            
                            int shown = 0;
                            foreach (var wmiErr in wmiErrors.EnumerateArray())
                            {
                                if (shown++ >= 5) break;
                                
                                var ns = wmiErr.TryGetProperty("namespace", out var nsEl) ? nsEl.GetString() : "?";
                                var query = wmiErr.TryGetProperty("query", out var qEl) ? qEl.GetString() : "?";
                                var hresult = wmiErr.TryGetProperty("hresult", out var hrEl) ? hrEl.GetString() : "?";
                                var duration = wmiErr.TryGetProperty("duration_ms", out var durEl) ? durEl.GetInt64().ToString() : "?";
                                var excType = wmiErr.TryGetProperty("exception_type", out var etEl) ? etEl.GetString() : "?";
                                
                                // FIX 6: Format actionnable - jamais "Unknown"
                                var shortQuery = query?.Length > 30 ? query.Substring(0, 27) + "..." : query;
                                rows.Add(($"  WMI #{shown}", $"{ns}: {shortQuery}"));
                                rows.Add(($"    HRESULT", $"{hresult}, {excType}, {duration}ms"));
                            }
                        }
                    }
                }
            }

            // Event logs summary
            if (psData.HasValue)
            {
                var eventData = GetNestedElement(psData.Value, "sections", "EventLogInfo", "data");
                if (eventData.HasValue)
                {
                    var errors7d = eventData.Value.TryGetProperty("ErrorCount7d", out var e7) ? e7.GetInt32() : 0;
                    var warnings7d = eventData.Value.TryGetProperty("WarningCount7d", out var w7) ? w7.GetInt32() : 0;
                    var bsod30d = eventData.Value.TryGetProperty("BSODCount30d", out var bs) ? bs.GetInt32() : 0;
                    var kp41 = eventData.Value.TryGetProperty("KernelPower41Count", out var kp) ? kp.GetInt32() : 0;

                    rows.Add(("", ""));
                    rows.Add(("═ Journal événements ═", ""));
                    rows.Add(("Erreurs (7 jours)", errors7d.ToString()));
                    rows.Add(("Avertissements (7 jours)", warnings7d.ToString()));
                    rows.Add(("BSOD (30 jours)", bsod30d.ToString()));
                    rows.Add(("Kernel Power 41", kp41.ToString()));
                }
            }

            if (rows.Count == 0)
                rows.Add(("Santé système", "Données non disponibles"));

            WriteTable(sb, rows);
            sb.AppendLine();

            // Événements détaillés (C#) - derniers N Critical/Error (add-only)
            if (combinedRoot.HasValue && combinedRoot.Value.TryGetProperty("event_logs_detailed", out var eventLogsArr) && eventLogsArr.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine("  ═ Événements détaillés (C#) ═");
                int shown = 0;
                foreach (var ev in eventLogsArr.EnumerateArray())
                {
                    if (shown >= 20) break;
                    var eventId = ev.TryGetProperty("event_id", out var eid) ? eid.GetInt32() : ev.TryGetProperty("eventId", out eid) ? eid.GetInt32() : 0;
                    var provider = ev.TryGetProperty("provider_name", out var pn) ? pn.GetString() ?? "?" : ev.TryGetProperty("providerName", out pn) ? pn.GetString() ?? "?" : "?";
                    var msg = ev.TryGetProperty("message", out var m) ? m.GetString() ?? "" : ev.TryGetProperty("Message", out m) ? m.GetString() ?? "" : "";
                    var timeCreated = ev.TryGetProperty("time_created", out var tc) ? tc.GetString() : ev.TryGetProperty("timeCreated", out tc) ? tc.GetString() : null;
                    if (msg.Length > 80) msg = msg.Substring(0, 77) + "...";
                    sb.AppendLine($"  [{shown + 1}] Id={eventId} | {provider} | {timeCreated ?? "?"}");
                    sb.AppendLine($"      {msg}");
                    shown++;
                }
                if (shown == 0)
                    sb.AppendLine("  Aucun événement Critical/Error collecté.");
                sb.AppendLine();
            }

            // Minidumps (C#) - liste fichier + date + BugCheck (add-only)
            if (combinedRoot.HasValue && combinedRoot.Value.TryGetProperty("minidumps_detailed", out var minidumpsArr) && minidumpsArr.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine("  ═ Minidumps (C#) ═");
                foreach (var md in minidumpsArr.EnumerateArray())
                {
                    var fileName = md.TryGetProperty("file_name", out var fn) ? fn.GetString() ?? "?" : md.TryGetProperty("fileName", out fn) ? fn.GetString() ?? "?" : "?";
                    var dateStr = md.TryGetProperty("last_write_time_utc", out var dt) ? dt.GetString() : md.TryGetProperty("lastWriteTimeUtc", out dt) ? dt.GetString() : null;
                    if (string.IsNullOrEmpty(dateStr) && dt.ValueKind == JsonValueKind.String)
                        dateStr = dt.GetString();
                    var bugCheck = md.TryGetProperty("bug_check_code", out var bc) ? bc.GetUInt32() : md.TryGetProperty("bugCheckCode", out bc) ? bc.GetUInt32() : (uint?)null;
                    var driverHint = md.TryGetProperty("driver_hint", out var dh) ? dh.GetString() : md.TryGetProperty("driverHint", out dh) ? dh.GetString() : null;
                    sb.AppendLine($"  Fichier: {fileName} | Date: {dateStr ?? "?"}");
                    if (bugCheck.HasValue)
                        sb.AppendLine($"      BugCheck: 0x{bugCheck.Value:X8}");
                    if (!string.IsNullOrEmpty(driverHint))
                        sb.AppendLine($"      Driver hint: {driverHint}");
                }
                sb.AppendLine();
            }
        }

        #endregion

        #region Section 15: Périphériques

        private static void BuildSection15_Peripheriques(StringBuilder sb, JsonElement? psData, JsonElement? diagnosticSnapshot)
        {
            sb.AppendLine("  ▶ SECTION 15 : PÉRIPHÉRIQUES");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            var rows = new List<(string field, string value)>();
            bool foundData = false;

            // ===== AUDIO =====
            bool audioRendered = false;
            if (diagnosticSnapshot.HasValue)
            {
                var snapAudio = RenderIfPresent(diagnosticSnapshot, new[] { "psSummary", "audio" });
                if (snapAudio.HasValue)
                {
                    foundData = true;
                    var count = TryGetInt(snapAudio.Value, "count", "Count");
                    if (count >= 0)
                    {
                        rows.Add(("Périphériques audio", count.ToString()));
                        audioRendered = true;
                    }
                }
            }

            if (!audioRendered && psData.HasValue)
            {
                var audioData = RenderIfPresent(psData,
                    new[] { "sections", "Audio", "data" },
                    new[] { "sections", "AudioDevices", "data" });
                if (audioData.HasValue)
                {
                    foundData = true;
                    int audioCount = -1;
                    if (audioData.Value.ValueKind == JsonValueKind.Array)
                        audioCount = audioData.Value.GetArrayLength();
                    else if (audioData.Value.ValueKind == JsonValueKind.Object)
                    {
                        audioCount = TryGetInt(audioData.Value, "deviceCount", "DeviceCount", "count", "Count");
                        if (audioCount < 0 && TryGetPropertyRobust(audioData.Value, out var devArr, "devices", "Devices", "items", "Items", "list", "List") &&
                            devArr.ValueKind == JsonValueKind.Array)
                            audioCount = devArr.GetArrayLength();
                    }
                    if (audioCount >= 0)
                    {
                        rows.Add(("Périphériques audio", audioCount.ToString()));
                    }
                }
            }

            // ===== PRINTERS =====
            bool printersRendered = false;
            if (diagnosticSnapshot.HasValue)
            {
                var snapPrinters = RenderIfPresent(diagnosticSnapshot, new[] { "psSummary", "printers" });
                if (snapPrinters.HasValue)
                {
                    foundData = true;
                    var count = TryGetInt(snapPrinters.Value, "count", "Count");
                    if (count >= 0)
                    {
                        rows.Add(("Imprimantes", count.ToString()));
                        printersRendered = true;
                    }
                }
            }

            if (!printersRendered && psData.HasValue)
            {
                var printerData = RenderIfPresent(psData,
                    new[] { "sections", "Printers", "data" },
                    new[] { "sections", "PrinterInfo", "data" });
                if (printerData.HasValue)
                {
                    foundData = true;
                    int printerCount = -1;
                    JsonElement? printersArr = null;

                    if (printerData.Value.ValueKind == JsonValueKind.Array)
                    {
                        printerCount = printerData.Value.GetArrayLength();
                        printersArr = printerData;
                    }
                    else if (printerData.Value.ValueKind == JsonValueKind.Object)
                    {
                        printerCount = TryGetInt(printerData.Value, "printerCount", "PrinterCount", "count", "Count");
                        if (TryGetPropertyRobust(printerData.Value, out var pArr, "printers", "Printers", "items", "Items", "list", "List", "devices", "Devices") &&
                            pArr.ValueKind == JsonValueKind.Array)
                        {
                            printersArr = pArr;
                            if (printerCount < 0)
                                printerCount = pArr.GetArrayLength();
                        }
                    }

                    if (printerCount >= 0)
                    {
                        rows.Add(("Imprimantes", printerCount.ToString()));
                    }

                    if (printersArr.HasValue)
                    {
                        var printerList = printersArr.Value.EnumerateArray().Take(5).ToList();
                        if (printerList.Count > 0)
                        {
                            foreach (var p in printerList)
                            {
                                var name = TryGetString(p, "name", "Name", "PrinterName", "printerName") ?? "?";
                                var isDefault = TryGetBool(p, "default", "Default", "isDefault", "IsDefault") ?? false;
                                var displayName = name.Length > 30 ? name.Substring(0, 27) + "..." : name;
                                rows.Add(($"  • {(isDefault ? "⭐ " : "")}{displayName}", ""));
                            }
                        }
                    }
                }
            }

            // ===== DEVICES / DRIVERS =====
            bool devicesRendered = false;
            if (diagnosticSnapshot.HasValue)
            {
                var snapDevices = RenderIfPresent(diagnosticSnapshot, new[] { "psSummary", "devices" });
                if (snapDevices.HasValue)
                {
                    foundData = true;
                    var problemCount = TryGetInt(snapDevices.Value, "problemDeviceCount", "ProblemDeviceCount");
                    if (problemCount >= 0)
                    {
                        rows.Add(("Périph. en erreur", problemCount > 0 ? $"⚠️ {problemCount}" : "0 ✅"));
                        devicesRendered = true;
                    }
                }
            }

            if (!devicesRendered && psData.HasValue)
            {
                var devDriversData = RenderIfPresent(psData,
                    new[] { "sections", "DevicesDrivers", "data" },
                    new[] { "sections", "Devices", "data" },
                    new[] { "sections", "PnPDevices", "data" });

                if (devDriversData.HasValue)
                {
                    foundData = true;
                    int problemCount = -1;
                    JsonElement? problemArr = null;

                    if (devDriversData.Value.ValueKind == JsonValueKind.Object)
                    {
                        problemCount = TryGetInt(devDriversData.Value, "problemDeviceCount", "ProblemDeviceCount", "problemCount", "ProblemCount", "errorCount", "ErrorCount");

                        if (TryGetPropertyRobust(devDriversData.Value, out var pd, "problemDevices", "ProblemDevices", "problems", "Problems", "errors", "Errors", "failedDevices", "FailedDevices"))
                        {
                            if (pd.ValueKind == JsonValueKind.Array)
                            {
                                problemArr = pd;
                                if (problemCount < 0)
                                    problemCount = pd.GetArrayLength();
                            }
                            else if (pd.ValueKind == JsonValueKind.Object && problemCount < 0)
                            {
                                problemCount = pd.EnumerateObject().Count();
                            }
                        }

                        var totalDevices = TryGetInt(devDriversData.Value, "deviceCount", "DeviceCount", "total", "Total");
                        if (totalDevices > 0)
                            rows.Add(("Total périphériques", totalDevices.ToString()));
                    }
                    else if (devDriversData.Value.ValueKind == JsonValueKind.Array)
                    {
                        var allDevices = devDriversData.Value.EnumerateArray().ToList();
                        var problemDevices = allDevices.Where(d =>
                        {
                            var status = TryGetString(d, "status", "Status", "state", "State") ?? "";
                            return !string.IsNullOrEmpty(status) &&
                                   !status.Equals("OK", StringComparison.OrdinalIgnoreCase) &&
                                   !status.Equals("Running", StringComparison.OrdinalIgnoreCase);
                        }).ToList();

                        problemCount = problemDevices.Count;
                        rows.Add(("Total périphériques", allDevices.Count.ToString()));
                    }

                    if (problemCount >= 0)
                    {
                        rows.Add(("Périph. en erreur", problemCount > 0 ? $"⚠️ {problemCount}" : "0 ✅"));
                    }

                    if (problemCount > 0 && problemArr.HasValue && problemArr.Value.ValueKind == JsonValueKind.Array)
                    {
                        rows.Add(("", ""));
                        sb.AppendLine("  Périphériques problématiques:");
                        foreach (var dev in problemArr.Value.EnumerateArray().Take(5))
                        {
                            var name = TryGetString(dev, "name", "Name", "deviceName", "DeviceName") ?? "?";
                            var status = TryGetString(dev, "status", "Status", "state", "State") ?? "?";
                            var cls = TryGetString(dev, "class", "Class", "deviceClass", "DeviceClass") ?? "";
                            var displayName = name.Length > 35 ? name.Substring(0, 32) + "..." : name;
                            sb.AppendLine($"    [{status}] {displayName} ({cls})");
                        }
                        sb.AppendLine();
                    }
                }
            }

            // ===== BLUETOOTH =====
            if (psData.HasValue)
            {
                var btData = RenderIfPresent(psData,
                    new[] { "sections", "Bluetooth", "data" },
                    new[] { "sections", "BluetoothInfo", "data" },
                    new[] { "sections", "BluetoothDevices", "data" });
                if (btData.HasValue)
                {
                    foundData = true;
                    int btCount = -1;
                    if (btData.Value.ValueKind == JsonValueKind.Array)
                        btCount = btData.Value.GetArrayLength();
                    else if (btData.Value.ValueKind == JsonValueKind.Object)
                    {
                        btCount = TryGetInt(btData.Value, "deviceCount", "DeviceCount", "count", "Count");
                        if (btCount < 0 && TryGetPropertyRobust(btData.Value, out var btArr, "devices", "Devices", "items", "Items", "list", "List") &&
                            btArr.ValueKind == JsonValueKind.Array)
                            btCount = btArr.GetArrayLength();
                    }
                    if (btCount >= 0)
                        rows.Add(("Périphériques Bluetooth", btCount.ToString()));
                }
            }

            // ===== USB =====
            if (psData.HasValue)
            {
                var usbData = RenderIfPresent(psData,
                    new[] { "sections", "USB", "data" },
                    new[] { "sections", "USBDevices", "data" });
                if (usbData.HasValue)
                {
                    foundData = true;
                    int usbCount = -1;
                    if (usbData.Value.ValueKind == JsonValueKind.Array)
                        usbCount = usbData.Value.GetArrayLength();
                    else if (usbData.Value.ValueKind == JsonValueKind.Object)
                    {
                        usbCount = TryGetInt(usbData.Value, "deviceCount", "DeviceCount", "count", "Count");
                        if (usbCount < 0 && TryGetPropertyRobust(usbData.Value, out var usbArr, "devices", "Devices", "items", "Items") &&
                            usbArr.ValueKind == JsonValueKind.Array)
                            usbCount = usbArr.GetArrayLength();
                    }
                    if (usbCount >= 0)
                        rows.Add(("Périphériques USB", usbCount.ToString()));
                }
            }

            if (!foundData || rows.Count == 0)
            {
                if (foundData)
                    rows.Add(("Périphériques", "Données présentes (voir bloc PowerShell brut)"));
                else
                    rows.Add(("Périphériques", "Données non disponibles"));
            }

            WriteTable(sb, rows);
            sb.AppendLine();
        }

        #endregion

        #region Section 16: Virtualisation (NEW)

        /// <summary>
        /// Section 16: Virtualisation - Affiche les informations sur la virtualisation
        /// </summary>
        private static void BuildSection16_Virtualisation(StringBuilder sb, JsonElement? psData, JsonElement? diagnosticSnapshot)
        {
            sb.AppendLine("  ▶ SECTION 16 : VIRTUALISATION");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            var rows = new List<(string field, string value)>();
            bool foundData = false;

            // Essayer le snapshot d'abord
            if (diagnosticSnapshot.HasValue)
            {
                var snapVirt = RenderIfPresent(diagnosticSnapshot, 
                    new[] { "psSummary", "virtualization" },
                    new[] { "virtualization" });
                if (snapVirt.HasValue)
                {
                    foundData = true;
                    var isVM = TryGetBool(snapVirt.Value, "isVM", "IsVM", "isVirtualMachine", "IsVirtualMachine");
                    if (isVM.HasValue)
                    {
                        rows.Add(("Machine virtuelle", isVM.Value ? "✅ Oui" : "❌ Non (machine physique)"));
                    }
                    var hypervisor = TryGetString(snapVirt.Value, "hypervisor", "Hypervisor", "vmType", "VmType");
                    if (!string.IsNullOrEmpty(hypervisor))
                    {
                        rows.Add(("Hyperviseur détecté", hypervisor));
                    }
                }
            }

            // Fallback sur les données PS brutes
            if (rows.Count == 0 && psData.HasValue)
            {
                var virtData = RenderIfPresent(psData,
                    new[] { "sections", "Virtualization", "data" },
                    new[] { "sections", "VirtualizationInfo", "data" },
                    new[] { "Virtualization" });

                if (virtData.HasValue)
                {
                    foundData = true;

                    // isVM
                    var isVM = TryGetBool(virtData.Value, "isVM", "IsVM", "isVirtualMachine", "IsVirtualMachine", "isVirtual", "IsVirtual");
                    if (isVM.HasValue)
                    {
                        rows.Add(("Machine virtuelle", isVM.Value ? "✅ Oui" : "❌ Non (machine physique)"));
                    }

                    // Hypervisor / VMType
                    var hypervisor = TryGetString(virtData.Value, "hypervisor", "Hypervisor", "vmType", "VmType", "vmPlatform", "VmPlatform");
                    if (!string.IsNullOrEmpty(hypervisor))
                    {
                        rows.Add(("Hyperviseur / Platform", hypervisor));
                    }

                    // Hyper-V enabled
                    var hyperVEnabled = TryGetBool(virtData.Value, "hyperVEnabled", "HyperVEnabled", "hyperVInstalled", "HyperVInstalled");
                    if (hyperVEnabled.HasValue)
                    {
                        rows.Add(("Hyper-V", hyperVEnabled.Value ? "✅ Activé" : "Non activé"));
                    }

                    // WSL
                    var wslEnabled = TryGetBool(virtData.Value, "wslEnabled", "WSLEnabled", "wsl2Installed", "WSL2Installed", "wslInstalled", "WSLInstalled");
                    if (wslEnabled.HasValue)
                    {
                        rows.Add(("WSL", wslEnabled.Value ? "✅ Activé" : "Non activé"));
                    }

                    // Sandbox
                    var sandboxEnabled = TryGetBool(virtData.Value, "sandboxEnabled", "SandboxEnabled", "windowsSandbox", "WindowsSandbox");
                    if (sandboxEnabled.HasValue)
                    {
                        rows.Add(("Windows Sandbox", sandboxEnabled.Value ? "✅ Activé" : "Non activé"));
                    }

                    // DeviceGuard / CredentialGuard
                    var deviceGuard = TryGetBool(virtData.Value, "deviceGuardEnabled", "DeviceGuardEnabled", "credentialGuard", "CredentialGuard");
                    if (deviceGuard.HasValue)
                    {
                        rows.Add(("Device Guard", deviceGuard.Value ? "✅ Actif" : "Non actif"));
                    }

                    // VBS (Virtualization-Based Security)
                    var vbs = TryGetBool(virtData.Value, "vbsEnabled", "VBSEnabled", "virtualizationBasedSecurity", "VirtualizationBasedSecurity");
                    if (vbs.HasValue)
                    {
                        rows.Add(("VBS (sécurité)", vbs.Value ? "✅ Actif" : "Non actif"));
                    }

                    // Containers
                    var containersEnabled = TryGetBool(virtData.Value, "containersEnabled", "ContainersEnabled", "dockerInstalled", "DockerInstalled");
                    if (containersEnabled.HasValue)
                    {
                        rows.Add(("Conteneurs", containersEnabled.Value ? "✅ Activé" : "Non activé"));
                    }
                }
            }

            // Message par défaut si pas de données
            if (!foundData || rows.Count == 0)
            {
                if (foundData)
                {
                    rows.Add(("Virtualisation", "Données présentes (voir bloc PowerShell brut)"));
                }
                else
                {
                    rows.Add(("Machine virtuelle", "❌ Non (machine physique probable)"));
                    rows.Add(("Hyper-V / WSL", "Non détecté ou non collecté"));
                }
            }

            WriteTable(sb, rows);
            sb.AppendLine();
        }

        #endregion

        #region Footer

        private static void BuildFooter(StringBuilder sb)
        {
            sb.AppendLine(SEPARATOR);
            sb.AppendLine();
            sb.AppendLine("  RAPPORT UNIFIÉ GÉNÉRÉ PAR PC DIAGNOSTIC PRO");
            sb.AppendLine();
            sb.AppendLine("  Ce rapport combine:");
            sb.AppendLine("    ✓ Données système PowerShell (structure, config, events)");
            sb.AppendLine("    ✓ Données capteurs hardware C# (températures, charges, VRAM)");
            sb.AppendLine("    ✓ UDIS — Unified Diagnostic Intelligence Scoring");
            sb.AppendLine("    ✓ Tests réseau locaux (sans speedtest externe)");
            sb.AppendLine("    ✓ Processus temps réel (CPU % mesuré sur 750ms)");
            sb.AppendLine();
            sb.AppendLine($"  Généré le {DateTime.Now:yyyy-MM-dd} à {DateTime.Now:HH:mm:ss}");
            sb.AppendLine("  PC X-Ray - Rapport Unifié v2.0");
            sb.AppendLine(SEPARATOR);
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Énumère les pilotes essentiels via WMI Win32_PnPSignedDriver
        /// </summary>
        private static List<(string cls, string? name, string? version, string date)> GetEssentialDriversFromWmi()
        {
            var result = new List<(string, string?, string?, string)>();
            var classes = new[] { "DISPLAY", "NET", "MEDIA", "SYSTEM", "HDC", "BLUETOOTH", "USB", "Sound", "Image" };
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "root\\cimv2",
                    "SELECT DeviceClass, DeviceName, DriverVersion, DriverDate FROM Win32_PnPSignedDriver WHERE DeviceClass IS NOT NULL");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var obj in searcher.Get().OfType<ManagementObject>())
                {
                    try
                    {
                        var devClass = obj["DeviceClass"]?.ToString() ?? "";
                        if (!classes.Any(c => string.Equals(devClass, c, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        var name = obj["DeviceName"]?.ToString();
                        var version = obj["DriverVersion"]?.ToString();
                        var dateRaw = obj["DriverDate"]?.ToString();
                        var date = ParseWmiDate(dateRaw);
                        var key = $"{devClass}|{name}";
                        if (seen.Add(key) && !string.IsNullOrEmpty(name))
                            result.Add((devClass, name, version ?? "-", date));
                    }
                    catch { /* Skip faulty device */ }
                }
                result = result.OrderBy(r => r.Item1).ThenBy(r => r.Item2).ToList();
            }
            catch (Exception ex)
            {
                App.LogMessage($"[UnifiedReport] GetEssentialDrivers WMI failed: {ex.Message}");
            }
            return result;
        }

        private static string ParseWmiDate(string? wmiDate)
        {
            if (string.IsNullOrEmpty(wmiDate) || wmiDate.Length < 8) return "";
            try
            {
                var y = wmiDate.Substring(0, 4);
                var m = wmiDate.Substring(4, 2);
                var d = wmiDate.Substring(6, 2);
                return $"{y}-{m}-{d}";
            }
            catch { return wmiDate; }
        }

        /// <summary>
        /// Écrit un tableau simple Champ | Valeur
        /// </summary>
        private static void WriteTable(StringBuilder sb, List<(string field, string value)> rows)
        {
            foreach (var (field, value) in rows)
            {
                if (string.IsNullOrEmpty(field) && string.IsNullOrEmpty(value))
                {
                    sb.AppendLine();
                    continue;
                }

                if (field.StartsWith("═"))
                {
                    sb.AppendLine($"  {field}");
                    continue;
                }

                var paddedField = field.PadRight(25);
                sb.AppendLine($"  {paddedField} : {value}");
            }
        }
        
        /// <summary>
        /// FIX 1: RenderIfPresent - Ajoute un champ au tableau SEULEMENT s'il existe
        /// Essaie plusieurs chemins pour trouver la donnée
        /// </summary>
        private static void RenderIfPresent(List<(string field, string value)> rows, string fieldName, JsonElement? psData, params string[][] paths)
        {
            if (!psData.HasValue) return;
            
            foreach (var path in paths)
            {
                var value = GetNestedStringRobust(psData.Value, path);
                if (!string.IsNullOrEmpty(value))
                {
                    rows.Add((fieldName, value));
                    return;
                }
            }
        }
        
        /// <summary>
        /// FIX 1: RenderIfPresentDouble - Pour les valeurs numériques
        /// </summary>
        private static void RenderIfPresentDouble(List<(string field, string value)> rows, string fieldName, string format, JsonElement? psData, params string[][] paths)
        {
            if (!psData.HasValue) return;
            
            foreach (var path in paths)
            {
                var value = GetNestedDoubleRobust(psData.Value, path);
                if (value > 0 && !double.IsNaN(value) && !double.IsInfinity(value))
                {
                    rows.Add((fieldName, string.Format(format, value)));
                    return;
                }
            }
        }
        
        /// <summary>
        /// FIX 1: RenderIfPresentBool - Pour les booléens (Oui/Non)
        /// </summary>
        private static void RenderIfPresentBool(List<(string field, string value)> rows, string fieldName, JsonElement? psData, string trueText, string falseText, params string[][] paths)
        {
            if (!psData.HasValue) return;
            
            foreach (var path in paths)
            {
                var value = GetNestedBoolRobust(psData.Value, path);
                if (value.HasValue)
                {
                    rows.Add((fieldName, value.Value ? trueText : falseText));
                    return;
                }
            }
        }
        
        /// <summary>
        /// Lecture robuste string avec fallback camelCase/snake_case/PascalCase
        /// </summary>
        private static string? GetNestedStringRobust(JsonElement root, string[] path)
        {
            var current = root;
            foreach (var key in path)
            {
                // Essayer plusieurs variantes du nom de propriété
                var variants = GetPropertyVariants(key);
                bool found = false;
                foreach (var variant in variants)
                {
                    if (current.TryGetProperty(variant, out var next))
                    {
                        current = next;
                        found = true;
                        break;
                    }
                }
                if (!found) return null;
            }
            
            if (current.ValueKind == JsonValueKind.String)
                return current.GetString();
            if (current.ValueKind == JsonValueKind.Number)
                return current.GetDouble().ToString("F2");
            if (current.ValueKind == JsonValueKind.True || current.ValueKind == JsonValueKind.False)
                return current.GetBoolean() ? "Oui" : "Non";
            
            return null;
        }
        
        /// <summary>
        /// Lecture robuste double avec fallback
        /// </summary>
        private static double GetNestedDoubleRobust(JsonElement root, string[] path)
        {
            var current = root;
            foreach (var key in path)
            {
                var variants = GetPropertyVariants(key);
                bool found = false;
                foreach (var variant in variants)
                {
                    if (current.TryGetProperty(variant, out var next))
                    {
                        current = next;
                        found = true;
                        break;
                    }
                }
                if (!found) return -1;
            }
            
            if (current.ValueKind == JsonValueKind.Number)
                return current.GetDouble();
            if (current.ValueKind == JsonValueKind.String && double.TryParse(current.GetString(), out var d))
                return d;
            
            return -1;
        }
        
        /// <summary>
        /// Lecture robuste bool avec fallback
        /// </summary>
        private static bool? GetNestedBoolRobust(JsonElement root, string[] path)
        {
            var current = root;
            foreach (var key in path)
            {
                var variants = GetPropertyVariants(key);
                bool found = false;
                foreach (var variant in variants)
                {
                    if (current.TryGetProperty(variant, out var next))
                    {
                        current = next;
                        found = true;
                        break;
                    }
                }
                if (!found) return null;
            }
            
            if (current.ValueKind == JsonValueKind.True) return true;
            if (current.ValueKind == JsonValueKind.False) return false;
            if (current.ValueKind == JsonValueKind.String)
            {
                var str = current.GetString()?.ToLowerInvariant();
                if (str == "true" || str == "1" || str == "yes" || str == "oui") return true;
                if (str == "false" || str == "0" || str == "no" || str == "non") return false;
            }
            if (current.ValueKind == JsonValueKind.Number)
                return current.GetInt32() != 0;
            
            return null;
        }
        
        /// <summary>
        /// Génère les variantes de noms de propriétés (camelCase, PascalCase, snake_case)
        /// </summary>
        private static string[] GetPropertyVariants(string key)
        {
            var variants = new List<string> { key };
            
            // camelCase → PascalCase
            if (!string.IsNullOrEmpty(key) && char.IsLower(key[0]))
                variants.Add(char.ToUpper(key[0]) + key.Substring(1));
            
            // PascalCase → camelCase
            if (!string.IsNullOrEmpty(key) && char.IsUpper(key[0]))
                variants.Add(char.ToLower(key[0]) + key.Substring(1));
            
            // snake_case
            if (key.Contains("_"))
            {
                var pascal = string.Join("", key.Split('_').Select(s => 
                    string.IsNullOrEmpty(s) ? "" : char.ToUpper(s[0]) + s.Substring(1).ToLower()));
                variants.Add(pascal);
                if (!string.IsNullOrEmpty(pascal))
                    variants.Add(char.ToLower(pascal[0]) + pascal.Substring(1));
            }
            
            return variants.ToArray();
        }

        private static string? GetNestedString(JsonElement root, params string[] path)
        {
            var current = root;
            foreach (var key in path)
            {
                if (!current.TryGetProperty(key, out current))
                    return null;
            }
            return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        }

        private static int GetNestedInt(JsonElement root, params string[] path)
        {
            var current = root;
            foreach (var key in path)
            {
                if (!TryGetPropertyRobust(current, out current, key))
                    return -1;
            }
            return current.ValueKind == JsonValueKind.Number ? current.GetInt32() : -1;
        }

        private static double GetNestedDouble(JsonElement root, params string[] path)
        {
            var current = root;
            foreach (var key in path)
            {
                if (!TryGetPropertyRobust(current, out current, key))
                    return -1;
            }
            return current.ValueKind == JsonValueKind.Number ? current.GetDouble() : -1;
        }

        private static JsonElement? GetNestedElement(JsonElement root, params string[] path)
        {
            var current = root;
            foreach (var key in path)
            {
                if (!TryGetPropertyRobust(current, out current, key))
                    return null;
            }
            return current;
        }

        /// <summary>
        /// RÈGLE: RenderIfPresent - essaie plusieurs chemins et retourne le premier non vide.
        /// </summary>
        private static JsonElement? RenderIfPresent(JsonElement? root, params string[][] paths)
        {
            if (!root.HasValue)
                return null;

            foreach (var path in paths)
            {
                var element = GetNestedElement(root.Value, path);
                if (element.HasValue && IsJsonElementNonEmpty(element.Value))
                    return element;
            }

            return null;
        }

        private static bool IsJsonElementNonEmpty(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Array => element.GetArrayLength() > 0,
                JsonValueKind.Object => element.EnumerateObject().Any(),
                JsonValueKind.String => !string.IsNullOrWhiteSpace(element.GetString()),
                JsonValueKind.Number => true,
                JsonValueKind.True => true,
                JsonValueKind.False => true,
                _ => false
            };
        }

        /// <summary>
        /// FIX E: Log report data availability to %TEMP% for debugging
        /// </summary>
        private static void LogReportDataAvailability(JsonElement root, JsonElement? psData, HardwareSensorsResult? sensors, string jsonPath)
        {
            try
            {
                var logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_Report_Debug.log");
                var logSb = new StringBuilder();
                logSb.AppendLine($"=== UNIFIED REPORT DEBUG LOG ===");
                logSb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                logSb.AppendLine($"Source JSON: {jsonPath}");
                logSb.AppendLine();
                
                // Root level properties
                logSb.AppendLine("=== ROOT LEVEL PROPERTIES ===");
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in root.EnumerateObject())
                    {
                        var valueKind = prop.Value.ValueKind.ToString();
                        var detail = prop.Value.ValueKind == JsonValueKind.Array 
                            ? $"[{prop.Value.GetArrayLength()} items]" 
                            : prop.Value.ValueKind == JsonValueKind.Object 
                                ? $"{{object}}" 
                                : "";
                        logSb.AppendLine($"  {prop.Name}: {valueKind} {detail}");
                    }
                }
                logSb.AppendLine();
                
                // process_telemetry availability
                logSb.AppendLine("=== PROCESS TELEMETRY ===");
                if (TryGetPropertyRobust(root, out var procTel, "process_telemetry", "processTelemetry", "ProcessTelemetry"))
                {
                    logSb.AppendLine($"  Found: YES ({procTel.ValueKind})");
                    if (procTel.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in procTel.EnumerateObject())
                        {
                            var detail = prop.Value.ValueKind == JsonValueKind.Array ? $"[{prop.Value.GetArrayLength()}]" : "";
                            logSb.AppendLine($"    {prop.Name}: {prop.Value.ValueKind} {detail}");
                        }
                    }
                }
                else
                {
                    logSb.AppendLine("  Found: NO");
                }
                logSb.AppendLine();
                
                // PS sections availability
                logSb.AppendLine("=== PS SECTIONS ===");
                if (psData.HasValue && psData.Value.TryGetProperty("sections", out var sections))
                {
                    logSb.AppendLine($"  sections found: YES ({sections.ValueKind})");
                    if (sections.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var section in sections.EnumerateObject())
                        {
                            var hasData = section.Value.TryGetProperty("data", out var data);
                            var dataInfo = hasData ? $"data={data.ValueKind}" : "no data";
                            if (hasData && data.ValueKind == JsonValueKind.Array)
                                dataInfo += $" [{data.GetArrayLength()}]";
                            logSb.AppendLine($"    {section.Name}: {dataInfo}");
                        }
                    }
                }
                else
                {
                    logSb.AppendLine("  sections found: NO");
                }
                logSb.AppendLine();
                
                // Sensors availability
                logSb.AppendLine("=== C# SENSORS ===");
                if (sensors != null)
                {
                    logSb.AppendLine($"  CollectedAt: {sensors.CollectedAt}");
                    logSb.AppendLine($"  CPU Temp: value={sensors.Cpu?.CpuTempC?.Value}, available={sensors.Cpu?.CpuTempC?.Available}");
                    logSb.AppendLine($"  GPU Temp: value={sensors.Gpu?.GpuTempC?.Value}, available={sensors.Gpu?.GpuTempC?.Available}");
                    logSb.AppendLine($"  GPU VRAM Used: value={sensors.Gpu?.VramUsedMB?.Value} MB");
                    logSb.AppendLine($"  BlockedBySecurity: {sensors.BlockedBySecurity}");
                }
                else
                {
                    logSb.AppendLine("  Sensors: null");
                }
                logSb.AppendLine();
                
                // Key sections for debugging
                var keySections = new[] { "WindowsUpdate", "StartupPrograms", "DevicesDrivers", "Printers", "Audio", "Processes", "DynamicSignals" };
                logSb.AppendLine("=== KEY SECTION DETAILS ===");
                foreach (var sectionName in keySections)
                {
                    if (psData.HasValue)
                    {
                        var sectionData = GetNestedElement(psData.Value, "sections", sectionName, "data");
                        if (sectionData.HasValue)
                        {
                            logSb.AppendLine($"  {sectionName}.data: {sectionData.Value.ValueKind}");
                            if (sectionData.Value.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var prop in sectionData.Value.EnumerateObject().Take(5))
                                {
                                    var val = prop.Value.ValueKind == JsonValueKind.Array ? $"[{prop.Value.GetArrayLength()}]" : 
                                              prop.Value.ValueKind == JsonValueKind.String ? $"\"{prop.Value.GetString()?.Substring(0, Math.Min(20, prop.Value.GetString()?.Length ?? 0))}...\"" :
                                              prop.Value.ToString().Substring(0, Math.Min(30, prop.Value.ToString().Length));
                                    logSb.AppendLine($"      {prop.Name}: {val}");
                                }
                            }
                            else if (sectionData.Value.ValueKind == JsonValueKind.Array)
                            {
                                logSb.AppendLine($"      [{sectionData.Value.GetArrayLength()} items]");
                            }
                        }
                        else
                        {
                            logSb.AppendLine($"  {sectionName}.data: NOT FOUND");
                        }
                    }
                }
                
                File.WriteAllText(logPath, TextEncodingNormalizer.NormalizeIfCorrupted(logSb.ToString()), Encoding.UTF8);
                App.LogMessage($"[UnifiedReport] Debug log written to {logPath}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[UnifiedReport] Failed to write debug log: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Cherche une propriété avec tolérance: essaie les noms exacts d'abord,
        /// puis fait une recherche case-insensitive en fallback.
        /// </summary>
        private static bool TryGetPropertyRobust(JsonElement element, out JsonElement value, params string[] propertyNames)
        {
            value = default;

            if (element.ValueKind != JsonValueKind.Object)
                return false;

            // Pass 1: Try exact matches first
            foreach (var name in propertyNames)
            {
                if (element.TryGetProperty(name, out value))
                    return true;
            }

            // Pass 2: Case-insensitive fallback - iterate all properties
            foreach (var prop in element.EnumerateObject())
            {
                foreach (var name in propertyNames)
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
        
        /// <summary>
        /// Cherche une propriété avec tolérance case-insensitive + alias multiples.
        /// Retourne le nom trouvé pour debug.
        /// </summary>
        private static bool TryGetPropertyRobustWithName(JsonElement element, out JsonElement value, out string? foundName, params string[] propertyNames)
        {
            value = default;
            foundName = null;

            if (element.ValueKind != JsonValueKind.Object)
                return false;

            // Pass 1: Try exact matches first
            foreach (var name in propertyNames)
            {
                if (element.TryGetProperty(name, out value))
                {
                    foundName = name;
                    return true;
                }
            }

            // Pass 2: Case-insensitive fallback
            foreach (var prop in element.EnumerateObject())
            {
                foreach (var name in propertyNames)
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        foundName = prop.Name;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Trouve le fichier TXT PowerShell le plus récent dans le dossier de rapports.
        /// </summary>
        public static string? FindLatestPsTxtReport(string reportsDir)
        {
            if (string.IsNullOrEmpty(reportsDir) || !Directory.Exists(reportsDir))
                return null;

            var patterns = new[] { "Scan_*.txt", "Rapport*.txt", "*_report.txt" };

            foreach (var pattern in patterns)
            {
                var files = Directory.GetFiles(reportsDir, pattern, SearchOption.TopDirectoryOnly);
                if (files.Length > 0)
                {
                    return files.OrderByDescending(f => File.GetLastWriteTime(f)).First();
                }
            }

            return null;
        }

        #endregion
    }
}

