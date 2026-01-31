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
                BuildSection6_StockageDisques(sb, psData, sensors);

                // Section 7: Températures et Refroidissement
                BuildSection7_Temperatures(sb, sensors, psData);

                // Section 8: Batterie et Alimentation
                BuildSection8_Batterie(sb, psData);

                // Section 9: Réseau et Internet
                BuildSection9_Reseau(sb, psData, combinedRoot);

                // Section 10: Sécurité
                BuildSection10_Securite(sb, psData);

                // Section 11: Mises à jour
                BuildSection11_MisesAJour(sb, psData);

                // Section 12: Pilotes (Drivers)
                BuildSection12_Pilotes(sb, psData);

                // Section 13: Démarrage et Applications
                BuildSection13_Demarrage(sb, psData);

                // Section 14: Santé système et Erreurs
                BuildSection14_SanteSysteme(sb, psData, healthReport, combinedRoot);

                // Section 15: Périphériques
                BuildSection15_Peripheriques(sb, psData);

                // Footer
                BuildFooter(sb);

                // Écrire le fichier
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
                App.LogMessage($"[UnifiedReport] TXT unifié généré: {outputPath}");

                // === VALIDATION: Vérifier que le rapport unifié est un SUPERSET du PS brut ===
                await ValidateReportCompletenessAsync(sb.ToString(), originalTxtPath, combinedJsonPath);

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
        private static async Task ValidateReportCompletenessAsync(string unifiedContent, string? psTxtPath, string combinedJsonPath)
        {
            try
            {
                var missingCategories = new List<string>();
                
                // 1. Vérifier les catégories clés présentes dans PS JSON
                if (File.Exists(combinedJsonPath))
                {
                    var jsonContent = await File.ReadAllTextAsync(combinedJsonPath, Encoding.UTF8);
                    using var doc = JsonDocument.Parse(jsonContent);
                    var root = doc.RootElement;
                    
                    // Chercher psData
                    JsonElement psData = default;
                    if (TryGetPropertyRobust(root, out psData, "scan_powershell", "scanPowershell") && 
                        psData.TryGetProperty("sections", out var sectionsEl))
                    {
                        // Liste des sections PS attendues
                        var psSectionNames = new[] {
                            "CPU", "Memory", "Storage", "GPU", "Network", "Security", "Services",
                            "StartupPrograms", "HealthChecks", "EventLogs", "WindowsUpdate", "Audio",
                            "DevicesDrivers", "InstalledApplications", "PerformanceCounters", 
                            "DynamicSignals", "Processes", "Battery", "Printers"
                        };
                        
                        foreach (var sectionName in psSectionNames)
                        {
                            if (sectionsEl.TryGetProperty(sectionName, out var sectionEl) && 
                                sectionEl.TryGetProperty("data", out var dataEl) &&
                                dataEl.ValueKind != JsonValueKind.Null)
                            {
                                // Cette section PS a des données - vérifier si le rapport unifié en parle
                                bool hasDataInUnified = CheckSectionInUnified(unifiedContent, sectionName);
                                if (!hasDataInUnified)
                                {
                                    missingCategories.Add(sectionName);
                                }
                            }
                        }
                    }
                    
                    // Vérifier process_telemetry
                    if (TryGetPropertyRobust(root, out var procTel, "process_telemetry", "processTelemetry"))
                    {
                        if (TryGetPropertyRobust(procTel, out var avail, "Available", "available") && 
                            avail.ValueKind == JsonValueKind.True)
                        {
                            if (!unifiedContent.Contains("Top 5 Processus") && !unifiedContent.Contains("Top processus"))
                            {
                                missingCategories.Add("ProcessTelemetry");
                            }
                        }
                    }
                    
                    // Vérifier network_diagnostics
                    if (TryGetPropertyRobust(root, out var netDiag, "network_diagnostics", "networkDiagnostics"))
                    {
                        if (!unifiedContent.Contains("RÉSULTATS TEST HTTP") && !unifiedContent.Contains("Débit réseau"))
                        {
                            missingCategories.Add("NetworkDiagnostics");
                        }
                    }
                }
                
                // Loguer les résultats
                if (missingCategories.Count > 0)
                {
                    App.LogMessage($"[VALIDATION] ⚠️ ATTENTION: {missingCategories.Count} catégories PS non représentées dans le rapport unifié:");
                    foreach (var cat in missingCategories)
                    {
                        App.LogMessage($"  - {cat}");
                    }
                }
                else
                {
                    App.LogMessage("[VALIDATION] ✅ Rapport unifié complet - toutes les données PS sont représentées");
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[VALIDATION] Erreur validation: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Vérifie si une section PS est représentée dans le rapport unifié
        /// </summary>
        private static bool CheckSectionInUnified(string unifiedContent, string psSectionName)
        {
            // Mapping des sections PS vers les termes attendus dans le rapport unifié
            var mappings = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "CPU", new[] { "CPU", "Fréquence", "Charge CPU" } },
                { "Memory", new[] { "RAM", "Mémoire", "MÉMOIRE" } },
                { "Storage", new[] { "Disque", "Stockage", "STOCKAGE" } },
                { "GPU", new[] { "GPU", "Graphique", "VRAM" } },
                { "Network", new[] { "Réseau", "RÉSEAU", "IP", "Gateway" } },
                { "Security", new[] { "Sécurité", "SÉCURITÉ", "Defender", "Firewall" } },
                { "Services", new[] { "Services", "service" } },
                { "StartupPrograms", new[] { "Démarrage", "DÉMARRAGE", "startup" } },
                { "HealthChecks", new[] { "Santé", "SANTÉ", "Redémarrage" } },
                { "EventLogs", new[] { "Événements", "BSOD", "EventLog" } },
                { "WindowsUpdate", new[] { "Update", "Mise à jour", "MISES À JOUR" } },
                { "Audio", new[] { "Audio", "audio", "Son" } },
                { "DevicesDrivers", new[] { "Pilotes", "PILOTES", "Périph" } },
                { "InstalledApplications", new[] { "Applications", "installé" } },
                { "PerformanceCounters", new[] { "Performance", "PERFORMANCE", "CPU", "Disque" } },
                { "DynamicSignals", new[] { "Performance", "CPU", "topCpu" } },
                { "Processes", new[] { "Processus", "Top 5", "processus" } },
                { "Battery", new[] { "Batterie", "BATTERIE", "Alimentation" } },
                { "Printers", new[] { "Imprimante", "imprimante" } }
            };
            
            if (mappings.TryGetValue(psSectionName, out var keywords))
            {
                return keywords.Any(kw => unifiedContent.Contains(kw, StringComparison.OrdinalIgnoreCase));
            }
            
            // Si pas de mapping, chercher le nom brut
            return unifiedContent.Contains(psSectionName, StringComparison.OrdinalIgnoreCase);
        }

        #region Section 1: Résumé global

        private static void BuildSection1_ResumeGlobal(StringBuilder sb, HealthReport? healthReport, HardwareSensorsResult? sensors)
        {
            sb.AppendLine(SEPARATOR);
            sb.AppendLine("                    PC DIAGNOSTIC PRO — RAPPORT UNIFIÉ");
            sb.AppendLine(SEPARATOR);
            sb.AppendLine();
            sb.AppendLine("  ▶ SECTION 1 : RÉSUMÉ GLOBAL");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            var rows = new List<(string field, string value)>();

            // Score et statut
            if (healthReport != null)
            {
                var emoji = healthReport.GlobalScore >= 90 ? "✅" :
                            healthReport.GlobalScore >= 70 ? "⚠️" :
                            healthReport.GlobalScore >= 50 ? "🔶" : "❌";
                var status = healthReport.GlobalScore >= 90 ? "OK" :
                             healthReport.GlobalScore >= 70 ? "Avertissement" :
                             healthReport.GlobalScore >= 50 ? "Dégradé" : "Critique";

                rows.Add(("Score santé global", $"{healthReport.GlobalScore}/100 (Grade {healthReport.Grade})"));
                rows.Add(("Statut", $"{emoji} {status}"));

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
                        var maxSpeedMhz = firstCpu.TryGetProperty("maxClockSpeed", out var mcs) ? mcs.GetDouble() : -1;
                        var currentSpeedMhz = firstCpu.TryGetProperty("currentClockSpeed", out var ccs) ? ccs.GetDouble() : maxSpeedMhz;
                        if (maxSpeedMhz > 0)
                        {
                            rows.Add(("Fréquence CPU (max)", $"{maxSpeedMhz / 1000:F2} GHz"));
                            if (currentSpeedMhz > 0 && Math.Abs(currentSpeedMhz - maxSpeedMhz) > 1)
                                rows.Add(("Fréquence CPU (instantanée)", $"{currentSpeedMhz / 1000:F2} GHz"));
                        }
                        cpuUsage = firstCpu.TryGetProperty("currentLoad", out var cl) ? cl.GetDouble() : -1;
                    }
                }
                if (cpuUsage < 0) cpuUsage = GetNestedDouble(psData.Value, "sections", "CPUInfo", "data", "LoadPercentage");
                if (cpuUsage < 0)
                {
                    var dynSignals = GetNestedElement(psData.Value, "sections", "DynamicSignals", "data");
                    if (dynSignals.HasValue && dynSignals.Value.TryGetProperty("cpu", out var cpuEl))
                        cpuUsage = cpuEl.TryGetProperty("average", out var avg) ? avg.GetDouble() : -1;
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
                        usedRam = memEl.TryGetProperty("usedPercent", out var up) ? up.GetDouble() : -1;
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
                        var mbps = dm.GetDouble();
                        if (mbps > 0) rows.Add(("Débit réseau (test HTTP)", $"{mbps:F1} Mbps"));
                    }
                }
            }
            if (rows.All(r => !r.field.Contains("réseau")) && psData.HasValue)
            {
                var dynData = GetNestedElement(psData.Value, "sections", "DynamicSignals", "data");
                if (dynData.HasValue && dynData.Value.TryGetProperty("network", out var netEl) && netEl.TryGetProperty("throughputMbps", out var tm))
                {
                    var mbps = tm.GetDouble();
                    if (mbps >= 0) rows.Add(("Débit réseau (samples)", $"{mbps:F1} Mbps"));
                }
            }

            WriteTable(sb, rows);
            sb.AppendLine();

            // Top 5 CPU et Top 5 RAM - process_telemetry (PascalCase) ou scan_powershell.sections.Processes/DynamicSignals
            JsonElement? topCpuArr = null;
            JsonElement? topMemArr = null;
            if (combinedRoot.HasValue)
            {
                JsonElement procTelemetry = default;
                if (TryGetPropertyRobust(combinedRoot.Value, out procTelemetry, "process_telemetry", "processTelemetry"))
                {
                    if (TryGetPropertyRobust(procTelemetry, out var topCpu, "TopByCpu", "topByCpu"))
                        topCpuArr = topCpu;
                    if (TryGetPropertyRobust(procTelemetry, out var topMem, "TopByMemory", "topByMemory"))
                        topMemArr = topMem;
                }
            }
            if (!topCpuArr.HasValue && !topMemArr.HasValue && psData.HasValue)
            {
                var dynData = GetNestedElement(psData.Value, "sections", "DynamicSignals", "data");
                if (dynData.HasValue)
                {
                    if (dynData.Value.TryGetProperty("topCpu", out var tc)) topCpuArr = tc;
                    if (dynData.Value.TryGetProperty("topMemory", out var tm)) topMemArr = tm;
                }
            }
            if (!topCpuArr.HasValue && psData.HasValue)
            {
                var procData = GetNestedElement(psData.Value, "sections", "Processes", "data");
                if (procData.HasValue && procData.Value.TryGetProperty("topCpu", out var tc)) topCpuArr = tc;
            }

            if (topCpuArr.HasValue && topCpuArr.Value.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine("  Top 5 Processus (CPU):");
                sb.AppendLine("  ┌────────────────────────────┬──────────┬────────────┐");
                sb.AppendLine("  │ Processus                  │ CPU %    │ RAM (MB)   │");
                sb.AppendLine("  ├────────────────────────────┼──────────┼────────────┤");
                int count = 0;
                foreach (var proc in topCpuArr.Value.EnumerateArray())
                {
                    if (count++ >= 5) break;
                    var name = TryGetString(proc, "Name", "name") ?? "?";
                    var cpuPct = proc.TryGetProperty("CpuPercent", out var c) ? c.GetDouble() : proc.TryGetProperty("cpuPercent", out var c2) ? c2.GetDouble() : 0;
                    var ram = proc.TryGetProperty("WorkingSetMB", out var r) ? r.GetDouble() : proc.TryGetProperty("workingSetMB", out var r2) ? r2.GetDouble() : proc.TryGetProperty("memoryMB", out var m) ? m.GetDouble() : 0;
                    name = name.Length > 26 ? name.Substring(0, 23) + "..." : name;
                    sb.AppendLine($"  │ {name,-26} │ {cpuPct,7:F1}% │ {ram,9:F1} │");
                }
                sb.AppendLine("  └────────────────────────────┴──────────┴────────────┘");
                sb.AppendLine();
            }
            if (topMemArr.HasValue && topMemArr.Value.ValueKind == JsonValueKind.Array)
            {
                sb.AppendLine("  Top 5 Processus (RAM):");
                sb.AppendLine("  ┌────────────────────────────┬────────────┬──────────┐");
                sb.AppendLine("  │ Processus                  │ RAM (MB)   │ CPU %    │");
                sb.AppendLine("  ├────────────────────────────┼────────────┼──────────┤");
                int count = 0;
                foreach (var proc in topMemArr.Value.EnumerateArray())
                {
                    if (count++ >= 5) break;
                    var name = TryGetString(proc, "Name", "name") ?? "?";
                    var ram = proc.TryGetProperty("WorkingSetMB", out var r) ? r.GetDouble() : proc.TryGetProperty("workingSetMB", out var r2) ? r2.GetDouble() : proc.TryGetProperty("memoryMB", out var m) ? m.GetDouble() : 0;
                    var cpuPct = proc.TryGetProperty("CpuPercent", out var c) ? c.GetDouble() : proc.TryGetProperty("cpuPercent", out var c2) ? c2.GetDouble() : 0;
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

        private static void BuildSection6_StockageDisques(StringBuilder sb, JsonElement? psData, HardwareSensorsResult? sensors)
        {
            sb.AppendLine("  ▶ SECTION 6 : STOCKAGE ET DISQUES");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            // Disques logiques (partitions) - PS keys: Storage, DiskInfo
            if (psData.HasValue && psData.Value.TryGetProperty("sections", out var sections))
            {
                JsonElement diskData = default;
                if (sections.TryGetProperty("Storage", out var storageEl) && storageEl.TryGetProperty("data", out diskData)) { }
                else if (sections.TryGetProperty("DiskInfo", out var diskInfo) && diskInfo.TryGetProperty("data", out diskData)) { }
                
                if (diskData.ValueKind == JsonValueKind.Array || diskData.ValueKind == JsonValueKind.Object)
                {
                    sb.AppendLine("  Partitions:");
                    sb.AppendLine("  ┌───────┬────────────┬────────────┬────────────┬──────────┐");
                    sb.AppendLine("  │ Lettre│ Capacité   │ Libre      │ Utilisé    │ Alerte   │");
                    sb.AppendLine("  ├───────┼────────────┼────────────┼────────────┼──────────┤");

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
                            var sizeGb = vol.TryGetProperty("totalGB", out var s) ? s.GetDouble() : 
                                         vol.TryGetProperty("SizeGB", out var s2) ? s2.GetDouble() : 0;
                            var freeGb = vol.TryGetProperty("freeGB", out var f) ? f.GetDouble() : 
                                         vol.TryGetProperty("FreeSpaceGB", out var f2) ? f2.GetDouble() : 0;
                            var usedGb = sizeGb - freeGb;
                            var freePct = sizeGb > 0 ? (freeGb / sizeGb * 100) : 0;
                            var alert = freePct < 15 ? "⚠️ <15%" : "OK";

                            sb.AppendLine($"  │ {letter,-5} │ {sizeGb,8:F1} GB │ {freeGb,8:F1} GB │ {usedGb,8:F1} GB │ {alert,-8} │");
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
                        var freePct = sizeGb > 0 ? (freeGb / sizeGb * 100) : 0;
                        var alert = freePct < 15 ? "⚠️ <15%" : "OK";
                        sb.AppendLine($"  │ {letter,-5} │ {sizeGb,8:F1} GB │ {freeGb,8:F1} GB │ {usedGb,8:F1} GB │ {alert,-8} │");
                    }

                    sb.AppendLine("  └───────┴────────────┴────────────┴────────────┴──────────┘");
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
                    rows.Add(("Temp CPU", $"Non disponible ({cpuValid.Reason ?? "capteur absent"})"));

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

            // Résultats complets du test HTTP (speedtest.tele2.net / proof.ovh.net)
            if (combinedRoot.HasValue && TryGetPropertyRobust(combinedRoot.Value, out var netDiagEl, "network_diagnostics", "networkDiagnostics"))
            {
                sb.AppendLine("  ═ RÉSULTATS TEST HTTP VITESSE ═");
                sb.AppendLine();
                
                if (netDiagEl.TryGetProperty("throughput", out var throughput))
                {
                    var downMbps = throughput.TryGetProperty("downloadMbpsMedian", out var dm) ? dm.GetDouble() : -1;
                    var upMbps = throughput.TryGetProperty("uploadMbpsMedian", out var um) ? um.GetDouble() : -1;
                    
                    sb.AppendLine("  Débit mesuré :");
                    if (downMbps > 0 && !double.IsNaN(downMbps))
                    {
                        sb.AppendLine($"    Débit descendant : {downMbps:F1} Mbps");
                        var verdict = downMbps >= 100 ? "Excellente" : downMbps >= 20 ? "Bonne" : downMbps >= 5 ? "Moyenne" : "Lente";
                        sb.AppendLine($"    Verdict          : {verdict}");
                    }
                    if (upMbps > 0 && !double.IsNaN(upMbps))
                        sb.AppendLine($"    Débit montant    : {upMbps:F1} Mbps");
                    if (downMbps <= 0 && throughput.TryGetProperty("reason", out var tr))
                        sb.AppendLine($"    Non disponible   : {tr.GetString() ?? "—"}");
                    sb.AppendLine("  Source : speedtest.tele2.net / proof.ovh.net");
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

            // Basic network info from PS (sections.Network.data.adapters[] avec name, ip[], gateway[])
            if (psData.HasValue)
            {
                var netData = GetNestedElement(psData.Value, "sections", "Network", "data");
                if (!netData.HasValue) netData = GetNestedElement(psData.Value, "sections", "NetworkInfo", "data");
                
                if (netData.HasValue)
                {
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
                            if (!string.IsNullOrEmpty(ip)) rows.Add(("IP", ip));
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
                            if (!string.IsNullOrEmpty(ip)) rows.Add(("IP", ip));
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

            if (!foundData || rows.Count == 0)
                rows.Add(("Sécurité", "Données non disponibles"));

            WriteTable(sb, rows);
            sb.AppendLine();
        }

        #endregion

        #region Section 11: Mises à jour

        private static void BuildSection11_MisesAJour(StringBuilder sb, JsonElement? psData)
        {
            sb.AppendLine("  ▶ SECTION 11 : MISES À JOUR");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            var rows = new List<(string field, string value)>();
            bool foundData = false;

            if (psData.HasValue)
            {
                // PS: sections.WindowsUpdate.data a pendingCount (pas PendingUpdatesCount)
                var updatePaths = new[]
                {
                    GetNestedElement(psData.Value, "sections", "WindowsUpdate", "data"),
                    GetNestedElement(psData.Value, "sections", "WindowsUpdateInfo", "data"),
                    GetNestedElement(psData.Value, "sections", "Updates", "data"),
                    GetNestedElement(psData.Value, "WindowsUpdate"),
                    GetNestedElement(psData.Value, "Updates")
                };
                
                JsonElement? updateData = null;
                foreach (var path in updatePaths)
                {
                    if (path.HasValue && path.Value.ValueKind == JsonValueKind.Object)
                    {
                        updateData = path;
                        break;
                    }
                }
                
                if (updateData.HasValue)
                {
                    foundData = true;
                    // PS utilise pendingCount (lowercase)
                    var pending = TryGetInt(updateData.Value, "pendingCount", "PendingCount", "PendingUpdatesCount", "Pending");
                    if (pending >= 0)
                        rows.Add(("Updates en attente", pending.ToString()));
                    
                    // Last update date
                    var lastUpdate = TryGetString(updateData.Value, "lastUpdateDate", "LastUpdateDate", "LastInstalled", "LastCheck");
                    if (!string.IsNullOrEmpty(lastUpdate))
                        rows.Add(("Dernière mise à jour", lastUpdate));
                    
                    // Update errors
                    var errors = TryGetInt(updateData.Value, "failedCount", "FailedUpdatesCount", "ErrorCount", "FailedCount");
                    if (errors > 0)
                        rows.Add(("⚠️ Échecs récents", errors.ToString()));
                    
                    // Auto update status
                    var autoUpdate = TryGetBool(updateData.Value, "autoUpdateEnabled", "AutoUpdateEnabled", "AutoUpdate");
                    if (autoUpdate.HasValue)
                        rows.Add(("Mise à jour auto", autoUpdate.Value ? "Activée" : "Désactivée"));
                }
                
                // rebootRequired est dans HealthChecks, pas WindowsUpdate
                var healthData = GetNestedElement(psData.Value, "sections", "HealthChecks", "data");
                if (healthData.HasValue)
                {
                    foundData = true;
                    var reboot = TryGetBool(healthData.Value, "rebootRequired", "RebootRequired", "RebootPending", "NeedsReboot");
                    if (reboot.HasValue)
                        rows.Add(("Redémarrage requis", reboot.Value ? "⚠️ OUI" : "Non"));
                    if (reboot == true && healthData.Value.TryGetProperty("rebootReasons", out var reasons) && reasons.ValueKind == JsonValueKind.Array)
                    {
                        var reasonsList = reasons.EnumerateArray().Select(r => r.GetString()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                        if (reasonsList.Count > 0)
                            rows.Add(("  Raisons", string.Join(", ", reasonsList)));
                    }
                }
            }

            if (!foundData || rows.Count == 0)
                rows.Add(("Mises à jour", "Données non disponibles"));

            WriteTable(sb, rows);
            sb.AppendLine();
        }
        
        /// <summary>Helper: essaie plusieurs noms de propriétés pour un booléen</summary>
        private static bool? TryGetBool(JsonElement el, params string[] names)
        {
            foreach (var name in names)
            {
                if (el.TryGetProperty(name, out var prop))
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
                if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    var val = prop.GetString();
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
                if (el.TryGetProperty(name, out var prop))
                {
                    if (prop.ValueKind == JsonValueKind.Number) return prop.GetInt32();
                    if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var i)) return i;
                }
            }
            return -1;
        }

        #endregion

        #region Section 12: Pilotes

        private static void BuildSection12_Pilotes(StringBuilder sb, JsonElement? psData)
        {
            sb.AppendLine("  ▶ SECTION 12 : PILOTES (DRIVERS)");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            var rows = new List<(string field, string value)>();
            bool foundData = false;

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
                    var shortDate = string.IsNullOrEmpty(date) ? "—" : (date.Length > 10 ? date.Substring(0, 10) : date);
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

        private static void BuildSection13_Demarrage(StringBuilder sb, JsonElement? psData)
        {
            sb.AppendLine("  ▶ SECTION 13 : DÉMARRAGE ET APPLICATIONS");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            bool foundData = false;

            if (psData.HasValue)
            {
                // PS: sections.StartupPrograms.data a startupItems (array) et startupCount
                var startupObjData = GetNestedElement(psData.Value, "sections", "StartupPrograms", "data");
                JsonElement? startupItemsArr = null;
                int startupCount = 0;
                
                if (startupObjData.HasValue)
                {
                    foundData = true;
                    startupCount = TryGetInt(startupObjData.Value, "startupCount", "StartupCount");
                    if (startupObjData.Value.TryGetProperty("startupItems", out var si) && si.ValueKind == JsonValueKind.Array)
                        startupItemsArr = si;
                }
                
                // Fallback: anciens chemins (si data est directement array)
                if (!startupItemsArr.HasValue)
                {
                    var startupPaths = new[]
                    {
                        GetNestedElement(psData.Value, "sections", "StartupInfo", "data"),
                        GetNestedElement(psData.Value, "sections", "Startup", "data"),
                        GetNestedElement(psData.Value, "StartupPrograms"),
                        GetNestedElement(psData.Value, "Startup")
                    };
                    foreach (var path in startupPaths)
                    {
                        if (path.HasValue && path.Value.ValueKind == JsonValueKind.Array)
                        {
                            startupItemsArr = path;
                            foundData = true;
                            break;
                        }
                    }
                }
                
                if (startupItemsArr.HasValue)
                {
                    foundData = true;
                    var items = startupItemsArr.Value.EnumerateArray().ToList();
                    
                    var rows = new List<(string field, string value)>();
                    rows.Add(("Total programmes démarrage", (startupCount > 0 ? startupCount : items.Count).ToString()));
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
                        // PS structure: { name, scope }
                        var name = TryGetString(item, "name", "Name", "DisplayName", "Command") ?? "?";
                        var scope = TryGetString(item, "scope", "Scope") ?? "N/A";
                        name = name.Length > 38 ? name.Substring(0, 35) + "..." : name;
                        scope = scope.Length > 10 ? scope.Substring(0, 10) : scope;
                        sb.AppendLine($"  │ {name,-38} │ {scope,-10} │");
                    }
                    sb.AppendLine("  └────────────────────────────────────────┴────────────┘");
                    
                    // Suggestion si beaucoup de programmes
                    if (items.Count > 10)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"  💡 Suggestion: {items.Count} programmes au démarrage.");
                        sb.AppendLine("     Désactivez ceux non essentiels pour accélérer le boot.");
                    }
                }
                else if (startupCount > 0)
                {
                    foundData = true;
                    var rows = new List<(string field, string value)>();
                    rows.Add(("Total programmes démarrage", startupCount.ToString()));
                    WriteTable(sb, rows);
                }
                
                // Services si disponibles
                var servicesPaths = new[]
                {
                    GetNestedElement(psData.Value, "sections", "ServicesInfo", "data"),
                    GetNestedElement(psData.Value, "sections", "Services", "data"),
                    GetNestedElement(psData.Value, "ServicesInfo"),
                    GetNestedElement(psData.Value, "Services")
                };
                
                JsonElement? servicesData = null;
                foreach (var path in servicesPaths)
                {
                    if (path.HasValue && (path.Value.ValueKind == JsonValueKind.Array || path.Value.ValueKind == JsonValueKind.Object))
                    {
                        servicesData = path;
                        break;
                    }
                }
                
                if (servicesData.HasValue)
                {
                    foundData = true;
                    sb.AppendLine();
                    
                    if (servicesData.Value.ValueKind == JsonValueKind.Object)
                    {
                        // Statistiques de services
                        var total = TryGetInt(servicesData.Value, "TotalServices", "Total");
                        var running = TryGetInt(servicesData.Value, "RunningServices", "Running");
                        var stopped = TryGetInt(servicesData.Value, "StoppedServices", "Stopped");
                        var auto = TryGetInt(servicesData.Value, "AutoStartServices", "AutoStart");
                        
                        if (total > 0 || running > 0)
                        {
                            var svcRows = new List<(string field, string value)>();
                            svcRows.Add(("═ Services Windows ═", ""));
                            if (total > 0) svcRows.Add(("Total services", total.ToString()));
                            if (running > 0) svcRows.Add(("En cours", running.ToString()));
                            if (stopped > 0) svcRows.Add(("Arrêtés", stopped.ToString()));
                            if (auto > 0) svcRows.Add(("Démarrage auto", auto.ToString()));
                            WriteTable(sb, svcRows);
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

            if (!foundData)
            {
                sb.AppendLine("  Programmes au démarrage : Données non disponibles");
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
        }

        #endregion

        #region Section 15: Périphériques

        private static void BuildSection15_Peripheriques(StringBuilder sb, JsonElement? psData)
        {
            sb.AppendLine("  ▶ SECTION 15 : PÉRIPHÉRIQUES");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            var rows = new List<(string field, string value)>();
            bool foundData = false;

            if (psData.HasValue)
            {
                // Audio: sections.Audio.data avec devices (array) et deviceCount
                var audioData = GetNestedElement(psData.Value, "sections", "Audio", "data");
                if (audioData.HasValue)
                {
                    foundData = true;
                    var audioCount = TryGetInt(audioData.Value, "deviceCount", "DeviceCount");
                    if (audioCount < 0 && audioData.Value.TryGetProperty("devices", out var devArr) && devArr.ValueKind == JsonValueKind.Array)
                        audioCount = devArr.EnumerateArray().Count();
                    if (audioCount >= 0)
                        rows.Add(("Périphériques audio", audioCount.ToString()));
                }

                // Printers: sections.Printers.data avec printers (array) et printerCount
                var printerData = GetNestedElement(psData.Value, "sections", "Printers", "data");
                if (printerData.HasValue)
                {
                    foundData = true;
                    var printerCount = TryGetInt(printerData.Value, "printerCount", "PrinterCount");
                    if (printerCount < 0 && printerData.Value.TryGetProperty("printers", out var pArr) && pArr.ValueKind == JsonValueKind.Array)
                        printerCount = pArr.EnumerateArray().Count();
                    if (printerCount >= 0)
                        rows.Add(("Imprimantes", printerCount.ToString()));
                    
                    // Afficher les imprimantes
                    if (printerData.Value.TryGetProperty("printers", out var printersEl) && printersEl.ValueKind == JsonValueKind.Array)
                    {
                        var printerList = printersEl.EnumerateArray().Take(5).ToList();
                        if (printerList.Count > 0)
                        {
                            foreach (var p in printerList)
                            {
                                var name = TryGetString(p, "name", "Name") ?? "?";
                                var isDefault = TryGetBool(p, "default", "Default") ?? false;
                                rows.Add(($"  • {(isDefault ? "⭐ " : "")}{name.Substring(0, Math.Min(30, name.Length))}", ""));
                            }
                        }
                    }
                }

                // DevicesDrivers: sections.DevicesDrivers.data.problemDevices (périphériques en erreur)
                var devDriversData = GetNestedElement(psData.Value, "sections", "DevicesDrivers", "data");
                if (devDriversData.HasValue)
                {
                    foundData = true;
                    var problemCount = TryGetInt(devDriversData.Value, "problemDeviceCount", "ProblemDeviceCount");
                    if (problemCount >= 0)
                        rows.Add(("Périph. en erreur", problemCount > 0 ? $"⚠️ {problemCount}" : "0 ✅"));
                    
                    // Liste des périphériques en erreur
                    if (problemCount > 0 && devDriversData.Value.TryGetProperty("problemDevices", out var problemArr) && problemArr.ValueKind == JsonValueKind.Array)
                    {
                        rows.Add(("", ""));
                        sb.AppendLine("  Périphériques problématiques:");
                        foreach (var dev in problemArr.EnumerateArray().Take(5))
                        {
                            var name = TryGetString(dev, "name", "Name") ?? "?";
                            var status = TryGetString(dev, "status", "Status") ?? "?";
                            var cls = TryGetString(dev, "class", "Class") ?? "";
                            sb.AppendLine($"    [{status}] {name.Substring(0, Math.Min(35, name.Length))} ({cls})");
                        }
                        sb.AppendLine();
                    }
                }
                
                // Bluetooth si disponible
                var btData = GetNestedElement(psData.Value, "sections", "Bluetooth", "data");
                if (!btData.HasValue) btData = GetNestedElement(psData.Value, "sections", "BluetoothInfo", "data");
                if (btData.HasValue && btData.Value.ValueKind == JsonValueKind.Array)
                {
                    foundData = true;
                    var btCount = btData.Value.EnumerateArray().Count();
                    rows.Add(("Périphériques Bluetooth", btCount.ToString()));
                }
            }

            if (!foundData || rows.Count == 0)
                rows.Add(("Périphériques", "Données non disponibles"));

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
            sb.AppendLine("  PC Diagnostic PRO — Rapport Unifié v2.0");
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
                            result.Add((devClass, name, version ?? "—", date));
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
                if (!current.TryGetProperty(key, out current))
                    return -1;
            }
            return current.ValueKind == JsonValueKind.Number ? current.GetInt32() : -1;
        }

        private static double GetNestedDouble(JsonElement root, params string[] path)
        {
            var current = root;
            foreach (var key in path)
            {
                if (!current.TryGetProperty(key, out current))
                    return -1;
            }
            return current.ValueKind == JsonValueKind.Number ? current.GetDouble() : -1;
        }

        private static JsonElement? GetNestedElement(JsonElement root, params string[] path)
        {
            var current = root;
            foreach (var key in path)
            {
                if (!current.TryGetProperty(key, out current))
                    return null;
            }
            return current;
        }

        private static bool TryGetPropertyRobust(JsonElement element, out JsonElement value, params string[] propertyNames)
        {
            value = default;

            if (element.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var name in propertyNames)
            {
                if (element.TryGetProperty(name, out value))
                    return true;
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
