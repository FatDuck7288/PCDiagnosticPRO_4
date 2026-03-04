using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Construit un HealthReport depuis le JSON PowerShell + capteurs C#.
    /// Source de vérité: UDIS (étape 7 de Build()).
    /// </summary>
    public static class HealthReportBuilder
    {
        /// <summary>
        /// Mapping des sections JSON vers les domaines de santé
        /// Mapping complet PS sections → domaines de santé (Schema 2.3.0)
        /// </summary>
        private static readonly Dictionary<string, HealthDomain> SectionToDomain = new(StringComparer.OrdinalIgnoreCase)
        {
            // OS
            { "OS", HealthDomain.OS },
            { "MachineIdentity", HealthDomain.PlatformFirmware },
            { "SystemInfo", HealthDomain.PlatformFirmware },
            { "WindowsUpdate", HealthDomain.OS },
            { "SystemIntegrity", HealthDomain.OS },
            { "UserProfiles", HealthDomain.OS },
            { "EnvironmentVariables", HealthDomain.OS },
            { "Virtualization", HealthDomain.OS },
            { "Registry", HealthDomain.OS },
            
            // Security (new domain)
            { "Security", HealthDomain.Security },
            { "Certificates", HealthDomain.Security },
            
            // CPU
            { "CPU", HealthDomain.CPU },
            { "Temperatures", HealthDomain.CPU },
            
            // GPU
            { "GPU", HealthDomain.GPU },
            
            // RAM
            { "Memory", HealthDomain.RAM },
            
            // Storage
            { "Storage", HealthDomain.Storage },
            { "SmartDetails", HealthDomain.Storage },
            { "TempFiles", HealthDomain.Storage },
            
            // Network
            { "Network", HealthDomain.Network },
            { "NetworkLatency", HealthDomain.Network },
            
            // System Stability
            { "EventLogs", HealthDomain.SystemStability },
            { "ReliabilityHistory", HealthDomain.SystemStability },
            { "MinidumpAnalysis", HealthDomain.SystemStability },
            { "RestorePoints", HealthDomain.SystemStability },
            { "Services", HealthDomain.SystemStability },
            
            // Drivers
            { "DevicesDrivers", HealthDomain.Drivers },
            { "Audio", HealthDomain.Drivers },
            { "Printers", HealthDomain.Drivers },
            
            // Applications (new domain)
            { "StartupPrograms", HealthDomain.Applications },
            { "InstalledApplications", HealthDomain.Applications },
            { "ScheduledTasks", HealthDomain.Applications },
            
            // Performance (new domain)
            { "Processes", HealthDomain.Performance },
            { "PerformanceCounters", HealthDomain.Performance },
            { "DynamicSignals", HealthDomain.Performance },
            { "AdvancedAnalysis", HealthDomain.Performance },
            
            // Power (new domain)
            { "Battery", HealthDomain.Power },
            { "PowerSettings", HealthDomain.Power }
        };

        /// <summary>
        /// Icônes par domaine (extended with new domains)
        /// </summary>
        private static readonly Dictionary<HealthDomain, string> DomainIcons = new()
        {
            { HealthDomain.OS, "🖥️" },
            { HealthDomain.CPU, "\u26A1" }, // ⚡ (encoding-safe)
            { HealthDomain.GPU, "🎮" },
            { HealthDomain.RAM, "🧠" },
            { HealthDomain.Storage, "💾" },
            { HealthDomain.Network, "🌐" },
            { HealthDomain.SystemStability, "🛡️" },
            { HealthDomain.Drivers, "🔧" },
            { HealthDomain.Applications, "📦" },
            { HealthDomain.Performance, "📊" },
            { HealthDomain.Security, "🔒" },
            { HealthDomain.PlatformFirmware, "🧬" },
            { HealthDomain.Power, "🔋" }
        };

        /// <summary>
        /// Noms affichés par domaine (extended with new domains)
        /// </summary>
        private static readonly Dictionary<HealthDomain, string> DomainDisplayNames = new()
        {
            { HealthDomain.OS, "Système d'exploitation" },
            { HealthDomain.CPU, "Processeur" },
            { HealthDomain.GPU, "Carte graphique" },
            { HealthDomain.RAM, "Mémoire vive" },
            { HealthDomain.Storage, "Stockage" },
            { HealthDomain.Network, "Réseau" },
            { HealthDomain.SystemStability, "Stabilité système" },
            { HealthDomain.Drivers, "Pilotes" },
            { HealthDomain.Applications, "Applications" },
            { HealthDomain.Performance, "Performance" },
            { HealthDomain.Security, "Sécurité" },
            { HealthDomain.PlatformFirmware, "Plateforme / Firmware" },
            { HealthDomain.Power, "Alimentation" }
        };

        /// <summary>Scenario names for Performance placeholder list when evaluation unavailable (matches UsageScenarioScorer order).</summary>
        private static readonly string[] PerformancePlaceholderScenarioNames =
        {
            "Office / Browsing", "Multitasking", "Gaming (1080p)", "Gaming (1440p)",
            "4K Video Editing", "Streaming + Gaming", "Virtual Machines", "AI (basic inference)"
        };

        private static readonly Regex FirstNumberRegex = new(@"-?\d+(?:[.,]\d+)?", RegexOptions.Compiled);

        /// <summary>
        /// Construit un HealthReport depuis le JSON brut du PowerShell (sans capteurs)
        /// </summary>
        public static HealthReport Build(string jsonContent)
        {
            return Build(jsonContent, null, null, null);
        }

        /// <summary>
        /// Construit un HealthReport depuis le JSON brut du PowerShell AVEC données capteurs hardware.
        /// P0/P1: collectorErrorsLogical, missingData/topPenalties flexibles, UDIS scoring, DataSanitizer.
        /// </summary>
        public static HealthReport Build(
            string jsonContent,
            HardwareSensorsResult? sensors,
            DriverInventoryResult? driverInventory = null,
            WindowsUpdateResult? updatesCsharp = null)
        {
            var report = new HealthReport();
            
            try
            {
                using var doc = JsonDocument.Parse(jsonContent);
                var root = doc.RootElement;
                // P0: Résoudre le blob PS pour JSON combiné (scoreV2 et sections sont dans scan_powershell, pas à la racine)
                JsonElement psRoot = root;
                if (root.ValueKind == JsonValueKind.Object &&
                    (root.TryGetProperty("scan_powershell", out var sp) || root.TryGetProperty("scanPowershell", out sp)) &&
                    sp.ValueKind == JsonValueKind.Object)
                    psRoot = sp;
                
                // 1. Extraire metadata
                report.Metadata = ExtractMetadata(root);
                
                // 2. Extraire scoreV2 (base initiale, écrasé par UDIS à l'étape 7) — depuis psRoot pour avoir breakdown/collectorErrors
                report.ScoreV2 = ExtractScoreV2(psRoot);
                report.GlobalScore = report.ScoreV2.Score;
                report.Grade = report.ScoreV2.Grade;
                report.GlobalSeverity = HealthReport.ScoreToSeverity(report.GlobalScore);
                
                // 3. Diagnostics collecte
                var diagnostics = CollectorDiagnosticsService.Analyze(root, sensors);
                report.Errors = diagnostics.Errors;
                report.MissingData = diagnostics.MissingDataNormalized;
                report.ScoreV2.TopPenalties = diagnostics.TopPenaltiesNormalized;
                report.CollectorErrorsLogical = diagnostics.CollectorErrorsLogical;
                if (report.Metadata.PartialFailure || diagnostics.CollectionStatus == "FAILED")
                    report.CollectorErrorsLogical = Math.Max(report.CollectorErrorsLogical, 1);
                report.CollectionStatus = diagnostics.CollectionStatus;
                
                // Harmonize: force PARTIAL when errors/missingData contradict OK status
                if (report.CollectionStatus == "OK")
                {
                    if (report.CollectorErrorsLogical > 0 || report.Errors.Count > 0)
                    {
                        report.CollectionStatus = "PARTIAL";
                        App.LogMessage($"[HealthReportBuilder] Status harmonized OKâ†’PARTIAL (errors={report.Errors.Count}, collectorErrors={report.CollectorErrorsLogical})");
                    }
                    else if (report.MissingData.Count > 2)
                    {
                        report.CollectionStatus = "PARTIAL";
                        App.LogMessage($"[HealthReportBuilder] Status harmonized OKâ†’PARTIAL (missingData={report.MissingData.Count})");
                    }
                }
                
                App.LogMessage($"COLLECTOR_ERRORS_LOGICAL={report.CollectorErrorsLogical} (from errors[]={report.Errors.Count})");
                
                // 4. Sections par domaine (passer root complet pour diagnostic_signals + event_logs_detailed ; BuildHealthSections résout scan_powershell.sections en interne)
                report.Sections = BuildHealthSections(root, report.ScoreV2, sensors);
                
                // 4b. Neutralisation des valeurs sentinelles / impossibles
                try { NeutralizeSentinelValues(report, sensors); }
                catch (Exception ex4b) { App.LogMessage($"[HealthReportBuilder] NeutralizeSentinelValues failed (non-fatal): {ex4b.Message}"); }

                // 4c. Enrichissement C# (Drivers / Updates)
                try { if (driverInventory != null) InjectDriverInventory(report, driverInventory); }
                catch (Exception ex4c) { App.LogMessage($"[HealthReportBuilder] InjectDriverInventory failed (non-fatal): {ex4c.Message}"); }
                try { if (updatesCsharp != null) InjectUpdatesCsharp(report, updatesCsharp); }
                catch (Exception ex4c2) { App.LogMessage($"[HealthReportBuilder] InjectUpdatesCsharp failed (non-fatal): {ex4c2.Message}"); }
                
                // 5. Capteurs hardware C#
                try { if (sensors != null) InjectHardwareSensors(report, sensors); }
                catch (Exception ex5) { App.LogMessage($"[HealthReportBuilder] InjectHardwareSensors failed (non-fatal): {ex5.Message}"); }

                // 5b. Score performance heuristique (Bureautique / Création / Jeux)
                try
                {
                    InjectPerformanceScore(report, root, sensors);
                }
                catch (Exception ex5b) { App.LogMessage($"[HealthReportBuilder] InjectPerformanceScore failed (non-fatal): {ex5b.Message}"); }

                // 5c. Recompute section scores from real health signals with traceable deductions.
                try
                {
                    RecomputeSectionScores(report, report.ScoreV2);
                }
                catch (Exception ex5c) { App.LogMessage($"[HealthReportBuilder] RecomputeSectionScores failed (non-fatal): {ex5c.Message}"); }
                
                // 6. Modèle de confiance
                try
                {
                    report.ConfidenceModel = BuildConfidenceModel(report, sensors);
                    report.ConfidenceModel.ConfidenceScore = CollectorDiagnosticsService.ApplyConfidenceGating(report.ConfidenceModel.ConfidenceScore, diagnostics);
                }
                catch (Exception ex6) { App.LogMessage($"[HealthReportBuilder] BuildConfidenceModel failed (non-fatal): {ex6.Message}"); }
                
                // 7. UDIS - Unified Diagnostic Intelligence Scoring (source de vérité unique) - psRoot pour findings (sections objet)
                try
                {
                    var udis = UnifiedDiagnosticScoreEngine.Compute(report, psRoot, sensors, diagnostics);
                    report.GlobalScore = udis.UdisScore;
                    report.Grade = udis.Grade;
                    report.GlobalMessage = udis.Message;
                    report.GlobalSeverity = HealthReport.ScoreToSeverity(udis.UdisScore);
                    report.MachineHealthScore = udis.MachineHealthScore;
                    report.DataReliabilityScore = udis.DataReliabilityScore;
                    report.DiagnosticClarityScore = udis.DiagnosticClarityScore;
                    report.UdisFindings = udis.Findings;
                    report.AutoFixAllowed = udis.AutoFixAllowed;
                    report.UdisReport = udis;
                    report.InsufficientDataForDiagnostic = udis.InsufficientDataForDiagnostic;
                    if (report.InsufficientDataForDiagnostic)
                        report.CollectionStatus = "FAILED";
                    report.Divergence.PowerShellScore = report.ScoreV2.Score;
                    report.Divergence.PowerShellGrade = report.ScoreV2.Grade;
                    report.Divergence.GradeEngineScore = udis.UdisScore;  // Rempli par UDIS (legacy field name)
                    report.Divergence.GradeEngineGrade = udis.Grade;
                    report.Divergence.SourceOfTruth = "UDIS";
                }
                catch (Exception ex7) { App.LogMessage($"[HealthReportBuilder] UDIS failed (non-fatal): {ex7.Message}"); }
                
                // 8. Garde-fou confiance : plafonner si collecte insuffisante
                try
                {
                    var confScore = report.ConfidenceModel?.ConfidenceScore ?? 0;
                    if (confScore < 50 && report.GlobalScore > 60)
                    {
                        var originalScore = report.GlobalScore;
                        report.GlobalScore = Math.Min(report.GlobalScore, 60);
                        report.Grade = ScoreToGrade(report.GlobalScore);
                        report.GlobalSeverity = HealthReport.ScoreToSeverity(report.GlobalScore);
                        report.GlobalMessage = $"Score plafonné ({originalScore}→{report.GlobalScore}) : collecte trop faible ({confScore}/100)";
                        App.LogMessage($"[HealthReportBuilder] GARDE-FOU: Score plafonné {originalScore}→{report.GlobalScore} (confiance={confScore})");
                    }
                    else if (confScore < 70 && report.GlobalScore > 75)
                    {
                        var originalScore = report.GlobalScore;
                        report.GlobalScore = Math.Min(report.GlobalScore, 75);
                        report.Grade = ScoreToGrade(report.GlobalScore);
                        report.GlobalSeverity = HealthReport.ScoreToSeverity(report.GlobalScore);
                        report.GlobalMessage = $"Score ajusté ({originalScore}→{report.GlobalScore}) : collecte partielle ({confScore}/100)";
                        App.LogMessage($"[HealthReportBuilder] GARDE-FOU: Score ajusté {originalScore}→{report.GlobalScore} (confiance={confScore})");
                    }
                }
                catch (Exception ex8) { App.LogMessage($"[HealthReportBuilder] Garde-fou failed (non-fatal): {ex8.Message}"); }
                
                // 8b. Verdict collecte
                if (report.InsufficientDataForDiagnostic)
                {
                    report.GlobalSeverity = HealthSeverity.Unknown;
                    var failCloseMessage = report.UdisReport?.FailCloseUserMessage;
                    var failCloseAction = report.UdisReport?.FailCloseAction;
                    report.GlobalMessage = !string.IsNullOrWhiteSpace(failCloseMessage)
                        ? (!string.IsNullOrWhiteSpace(failCloseAction) ? $"{failCloseMessage} {failCloseAction}" : failCloseMessage)
                        : "Diagnostic incomplet: des données critiques manquent. Relancez un scan complet.";
                    report.Grade = "N/A";
                }
                else if (report.CollectionStatus == "FAILED" || report.CollectionStatus == "PARTIAL")
                {
                    report.GlobalMessage = report.CollectionStatus == "FAILED"
                        ? "Collecte échouée : interprétation prudente"
                        : "Collecte partielle : interprétation prudente";
                }
                
                // 9. Recommandations
                try { report.Recommendations = GenerateRecommendations(report); }
                catch (Exception ex9) { App.LogMessage($"[HealthReportBuilder] GenerateRecommendations failed (non-fatal): {ex9.Message}"); }
                
                App.LogMessage($"[HealthReportBuilder] UDIS={report.GlobalScore}, MHS={report.MachineHealthScore}, DRS={report.DataReliabilityScore}, " +
                    $"CollectorErrorsLogical={report.CollectorErrorsLogical}, CollectionStatus={report.CollectionStatus}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[HealthReportBuilder] ERREUR parsing JSON: {ex.Message}");
                report.GlobalScore = 0;
                report.GlobalSeverity = HealthSeverity.Unknown;
                report.GlobalMessage = "Impossible d'analyser les résultats du scan.";
                report.CollectionStatus = "FAILED";
                report.CollectorErrorsLogical = 1;
                report.Errors.Add(new ScanErrorInfo 
                { 
                    Code = "PARSE_ERROR", 
                    Message = ex.Message 
                });
                // FIX: Only replace sections with minimal ones if we don't already have valid sections
                // Previously, this ALWAYS overwrote sections, wiping out successfully extracted data
                // when a later step (UDIS, ConfidenceModel, etc.) threw an exception.
                if (report.Sections == null || report.Sections.Count == 0)
                    report.Sections = BuildMinimalSections(ex.Message);
                else
                    App.LogMessage($"[HealthReportBuilder] Preserved {report.Sections.Count} already-built sections despite error: {ex.Message}");
            }
            
            TextEncodingNormalizer.NormalizeHealthReport(report);
            return report;
        }

        /// <summary>
        /// Construit des sections minimales (une par domaine) quand le parsing JSON échoue,
        /// pour que le tableau dépliable reste visible dans l'UI au lieu de disparaître.
        /// </summary>
        private static List<HealthSection> BuildMinimalSections(string errorMessage)
        {
            var sections = new List<HealthSection>();
            foreach (HealthDomain domain in Enum.GetValues<HealthDomain>())
            {
                sections.Add(new HealthSection
                {
                    Domain = domain,
                    DisplayName = DomainDisplayNames[domain],
                    Icon = DomainIcons[domain],
                    HasData = false,
                    Score = 0,
                    Severity = HealthSeverity.Unknown,
                    StatusMessage = "Données non disponibles",
                    CollectionStatus = "PARSE_ERROR",
                    DetailedExplanation = "L'analyse du rapport a échoué. Les données détaillées ne sont pas disponibles pour ce domaine.",
                    EvidenceData = new Dictionary<string, string>
                    {
                        ["Erreur"] = errorMessage.Length > 200 ? errorMessage.Substring(0, 200) + "…" : errorMessage,
                        ["Recommandation"] = "Relancer un scan ou vérifier le fichier scan_result_combined.json."
                    },
                    SectionRecommendations = new List<string> { "Relancer le diagnostic." }
                });
            }
            return sections;
        }

        /// <summary>
        /// Injecte les données des capteurs hardware dans les EvidenceData des sections correspondantes
        /// </summary>
        private static void InjectHardwareSensors(HealthReport report, HardwareSensorsResult sensors)
        {
            // Trouver les sections concernées
            var cpuSection = report.Sections.FirstOrDefault(s => s.Domain == HealthDomain.CPU);
            var gpuSection = report.Sections.FirstOrDefault(s => s.Domain == HealthDomain.GPU);
            var storageSection = report.Sections.FirstOrDefault(s => s.Domain == HealthDomain.Storage);

            // Injection CPU (évite les doublons avec ComprehensiveEvidenceExtractor)
            if (cpuSection != null && sensors.Cpu.CpuTempC.Available &&
                !cpuSection.EvidenceData.ContainsKey("Temperature") &&
                !cpuSection.EvidenceData.ContainsKey("Température CPU"))
            {
                var sourceInfo = !string.IsNullOrEmpty(sensors.Cpu.CpuTempSource) && sensors.Cpu.CpuTempSource != "N/A"
                    ? $" ({sensors.Cpu.CpuTempSource})"
                    : "";
                cpuSection.EvidenceData["Température CPU"] = $"{sensors.Cpu.CpuTempC.Value:F1}°C{sourceInfo}";
                cpuSection.HasData = true;
                App.LogMessage($"[Sensors→CPU] Température injectée: {sensors.Cpu.CpuTempC.Value:F1}°C from {sensors.Cpu.CpuTempSource}");
            }

            // Injection GPU (évite les doublons)
            if (gpuSection != null)
            {
                if (sensors.Gpu.Name.Available && !gpuSection.EvidenceData.ContainsKey("GPU"))
                    gpuSection.EvidenceData["GPU"] = sensors.Gpu.Name.Value ?? "N/A";
                
                // Température GPU (vérifie les deux clés pour éviter les doublons)
                if (sensors.Gpu.GpuTempC.Available &&
                    !gpuSection.EvidenceData.ContainsKey("Temperature") && 
                    !gpuSection.EvidenceData.ContainsKey("Température GPU"))
                {
                    // Résumé: valeur sans source (source dans tooltip uniquement). Rapport intégral garde la source.
                    gpuSection.EvidenceData["Température GPU"] = $"{sensors.Gpu.GpuTempC.Value:F1}°C";
                    if (!string.IsNullOrEmpty(sensors.Gpu.GpuTempSource) && sensors.Gpu.GpuTempSource != "N/A")
                        gpuSection.EvidenceTooltips["Température GPU"] = "Source: " + sensors.Gpu.GpuTempSource;
                    App.LogMessage($"[Sensors→GPU] Température injectée: {sensors.Gpu.GpuTempC.Value:F1}°C from {sensors.Gpu.GpuTempSource}");
                }
                
                if (sensors.Gpu.GpuLoadPercent.Available && 
                    !gpuSection.EvidenceData.ContainsKey("Load") &&
                    !gpuSection.EvidenceData.ContainsKey("Charge GPU"))
                {
                    var gpuLoad = Math.Clamp(sensors.Gpu.GpuLoadPercent.Value, 0.0, 100.0);
                    gpuSection.EvidenceData["Charge GPU"] = $"{gpuLoad:F0}%";
                    App.LogMessage($"[Sensors→GPU] Charge injectée: {gpuLoad:F0}%");
                }
                
                if (sensors.Gpu.VramTotalMB.Available && sensors.Gpu.VramUsedMB.Available &&
                    !gpuSection.EvidenceData.ContainsKey("VRAM") &&
                    !gpuSection.EvidenceData.ContainsKey("VRAM totale"))
                {
                    var vramUsedPct = Math.Clamp((sensors.Gpu.VramUsedMB.Value / sensors.Gpu.VramTotalMB.Value) * 100.0, 0.0, 100.0);
                    gpuSection.EvidenceData["VRAM"] = $"{sensors.Gpu.VramUsedMB.Value:F0} MB / {sensors.Gpu.VramTotalMB.Value:F0} MB ({vramUsedPct:F0}%)";
                }

                if (!gpuSection.EvidenceData.ContainsKey("VRAM Dédiée"))
                {
                    if (sensors.Gpu.VramDedicatedUsedMB.Available && sensors.Gpu.VramDedicatedTotalMB.Available && sensors.Gpu.VramDedicatedTotalMB.Value > 0)
                    {
                        var pct = sensors.Gpu.VramDedicatedPercent.Available
                            ? sensors.Gpu.VramDedicatedPercent.Value
                            : (sensors.Gpu.VramDedicatedUsedMB.Value / sensors.Gpu.VramDedicatedTotalMB.Value) * 100.0;
                        pct = Math.Clamp(pct, 0.0, 100.0);
                        gpuSection.EvidenceData["VRAM Dédiée"] =
                            $"{sensors.Gpu.VramDedicatedUsedMB.Value / 1024.0:F1} Go / {sensors.Gpu.VramDedicatedTotalMB.Value / 1024.0:F1} Go ({pct:F0}%)";
                    }
                    else
                    {
                        var reason = sensors.Gpu.VramDedicatedReasonIfMissing ?? sensors.Gpu.VramUsedMB.Reason ?? "capteur indisponible";
                        gpuSection.EvidenceData["VRAM Dédiée"] = $"Indisponible ({reason})";
                    }
                }
                
                gpuSection.HasData = true;
            }

            // Injection Stockage (températures disques)
            if (storageSection != null && sensors.Disks.Count > 0)
            {
                var maxDiskTemp = sensors.Disks
                    .Where(d => d.TempC.Available)
                    .Select(d => d.TempC.Value)
                    .DefaultIfEmpty(0)
                    .Max();
                    
                if (maxDiskTemp > 0)
                {
                    storageSection.EvidenceData["TempMax Disques"] = $"{maxDiskTemp:F0}°C";
                    App.LogMessage($"[Sensors→Storage] Temp max disques: {maxDiskTemp:F0}°C");
                }
                
                // Ajouter chaque disque
                for (int i = 0; i < sensors.Disks.Count && i < 5; i++)
                {
                    var disk = sensors.Disks[i];
                    if (disk.Name.Available && disk.TempC.Available)
                    {
                        storageSection.EvidenceData[$"Disque {i+1}"] = $"{disk.Name.Value}: {disk.TempC.Value:F0}°C";
                    }
                }
            }
        }

        /// <summary>
        /// Injecte l'inventaire pilotes C# dans la section Drivers (UI).
        /// Fallback légal basé sur WMI, sans code tiers.
        /// </summary>
        private static void InjectDriverInventory(HealthReport report, DriverInventoryResult driverInventory)
        {
            if (!driverInventory.Available || driverInventory.Drivers.Count == 0) return;

            var driversSection = report.Sections.FirstOrDefault(s => s.Domain == HealthDomain.Drivers);
            if (driversSection == null) return;

            driversSection.HasData = true;
            if (driversSection.CollectionStatus == "MISSING")
                driversSection.CollectionStatus = "C#_FALLBACK";

            // Evidence data summary
            driversSection.EvidenceData["Pilotes detectes"] = driverInventory.TotalCount.ToString();
            if (driverInventory.UnsignedCount > 0)
                driversSection.EvidenceData["Non signes"] = driverInventory.UnsignedCount.ToString();
            if (driverInventory.ProblemCount > 0)
                driversSection.EvidenceData["Periph. en erreur"] = driverInventory.ProblemCount.ToString();

            var oldByAge = driverInventory.Drivers.Count(IsOldByAge);
            var updatesFound = driverInventory.Drivers.Count(d => string.Equals(d.UpdateAvailability, "Found", StringComparison.OrdinalIgnoreCase));
            var nonVerifiable = driverInventory.Drivers.Count(d => string.Equals(d.UpdateAvailability, "NotVerifiable", StringComparison.OrdinalIgnoreCase));

            if (oldByAge > 0)
                driversSection.EvidenceData["Anciens (age > 24 mois)"] = oldByAge.ToString();
            if (updatesFound > 0)
                driversSection.EvidenceData["Mises a jour trouvees (Windows Update)"] = updatesFound.ToString();
            if (nonVerifiable > 0)
                driversSection.EvidenceData["Pilotes non verifiables"] = nonVerifiable.ToString();

            // Only override status if section was previously empty/unknown
            if (driversSection.Score == 0 && driversSection.Severity == HealthSeverity.Unknown)
            {
                driversSection.Score = updatesFound > 0 ? 70 : 85;
                driversSection.Severity = updatesFound > 0 ? HealthSeverity.Warning : HealthSeverity.Healthy;
                driversSection.StatusMessage = updatesFound > 0
                    ? "Mises a jour pilotes trouvees via Windows Update"
                    : (oldByAge > 0 ? "Pilotes anciens detectes (age)" : "Pilotes detectes");
                driversSection.DetailedExplanation = updatesFound > 0
                    ? "Des mises a jour pilotes ont ete trouvees via Windows Update (WUA)."
                    : (oldByAge > 0
                        ? "Le statut Ancien est base sur l'age (>24 mois) et ne garantit pas qu'une mise a jour existe."
                        : "Inventaire pilotes detecte via WMI (Windows).");
            }

            static bool IsOldByAge(DriverInventoryItem d)
            {
                if (d.IsOldByAge.HasValue)
                    return d.IsOldByAge.Value;

                if (string.IsNullOrWhiteSpace(d.DriverDate) || !DateTime.TryParse(d.DriverDate, out var date))
                    return false;

                return (DateTime.Now - date).TotalDays > DriverStatusEvaluator.AgeThresholdMonths * 30.0;
            }
        }

        /// <summary>Score → Grade (A+ … F) — delegates to SchemaRegistry.ScoreToGrade (single source of truth).</summary>
        private static string ScoreToGrade(int score) => Models.SchemaRegistry.ScoreToGrade(score);

        /// <summary>
        /// Neutralize sentinel/impossible values from sensor data to prevent misleading scores.
        /// Marks values as unreliable when detected (0°C temps, >150°C temps, 0% RAM usage, >100% usage).
        /// </summary>
        private static void NeutralizeSentinelValues(HealthReport report, HardwareSensorsResult? sensors)
        {
            if (sensors == null) return;
            try
            {
                // CPU temp: 0°C or >150°C are sentinel values from failed WMI
                if (sensors.Cpu.CpuTempC.Available && (sensors.Cpu.CpuTempC.Value <= 0 || sensors.Cpu.CpuTempC.Value > 150))
                {
                    App.LogMessage($"[SentinelCheck] CPU temp {sensors.Cpu.CpuTempC.Value}°C neutralized (sentinel)");
                    sensors.Cpu.CpuTempC = new MetricValue<double> { Available = false, Value = 0, Reason = "Valeur non fiable (sentinelle)" };
                }
                // GPU temp: same checks
                if (sensors.Gpu.GpuTempC.Available && (sensors.Gpu.GpuTempC.Value <= 0 || sensors.Gpu.GpuTempC.Value > 150))
                {
                    App.LogMessage($"[SentinelCheck] GPU temp {sensors.Gpu.GpuTempC.Value}°C neutralized (sentinel)");
                    sensors.Gpu.GpuTempC = new MetricValue<double> { Available = false, Value = 0, Reason = "Valeur non fiable (sentinelle)" };
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SentinelCheck] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Injecte le statut Windows Update C# dans la section OS (UI).
        /// </summary>
        private static void InjectUpdatesCsharp(HealthReport report, WindowsUpdateResult updatesCsharp)
        {
            if (!updatesCsharp.Available) return;

            var osSection = report.Sections.FirstOrDefault(s => s.Domain == HealthDomain.OS);
            if (osSection == null) return;

            osSection.HasData = true;
            osSection.EvidenceData["Updates en attente"] = updatesCsharp.PendingCount.ToString();

            // Dedup stricte de l'entree reboot: une seule cle canonique dans l'UI.
            string? recoveredRebootValue = null;
            var rebootKeys = osSection.EvidenceData.Keys.Where(IsRebootKey).ToList();
            foreach (var key in rebootKeys)
            {
                if (recoveredRebootValue == null && osSection.EvidenceData.TryGetValue(key, out var currentValue))
                    recoveredRebootValue = NormalizeRebootValue(key, currentValue);
                osSection.EvidenceData.Remove(key);
            }

            if (updatesCsharp.RebootRequired.HasValue)
                osSection.EvidenceData["Redémarrage requis"] = updatesCsharp.RebootRequired.Value ? "Oui" : "Non";
            else if (!string.IsNullOrWhiteSpace(recoveredRebootValue))
                osSection.EvidenceData["Redémarrage requis"] = recoveredRebootValue;

            // Redémarrage requis = Oui -> section OS en Attention
            if (osSection.EvidenceData.TryGetValue("Redémarrage requis", out var rebootVal) && rebootVal == "Oui")
            {
                osSection.Score = Math.Min(osSection.Score, 69);
                osSection.Severity = HealthReport.ScoreToSeverity(osSection.Score);
            }
        }

        private static bool IsRebootKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            var normalized = TextEncodingNormalizer.Normalize(key).Trim().ToLowerInvariant();
            return normalized.StartsWith("redémarrage requis") ||
                   normalized.StartsWith("redemarrage requis") ||
                   normalized.StartsWith("redemarrage requis :") ||
                   normalized.StartsWith("reboot requis");
        }

        private static string? NormalizeRebootValue(string key, string? value)
        {
            var candidate = value?.Trim();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                var idx = key.IndexOf(':');
                if (idx >= 0 && idx < key.Length - 1)
                    candidate = key[(idx + 1)..].Trim();
            }

            if (string.IsNullOrWhiteSpace(candidate))
                return null;

            if (candidate.Contains("oui", StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains("yes", StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains("true", StringComparison.OrdinalIgnoreCase))
                return "Oui";

            if (candidate.Contains("non", StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains("no", StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains("false", StringComparison.OrdinalIgnoreCase))
                return "Non";

            return candidate;
        }

        private static void InjectPerformanceScore(HealthReport report, JsonElement root, HardwareSensorsResult? sensors)
        {
            var section = report.Sections.FirstOrDefault(s => s.Domain == HealthDomain.Performance);
            if (section == null) return;

            PerformanceEvaluationResult eval;
            try
            {
                eval = PerformanceEvaluationEngine.Evaluate(root, null, sensors);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[HealthReportBuilder] InjectPerformanceScore Evaluate failed: {ex.Message}");
                ApplyPerformanceFallback(section, "Évaluation non disponible (données ou erreur).");
                return;
            }

            // Handle Unavailable state (RequireExternal + dataset failed)
            if (eval.IsUnavailable)
            {
                ApplyPerformanceFallback(section, eval.SourceInfo.DisplayLabel);
                section.EvidenceData ??= new Dictionary<string, string>();
                section.EvidenceData["Source"] = eval.SourceInfo.DisplayLabel;
                section.EvidenceData["Mode"] = eval.SourceInfo.Mode.ToString();
                if (!string.IsNullOrEmpty(eval.SourceInfo.FallbackReason))
                    section.EvidenceData["Raison indisponibilité"] = eval.SourceInfo.FallbackReason;
                return;
            }

            section.HasData = true;
            section.IsPerformanceEvaluationAvailable = true;
            section.Score = eval.Score;
            section.Severity = HealthReport.ScoreToSeverity(eval.Score);
            section.StatusMessage = eval.Score >= 60 ? "Bon potentiel" : (eval.Score >= 40 ? "Potentiel modéré" : "Potentiel limité");

            // Dashboard-only fields for main window Capability Dashboard template
            section.PerformanceCategory = eval.Verdict?.Category ?? "";
            section.PrimaryBottleneck = eval.Bottleneck?.PrimaryLimitingFactor ?? "";
            section.RealisticSummary = eval.Verdict?.RealisticExpectationSummary ?? "";

            // Evidence block: specs used (existing profile data only)
            var p = eval.Profile;
            section.PerformanceCpuDisplay = !string.IsNullOrEmpty(p?.CpuModel) ? p.CpuModel : (p?.CpuTier ?? "Unknown");
            section.PerformanceGpuDisplay = !string.IsNullOrEmpty(p?.GpuModel) ? p.GpuModel : (p?.GpuTier ?? "Unknown");
            section.PerformanceVramDisplay = (p != null && p.GpuVramMb > 0) ? $"{p.GpuVramMb / 1024.0:F1} GB" : "Unknown";
            section.PerformanceCpuNameMatched = p?.CpuNameMatched ?? true;
            section.PerformanceGpuNameMatched = p?.GpuNameMatched ?? true;
            section.PerformanceRamDisplay = (p != null && p.RamGb > 0) ? $"{p.RamGb:F0} GB" : "Unknown";
            section.PerformanceStorageDisplay = !string.IsNullOrEmpty(p?.StorageKind) && p.StorageKind != "Unknown" ? p.StorageKind : "Unknown";
            var rows = (eval.ScenarioScores != null && eval.ScenarioScores.Count > 0)
                ? eval.ScenarioScores.Select(s => new PerformanceScenarioRow { Name = s.Name, Score = s.Score, Classification = s.Classification ?? "" }).ToList()
                : new List<PerformanceScenarioRow>();
            if (rows.Count == 0)
            {
                App.LogMessage("[HealthReportBuilder] InjectPerformanceScore: ScenarioScores empty, using fallback row.");
                rows = new List<PerformanceScenarioRow>
                {
                    new PerformanceScenarioRow
                    {
                        Name = "Évaluation globale",
                        Score = eval.Score,
                        Classification = eval.Score >= 70 ? "Excellent" : eval.Score >= 40 ? "Acceptable" : "Limitée"
                    }
                };
            }
            section.PerformanceScenarioRows = rows;
            section.PerformanceMarketRows = BuildPerformanceMarketRows(eval.Profile);

            // Replace EvidenceData with dashboard keys only (so "Données analysées" shows dashboard content, not raw GPU/CPU)
            var si = eval.SourceInfo;
            section.EvidenceData = new Dictionary<string, string>
            {
                ["Source"] = si.DisplayLabel,
                ["Version dataset"] = si.VersionDisplay,
                ["Mode"] = si.Mode.ToString(),
                ["Score performance"] = $"{eval.Score}/100",
                ["Dataset publié"] = eval.DatasetPublishedAt ?? "",
                ["Performance Analysis"] = $"CPU: {p?.CpuTier ?? "?"} | GPU: {p?.GpuTier ?? "?"} | RAM: {p?.RamTier ?? "?"} | Storage: {p?.StorageTier ?? "?"}. System: {eval.Verdict?.Category ?? "?"}.",
                ["Realistic summary"] = eval.Verdict?.RealisticExpectationSummary ?? "",
                ["Primary limiting factor"] = eval.Bottleneck?.PrimaryLimitingFactor ?? "",
                ["Profil utilisé (CPU)"] = !string.IsNullOrEmpty(p?.CpuModel) ? p.CpuModel : (p?.CpuTier ?? "?"),
                ["Profil utilisé (RAM)"] = (p != null && p.RamGb > 0) ? $"{p.RamGb:F0} GB" : "?",
                ["Profil utilisé (VRAM)"] = (p != null && p.GpuVramMb > 0) ? $"{p.GpuVramMb / 1024.0:F1} GB" : "?",
                ["Profil utilisé (Stockage)"] = !string.IsNullOrEmpty(p?.StorageKind) && p.StorageKind != "Unknown" ? p.StorageKind : (p?.StorageTier ?? "?")
            };
            foreach (var s in rows)
                section.EvidenceData[$"Capability Matrix ({s.Name})"] = $"{s.Score}/100 - {s.Classification}";
            if (eval.Bottleneck?.UpgradePriorityRank != null)
            {
                for (int i = 0; i < eval.Bottleneck.UpgradePriorityRank.Count && i < 3; i++)
                {
                    var u = eval.Bottleneck.UpgradePriorityRank[i];
                    section.EvidenceData[$"Upgrade Impact ({u.Rank})"] = $"{u.Component}: {u.Reason}";
                }
            }
        }

        private static List<PerformanceMarketRow> BuildPerformanceMarketRows(HardwareProfile? p)
        {
            var rows = new List<PerformanceMarketRow>();
            try
            {
                var benchmarkProvider = new GitHubBenchmarkDataProvider();
                var benchmarkResult = benchmarkProvider.GetDatasetAsync().GetAwaiter().GetResult();
                var benchmarkDs = benchmarkResult.Dataset;

                // FIXED: Consider benchmark data usable even if from embedded fallback (Error set but Dataset not null)
                // hasUsableBenchmarkData = true if we have a dataset with CPU or GPU entries, regardless of Success flag
                bool hasUsableBenchmarkData = benchmarkDs != null &&
                    ((benchmarkDs.CpuEntries?.Count ?? 0) > 0 || (benchmarkDs.GpuEntries?.Count ?? 0) > 0);

                // Build source display from actual dataset source name (even for embedded fallback)
                string sourceDisplay;
                if (hasUsableBenchmarkData)
                {
                    var sourceName = !string.IsNullOrWhiteSpace(benchmarkDs!.SourceName) ? benchmarkDs.SourceName : "PCDiagnosticPRO";
                    var version = !string.IsNullOrWhiteSpace(benchmarkDs.DatasetVersion) ? $" v{benchmarkDs.DatasetVersion}" : "";
                    var date = !string.IsNullOrWhiteSpace(benchmarkDs.PublishedAt) && benchmarkDs.PublishedAt.Length >= 10
                        ? $" ({benchmarkDs.PublishedAt.Substring(0, 10)})"
                        : "";
                    sourceDisplay = $"{sourceName}{version}{date}";
                }
                else
                {
                    sourceDisplay = "Estimation interne";
                }

                string cpuModel = p?.CpuModel ?? "";
                string gpuModel = p?.GpuModel ?? "";
                double ramGb = p?.RamGb ?? 0;
                string storageKind = p?.StorageKind ?? "Unknown";
                int cpuCores = p?.CpuCores ?? 0;
                int cpuThreads = p?.CpuThreads ?? 0;
                double gpuVramMb = p?.GpuVramMb ?? 0;
                int cpuTierOrder = PerformanceTierTable.TierOrder(p?.CpuTier ?? "");
                int gpuTierOrder = PerformanceTierTable.TierOrder(p?.GpuTier ?? "");
                int ramTierOrder = ramGb >= 64 ? 5 : (ramGb >= 32 ? 4 : (ramGb >= 16 ? 3 : (ramGb >= 8 ? 2 : 1)));
                int storageTierOrder = string.Equals(storageKind, PerformanceTierTable.StorageNvme, StringComparison.OrdinalIgnoreCase) ? 4
                    : string.Equals(storageKind, PerformanceTierTable.StorageSataSsd, StringComparison.OrdinalIgnoreCase) ? 2
                    : string.Equals(storageKind, PerformanceTierTable.StorageHdd, StringComparison.OrdinalIgnoreCase) ? 1 : 1;
                var cpuEntries = benchmarkDs?.CpuEntries ?? new List<CpuBenchmarkEntry>();
                var gpuEntries = benchmarkDs?.GpuEntries ?? new List<GpuBenchmarkEntry>();

                // === CPU ===
                MarketPositionScore cpuScore;
                string cpuDetected = string.IsNullOrWhiteSpace(cpuModel) ? "Non disponible" : cpuModel;
                if (hasUsableBenchmarkData && benchmarkDs != null && !string.IsNullOrWhiteSpace(cpuModel) && cpuEntries.Count > 0)
                {
                    var cpuMatch = BenchmarkMatcher.MatchCpu(cpuModel, cpuEntries);
                    if (cpuMatch.Entry != null)
                    {
                        // Match found - use CreateFromBenchmark with rank and confidence from match
                        cpuScore = MarketPositionScore.CreateFromBenchmark(
                            "CPU", cpuModel, cpuMatch.Entry.RawScore, cpuMatch.Entry.Percentile,
                            cpuMatch.Entry.Rank, benchmarkDs.TotalCpusInMarket, sourceDisplay, cpuMatch.Confidence);
                    }
                    else
                    {
                        // No match - use tier estimation but keep source
                        cpuScore = MarketPositionScore.CreateWithValue("CPU", Math.Max(cpuTierOrder, 1), cpuModel, cpuCores, 24);
                        cpuScore.Source = sourceDisplay;
                        cpuScore.Confidence = MatchConfidence.Low;
                    }
                }
                else
                {
                    cpuScore = MarketPositionScore.CreateWithValue("CPU", Math.Max(cpuTierOrder, 1), cpuDetected, cpuCores, 24);
                    cpuScore.Source = sourceDisplay;
                    cpuScore.Confidence = MatchConfidence.Low;
                }
                cpuScore.DetectedModel = cpuDetected;

                // === GPU ===
                MarketPositionScore gpuScore;
                string gpuDetected = string.IsNullOrWhiteSpace(gpuModel) ? "Non disponible" : gpuModel;
                if (hasUsableBenchmarkData && benchmarkDs != null && !string.IsNullOrWhiteSpace(gpuModel) && gpuEntries.Count > 0)
                {
                    var gpuMatch = BenchmarkMatcher.MatchGpu(gpuModel, gpuEntries);
                    if (gpuMatch.Entry != null)
                    {
                        // Match found - use CreateFromBenchmark with rank and confidence from match
                        gpuScore = MarketPositionScore.CreateFromBenchmark(
                            "GPU", gpuModel, gpuMatch.Entry.RawScore, gpuMatch.Entry.Percentile,
                            gpuMatch.Entry.Rank, benchmarkDs.TotalGpusInMarket, sourceDisplay, gpuMatch.Confidence);
                    }
                    else
                    {
                        // No match - use tier estimation but keep source
                        gpuScore = MarketPositionScore.CreateWithValue("GPU", Math.Max(gpuTierOrder, 1), gpuModel, gpuVramMb, 24576);
                        gpuScore.Source = sourceDisplay;
                        gpuScore.Confidence = MatchConfidence.Low;
                    }
                }
                else
                {
                    gpuScore = MarketPositionScore.CreateWithValue("GPU", Math.Max(gpuTierOrder, 1), gpuDetected, gpuVramMb, 24576);
                    gpuScore.Source = sourceDisplay;
                    gpuScore.Confidence = MatchConfidence.Low;
                }
                gpuScore.DetectedModel = gpuDetected;

                // === RAM (always tier-based, but with proper source and medium confidence if data available) ===
                string ramDetected = ramGb > 0 ? $"{ramGb:F0} Go" : "Non disponible";
                var ramScore = MarketPositionScore.CreateWithValue("RAM", Math.Max(ramTierOrder, 1), ramDetected, ramGb, 128);
                ramScore.DetectedModel = ramDetected;
                ramScore.Source = sourceDisplay;
                ramScore.Confidence = hasUsableBenchmarkData && ramGb > 0 ? MatchConfidence.Medium : MatchConfidence.Low;
                // Estimate rank for RAM based on percentile and total market
                if (ramGb > 0)
                {
                    int ramTotalMarket = 10000;
                    ramScore.Rank = MarketPositionScore.CalculateRankFromPercentile(ramScore.Percentile, ramTotalMarket);
                    ramScore.TotalInMarket = ramTotalMarket;
                    ramScore.RankDisplay = MarketPositionScore.GetRankDisplay(ramScore.Rank, ramTotalMarket);
                }
                // So that "Score bench" shows a number when percentile/rank exist (normalized 0-100 for RAM)
                if (ramScore.Percentile > 0)
                    ramScore.BenchmarkScore = ramScore.Percentile;

                // === Storage (tier-based with estimated rank) ===
                string storageDetected = string.IsNullOrWhiteSpace(storageKind) || storageKind == "Unknown" ? "Non disponible" : storageKind;
                var storageScore = MarketPositionScore.CreateWithValue("Stockage", Math.Max(storageTierOrder, 1), storageDetected, storageTierOrder, 4);
                storageScore.DetectedModel = storageDetected;
                storageScore.Source = sourceDisplay;
                storageScore.Confidence = hasUsableBenchmarkData && storageKind != "Unknown" ? MatchConfidence.Medium : MatchConfidence.Low;
                // Estimate rank for Storage
                if (!string.IsNullOrWhiteSpace(storageKind) && storageKind != "Unknown")
                {
                    int storageTotalMarket = 10000;
                    storageScore.Rank = MarketPositionScore.CalculateRankFromPercentile(storageScore.Percentile, storageTotalMarket);
                    storageScore.TotalInMarket = storageTotalMarket;
                    storageScore.RankDisplay = MarketPositionScore.GetRankDisplay(storageScore.Rank, storageTotalMarket);
                }
                // So that "Score bench" shows a number when percentile/rank exist (normalized 0-100 for storage)
                if (storageScore.Percentile > 0)
                    storageScore.BenchmarkScore = storageScore.Percentile;

                // === Global (weighted average) ===
                double weightedPercentile = (cpuScore.Percentile * 0.25) + (gpuScore.Percentile * 0.35) + (ramScore.Percentile * 0.25) + (storageScore.Percentile * 0.15);
                var globalScore = MarketPositionScore.CreateFromBenchmark(
                    "Global", "Score pondéré", weightedPercentile, weightedPercentile,
                    MarketPositionScore.CalculateRankFromPercentile(weightedPercentile, 10000), 10000,
                    sourceDisplay, MatchConfidence.Medium);

                foreach (var s in new[] { cpuScore, gpuScore, ramScore, storageScore, globalScore })
                {
                    rows.Add(new PerformanceMarketRow
                    {
                        Component = s.Component,
                        DetectedModel = string.IsNullOrWhiteSpace(s.DetectedModel) ? "Non disponible" : s.DetectedModel,
                        BenchmarkScoreDisplay = s.BenchmarkScore > 0 ? s.BenchmarkScore.ToString("N0") : "N/A",
                        PercentileDisplay = string.IsNullOrWhiteSpace(s.PercentileDisplay) ? "N/A" : s.PercentileDisplay,
                        RankDisplay = string.IsNullOrWhiteSpace(s.RankDisplay) ? "N/A" : s.RankDisplay,
                        Source = string.IsNullOrWhiteSpace(s.Source) ? "Estimation interne" : s.Source,
                        ConfidenceDisplay = string.IsNullOrWhiteSpace(s.ConfidenceDisplay) ? "Faible" : s.ConfidenceDisplay
                    });
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[HealthReportBuilder.BuildPerformanceMarketRows] Error: {ex.Message}");
                rows.Clear();
            }

            if (rows.Count == 0)
            {
                rows.Add(new PerformanceMarketRow { Component = "CPU", DetectedModel = "Non disponible", BenchmarkScoreDisplay = "N/A", PercentileDisplay = "N/A", RankDisplay = "N/A", Source = "Estimation interne", ConfidenceDisplay = "Faible" });
                rows.Add(new PerformanceMarketRow { Component = "GPU", DetectedModel = "Non disponible", BenchmarkScoreDisplay = "N/A", PercentileDisplay = "N/A", RankDisplay = "N/A", Source = "Estimation interne", ConfidenceDisplay = "Faible" });
                rows.Add(new PerformanceMarketRow { Component = "RAM", DetectedModel = "Non disponible", BenchmarkScoreDisplay = "N/A", PercentileDisplay = "N/A", RankDisplay = "N/A", Source = "Estimation interne", ConfidenceDisplay = "Faible" });
                rows.Add(new PerformanceMarketRow { Component = "Stockage", DetectedModel = "Non disponible", BenchmarkScoreDisplay = "N/A", PercentileDisplay = "N/A", RankDisplay = "N/A", Source = "Estimation interne", ConfidenceDisplay = "Faible" });
                rows.Add(new PerformanceMarketRow { Component = "Global", DetectedModel = "Non disponible", BenchmarkScoreDisplay = "N/A", PercentileDisplay = "N/A", RankDisplay = "N/A", Source = "Estimation interne", ConfidenceDisplay = "Faible" });
            }
            return rows;
        }

        /// <summary>
        /// Fills Performance section when evaluation is unavailable: N/A score, Indisponible, placeholder scenario rows (N/A), no fake scores.
        /// </summary>
        private static void ApplyPerformanceFallback(HealthSection section, string summary)
        {
            section.HasData = true;
            section.IsPerformanceEvaluationAvailable = false;
            section.Score = -1;
            section.Severity = HealthSeverity.Unknown;
            section.StatusMessage = "Indisponible";
            section.PerformanceCategory = "";
            section.PrimaryBottleneck = "Non déterminé";
            section.RealisticSummary = summary;
            section.PerformanceCpuDisplay = "Unknown";
            section.PerformanceGpuDisplay = "Unknown";
            section.PerformanceVramDisplay = "Unknown";
            section.PerformanceRamDisplay = "Unknown";
            section.PerformanceStorageDisplay = "Unknown";
            section.PerformanceScenarioRows = PerformancePlaceholderScenarioNames
                .Select(name => new PerformanceScenarioRow { Name = name, Score = -1, Classification = "N/A" })
                .ToList();
            section.EvidenceData = new Dictionary<string, string>
            {
                ["Score performance"] = "N/A",
                ["Realistic summary"] = summary,
                ["Primary limiting factor"] = "Non déterminé"
            };
        }

        /// <summary>
        /// Construit le modèle de confiance (coverage + cohérence).
        /// ConfidenceScore pénalise l'ABSENCE de données, pas les anomalies (c'est HealthScore).
        /// </summary>
        private static ConfidenceModel BuildConfidenceModel(HealthReport report, HardwareSensorsResult? sensors)
        {
            var model = new ConfidenceModel();
            
            // 1. Coverage des sections PS
            int expectedSections = 12; // 12 domaines (extended with Applications, Performance, Security, Power)
            int availableSections = report.Sections.Count(s => s.HasData);
            model.SectionsCoverage = (double)availableSections / expectedSections;
            
            // 2. Coverage des capteurs hardware
            if (sensors != null)
            {
                var (available, total) = sensors.GetAvailabilitySummary();
                model.SensorsCoverage = total > 0 ? (double)available / total : 0;
                model.SensorsAvailable = available;
                model.SensorsTotal = total;
            }
            else
            {
                model.SensorsCoverage = 0;
                model.SensorsAvailable = 0;
                model.SensorsTotal = 6; // GPU name, GPU temp, GPU load, VRAM total, VRAM used, CPU temp
            }
            
            // 3. Score de confiance global - PÉNALITÉS SPÉCIFIQUES
            model.ConfidenceScore = 100;
            
            // === PÉNALITÉS CAPTEURS C# CRITIQUES ===
            if (sensors == null)
            {
                model.ConfidenceScore -= 20;
                model.Warnings.Add("Capteurs hardware C# non collectés (objet null)");
            }
            else
            {
                // CPU température manquante = critique pour évaluer la santé thermique
                if (!sensors.Cpu.CpuTempC.Available)
                {
                    model.ConfidenceScore -= 8;
                    model.Warnings.Add($"Température CPU indisponible ({sensors.Cpu.CpuTempC.Reason ?? "capteur absent"})");
                }
                
                // GPU température manquante
                if (!sensors.Gpu.GpuTempC.Available)
                {
                    model.ConfidenceScore -= 5;
                    model.Warnings.Add($"Température GPU indisponible ({sensors.Gpu.GpuTempC.Reason ?? "capteur absent"})");
                }
                
                // VRAM = important pour évaluer les problèmes graphiques
                if (!sensors.Gpu.VramTotalMB.Available || !sensors.Gpu.VramUsedMB.Available)
                {
                    model.ConfidenceScore -= 3;
                    model.Warnings.Add("VRAM indisponible (limitation driver ou permissions)");
                }
                
                // GPU Load manquant
                if (!sensors.Gpu.GpuLoadPercent.Available)
                {
                    model.ConfidenceScore -= 2;
                    model.Warnings.Add("Charge GPU indisponible");
                }
                
                // Températures disques = vérifie la couverture
                var disksWithTemp = sensors.Disks.Count(d => d.TempC.Available);
                var totalDisks = sensors.Disks.Count;
                if (totalDisks > 0 && disksWithTemp == 0)
                {
                    model.ConfidenceScore -= 5;
                    model.Warnings.Add($"Aucune température disque disponible (0/{totalDisks} disques)");
                }
            }
            
            // === PÉNALITÉS POWERSHELL ===
            if (report.Metadata.PartialFailure)
            {
                model.ConfidenceScore -= 10;
                model.Warnings.Add("Scan PowerShell partiel - certaines sections manquantes");
            }
            
            if (model.SectionsCoverage < 0.7)
            {
                model.ConfidenceScore -= 8;
                model.Warnings.Add($"Couverture sections PS faible ({model.SectionsCoverage:P0})");
            }
            
            // Erreurs de collecteurs : priorité à collectorErrorsLogical (errors[]) pour cohérence JSON↔TXT
            // FIX #9: Pénalités réduites (était: *3, max 15)
            var collectorErrors = report.CollectorErrorsLogical > 0 ? report.CollectorErrorsLogical : report.ScoreV2.Breakdown.CollectorErrors;
            if (collectorErrors > 0)
            {
                var penalty = Math.Min(collectorErrors * 2, 10); // FIX #9: Réduit de *3/15 à *2/10
                model.ConfidenceScore -= penalty;
                model.Warnings.Add($"Erreurs collecteur: {collectorErrors} (pénalité -{penalty})");
            }
            
            // Timeouts = données potentiellement incomplètes
            if (report.ScoreV2.Breakdown.Timeouts > 0)
            {
                var penalty = Math.Min(report.ScoreV2.Breakdown.Timeouts * 5, 15);
                model.ConfidenceScore -= penalty;
                model.Warnings.Add($"Timeouts: {report.ScoreV2.Breakdown.Timeouts} (pénalité -{penalty})");
            }
            
            // MissingData du rapport PS
            if (report.MissingData.Count > 0)
            {
                var penalty = Math.Min(report.MissingData.Count * 2, 10);
                model.ConfidenceScore -= penalty;
                model.Warnings.Add($"Données PS manquantes: {report.MissingData.Count} éléments");
            }
            
            // Performance: Unmatched CPU/GPU name â†’ reduce confidence (tier from heuristic)
            var perfSection = report.Sections?.FirstOrDefault(s => s.Domain == HealthDomain.Performance);
            if (perfSection != null && (perfSection.IsPerformanceEvaluationAvailable))
            {
                if (!perfSection.PerformanceCpuNameMatched) { model.ConfidenceScore -= 5; model.Warnings.Add("CPU non reconnu dans la table de performance (tier déduit)"); }
                if (!perfSection.PerformanceGpuNameMatched) { model.ConfidenceScore -= 5; model.Warnings.Add("GPU non reconnu dans la table de performance (tier déduit)"); }
            }

            // Erreurs explicites dans le rapport
            var criticalErrors = report.Errors.Count(e => 
                e.Code.Contains("WMI", StringComparison.OrdinalIgnoreCase) ||
                e.Code.Contains("SMART", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase));
            if (criticalErrors > 0)
            {
                model.ConfidenceScore -= criticalErrors * 3;
                model.Warnings.Add($"Erreurs critiques détectées: {criticalErrors} (WMI/SMART/invalid)");
            }
            
            // Finaliser
            model.ConfidenceScore = Math.Max(0, Math.Min(100, model.ConfidenceScore));
            model.ConfidenceLevel = model.ConfidenceScore >= 80 ? "Élevée" :
                                    model.ConfidenceScore >= 60 ? "Moyenne" : "Faible";
            
            App.LogMessage($"[ConfidenceModel] Score={model.ConfidenceScore}, Level={model.ConfidenceLevel}, " +
                $"Sensors={model.SensorsAvailable}/{model.SensorsTotal}, Warnings={model.Warnings.Count}");
            
            return model;
        }

        private static ScanMetadata ExtractMetadata(JsonElement root)
        {
            var metadata = new ScanMetadata();
            
            if (root.ValueKind != JsonValueKind.Object)
                return metadata;
            
            if (root.TryGetProperty("metadata", out var metaElement) && metaElement.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    if (metaElement.TryGetProperty("version", out var v)) metadata.Version = v.GetString() ?? "unknown";
                    if (metaElement.TryGetProperty("runId", out var r)) metadata.RunId = r.GetString() ?? "";
                    if (metaElement.TryGetProperty("timestamp", out var t) && DateTime.TryParse(t.GetString(), out var dt)) metadata.Timestamp = dt;
                    if (metaElement.TryGetProperty("isAdmin", out var a)) metadata.IsAdmin = a.GetBoolean();
                    if (metaElement.TryGetProperty("redactLevel", out var rl)) metadata.RedactLevel = rl.GetString() ?? "standard";
                    if (metaElement.TryGetProperty("quickScan", out var q) && (q.ValueKind == JsonValueKind.True || q.ValueKind == JsonValueKind.False)) metadata.QuickScan = q.GetBoolean();
                    if (metaElement.TryGetProperty("monitorSeconds", out var m)) metadata.MonitorSeconds = SafeGetInt(m, 0);
                    if (metaElement.TryGetProperty("durationSeconds", out var d)) metadata.DurationSeconds = SafeGetDouble(d, 0);
                    if (metaElement.TryGetProperty("partialFailure", out var p) && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False)) metadata.PartialFailure = p.GetBoolean();
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[HealthReportBuilder] Warning: Erreur parsing metadata: {ex.Message}");
                }
            }
            
            return metadata;
        }

        private static ScoreV2Data ExtractScoreV2(JsonElement root)
        {
            var scoreV2 = new ScoreV2Data();
            
            if (root.ValueKind != JsonValueKind.Object)
                return scoreV2;
            
            if (root.TryGetProperty("scoreV2", out var scoreElement) && scoreElement.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    if (scoreElement.TryGetProperty("score", out var s)) scoreV2.Score = SafeGetInt(s, 100);
                    if (scoreElement.TryGetProperty("baseScore", out var bs)) scoreV2.BaseScore = SafeGetInt(bs, 100);
                    if (scoreElement.TryGetProperty("totalPenalty", out var tp)) scoreV2.TotalPenalty = SafeGetInt(tp, 0);
                    if (scoreElement.TryGetProperty("grade", out var g)) scoreV2.Grade = g.GetString() ?? "N/A";
                    
                    // Breakdown - FIX: check ValueKind before TryGetProperty
                    if (scoreElement.TryGetProperty("breakdown", out var bdElement) && bdElement.ValueKind == JsonValueKind.Object)
                    {
                        var bd = new ScoreBreakdown();
                        if (bdElement.TryGetProperty("critical", out var c)) bd.Critical = SafeGetInt(c, 0);
                        if (bdElement.TryGetProperty("collectorErrors", out var ce)) bd.CollectorErrors = SafeGetInt(ce, 0);
                        if (bdElement.TryGetProperty("warnings", out var w)) bd.Warnings = SafeGetInt(w, 0);
                        if (bdElement.TryGetProperty("timeouts", out var to)) bd.Timeouts = SafeGetInt(to, 0);
                        if (bdElement.TryGetProperty("infoIssues", out var ii)) bd.InfoIssues = SafeGetInt(ii, 0);
                        if (bdElement.TryGetProperty("excludedLimitations", out var el)) bd.ExcludedLimitations = SafeGetInt(el, 0);
                        scoreV2.Breakdown = bd;
                    }
                    
                    // Top penalties
                    if (scoreElement.TryGetProperty("topPenalties", out var tpArray) && tpArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var penalty in tpArray.EnumerateArray())
                        {
                            if (penalty.ValueKind != JsonValueKind.Object)
                                continue;
                            
                            var p = new PenaltyInfo();
                            if (penalty.TryGetProperty("type", out var pt)) p.Type = pt.GetString() ?? "";
                            if (penalty.TryGetProperty("source", out var ps)) p.Source = ps.GetString() ?? "";
                            if (penalty.TryGetProperty("penalty", out var pp)) p.Penalty = SafeGetInt(pp, 0);
                            if (penalty.TryGetProperty("msg", out var pm)) p.Message = pm.GetString() ?? "";
                            scoreV2.TopPenalties.Add(p);
                        }
                    }
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[HealthReportBuilder] Warning: Erreur parsing scoreV2: {ex.Message}");
                    // Fallback sur calcul legacy si scoreV2 échoue
                    scoreV2 = CalculateLegacyScore(root);
                }
            }
            else
            {
                // Pas de scoreV2, utiliser calcul legacy
                scoreV2 = CalculateLegacyScore(root);
            }
            
            return scoreV2;
        }

        private static ScoreV2Data CalculateLegacyScore(JsonElement root)
        {
            // FIX RISK #5: Fallback must not invent scores when data is incomplete
            var score = new ScoreV2Data { Score = -1, BaseScore = 100, Grade = "N/A" };
            bool hasSummaryData = false;
            
            if (root.ValueKind != JsonValueKind.Object)
            {
                // No data - mark as unavailable
                score.UnavailableReason = "Données JSON absentes ou invalides";
                App.LogMessage("[HealthReportBuilder] Legacy score: no valid JSON data");
                return score;
            }
            
            if (root.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object)
            {
                if (summary.TryGetProperty("score", out var s)) 
                {
                    score.Score = SafeGetInt(s, -1);
                    hasSummaryData = true;
                }
                if (summary.TryGetProperty("grade", out var g)) score.Grade = g.GetString() ?? "N/A";
                if (summary.TryGetProperty("criticalCount", out var cc)) score.Breakdown.Critical = SafeGetInt(cc, 0);
                if (summary.TryGetProperty("warningCount", out var wc)) score.Breakdown.Warnings = SafeGetInt(wc, 0);
            }
            
            // FIX RISK #5: If summary is absent and we had to calculate legacy, mark as unavailable
            if (!hasSummaryData || score.Score < 0)
            {
                score.Score = -1; // Explicitly mark as unavailable
                score.Grade = "N/A";
                score.UnavailableReason = "Score indisponible (données incomplètes)";
                App.LogMessage("[HealthReportBuilder] Legacy score: summary missing - score unavailable");
            }
            else
            {
                score.TotalPenalty = 100 - score.Score;
            }
            return score;
        }

        private static List<ScanErrorInfo> ExtractErrors(JsonElement root)
        {
            var errors = new List<ScanErrorInfo>();
            
            if (root.ValueKind != JsonValueKind.Object)
                return errors;
            
            if (root.TryGetProperty("errors", out var errArray) && errArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var err in errArray.EnumerateArray())
                {
                    if (err.ValueKind != JsonValueKind.Object)
                        continue;
                    
                    var error = new ScanErrorInfo();
                    if (err.TryGetProperty("code", out var c)) error.Code = c.GetString() ?? "";
                    if (err.TryGetProperty("message", out var m)) error.Message = m.GetString() ?? "";
                    if (err.TryGetProperty("section", out var s)) error.Section = s.GetString() ?? "";
                    if (err.TryGetProperty("exceptionType", out var e)) error.ExceptionType = e.GetString() ?? "";
                    errors.Add(error);
                }
            }
            
            return errors;
        }

        private static List<string> ExtractMissingData(JsonElement root)
        {
            var missing = new List<string>();
            
            if (root.ValueKind != JsonValueKind.Object)
                return missing;
            
            if (root.TryGetProperty("missingData", out var mdArray) && mdArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in mdArray.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        missing.Add(item.GetString() ?? "");
                }
            }
            
            return missing;
        }

        private static List<HealthSection> BuildHealthSections(JsonElement root, ScoreV2Data scoreV2, HardwareSensorsResult? sensors = null)
        {
            var sections = new List<HealthSection>();
            var domainData = new Dictionary<HealthDomain, List<(string sectionName, JsonElement data, string status)>>();
            
            // Initialiser tous les domaines
            foreach (HealthDomain domain in Enum.GetValues<HealthDomain>())
            {
                domainData[domain] = new List<(string, JsonElement, string)>();
            }
            
            if (root.ValueKind != JsonValueKind.Object)
            {
                App.LogMessage($"[HealthReportBuilder] Warning: root is not Object (is {root.ValueKind}), skipping section parsing");
                // Return minimal sections for all domains
                foreach (HealthDomain domain in Enum.GetValues<HealthDomain>())
                {
                    sections.Add(new HealthSection
                    {
                        Domain = domain,
                        DisplayName = DomainDisplayNames[domain],
                        Icon = DomainIcons[domain],
                        HasData = false,
                        Score = 0,
                        Severity = HealthSeverity.Unknown,
                        StatusMessage = "Données non disponibles",
                        CollectionStatus = "INVALID_ROOT"
                    });
                }
                return sections;
            }
            
            // Parser les sections JSON (scan_powershell.sections ou sections directement)
            JsonElement sectionsElement = default;
            bool hasSections = false;
            
            // Try scan_powershell.sections first - FIX: check ValueKind before TryGetProperty
            if (root.TryGetProperty("scan_powershell", out var psRoot) && 
                psRoot.ValueKind == JsonValueKind.Object &&
                psRoot.TryGetProperty("sections", out var psSections) && 
                psSections.ValueKind == JsonValueKind.Object)
            {
                sectionsElement = psSections;
                hasSections = true;
            }
            // Direct sections access
            else if (root.TryGetProperty("sections", out var directSections) && directSections.ValueKind == JsonValueKind.Object)
            {
                sectionsElement = directSections;
                hasSections = true;
            }
            
            if (hasSections)
            {
                foreach (var section in sectionsElement.EnumerateObject())
                {
                    var sectionName = section.Name;
                    var sectionData = section.Value;
                    
                    // Trouver le domaine correspondant
                    if (SectionToDomain.TryGetValue(sectionName, out var domain))
                    {
                        var status = "OK";
                        JsonElement data;
                        
                        if (sectionData.ValueKind == JsonValueKind.Object)
                        {
                            if (sectionData.TryGetProperty("status", out var statusProp))
                                status = statusProp.GetString() ?? "OK";
                            
                            data = sectionData.TryGetProperty("data", out var dataProp) ? dataProp : sectionData;
                        }
                        else
                        {
                            // sectionData is Array or other - use as-is
                            data = sectionData;
                        }
                        
                        domainData[domain].Add((sectionName, data, status));
                    }
                }
            }
            
            // Construire les HealthSection pour chaque domaine
            foreach (HealthDomain domain in Enum.GetValues<HealthDomain>())
            {
                var section = new HealthSection
                {
                    Domain = domain,
                    DisplayName = DomainDisplayNames[domain],
                    Icon = DomainIcons[domain],
                    HasData = domainData[domain].Count > 0
                };
                // Pour le domaine Drivers, utiliser les données WMI en fallback si PS est vide
                if (domain == HealthDomain.Drivers && !section.HasData)
                {
                    var wmiDriverData = GetEssentialDriversFromWmiForHealth();
                    if (wmiDriverData.Count > 0)
                    {
                        section.HasData = true;
                        section.Score = 85; // Score par défaut si WMI fonctionne
                        section.Severity = HealthSeverity.Healthy;
                        section.CollectionStatus = "WMI_FALLBACK";
                        section.StatusMessage = "Pilotes détectés (WMI)";
                        section.EvidenceData = new Dictionary<string, string>
                        {
                            ["Source"] = "WMI Win32_PnPSignedDriver",
                            ["Pilotes essentiels"] = wmiDriverData.Count.ToString()
                            // Ligne "Classes" supprimée (ne renseigne pas assez)
                        };
                        section.DetailedExplanation = $"Les pilotes ont été détectés via WMI. {wmiDriverData.Count} pilotes essentiels trouvés.";
                        section.SectionRecommendations = new List<string> { "Mettez à jour les pilotes obsolètes" };
                        App.LogMessage($"[HealthReportBuilder] Drivers domain: WMI fallback utilisé, {wmiDriverData.Count} pilotes");
                    }
                }
                
                // Toujours tenter l'extraction comprehensive pour tous les domaines:
                // évite les cartes "Données non disponibles" quand la map PS est partielle.
                if (section.HasData ||
                    domain == HealthDomain.Performance ||
                    domain == HealthDomain.Security ||
                    domain == HealthDomain.CPU ||
                    domain == HealthDomain.GPU ||
                    domain == HealthDomain.OS ||
                    domain == HealthDomain.RAM ||
                    domain == HealthDomain.Storage ||
                    domain == HealthDomain.Network ||
                    domain == HealthDomain.SystemStability ||
                    domain == HealthDomain.Applications ||
                    domain == HealthDomain.PlatformFirmware ||
                    domain == HealthDomain.Power)
                {
                    section.Score = CalculateSectionScore(domain, scoreV2, domainData[domain]);
                    section.Severity = HealthReport.ScoreToSeverity(section.Score);
                    section.CollectionStatus = GetWorstStatus(domainData[domain]);
                    
                    // === NOUVEAU: Utiliser ComprehensiveEvidenceExtractor pour données complètes ===
                    // Extrait données de: PS sections, sensors C#, diagnostic_signals, network_diagnostics, etc.
                    try
                    {
                        var comprehensiveEvidence = ComprehensiveEvidenceExtractor.Extract(domain, root, sensors);
                        
                        if (comprehensiveEvidence.Count > 0)
                        {
                            section.EvidenceData = comprehensiveEvidence;
                            section.HasData = true;
                        }
                        else if (section.EvidenceData.Count == 0)
                        {
                            // Fallback sur l'ancienne méthode si le nouvel extracteur n'a rien trouvé
                            section.EvidenceData = ExtractEvidenceData(domain, domainData[domain]);
                        }

                        if (domain == HealthDomain.SystemStability)
                            section.HasKernelPowerId1 = ComprehensiveEvidenceExtractor.HasKernelPowerId1Present(root);
                    }
                    catch (Exception exEvidence)
                    {
                        App.LogMessage($"[HealthReportBuilder] Warning: Extraction evidence {domain} failed: {exEvidence.Message}");
                        // Fallback: try the old method
                        try
                        {
                            section.EvidenceData = ExtractEvidenceData(domain, domainData[domain]);
                        }
                        catch
                        {
                            section.EvidenceData = new Dictionary<string, string>
                            {
                                ["Note"] = "Extraction des données impossible pour cette section"
                            };
                        }
                    }
                    
                    // Générer le message de statut
                    section.StatusMessage = GenerateSectionMessage(section);
                    
                    // Extraire les findings
                    section.Findings = ExtractFindings(domain, scoreV2);
                    
                    // Générer l'explication détaillée
                    section.DetailedExplanation = GenerateDetailedExplanation(section);
                    
                    // Générer les recommandations
                    section.SectionRecommendations = GenerateSectionRecommendations(section);
                }
                else if (!section.HasData)
                {
                    section.Score = 0;
                    section.Severity = HealthSeverity.Unknown;
                    section.StatusMessage = "Données non disponibles";
                    section.CollectionStatus = "MISSING";
                }
                
                sections.Add(section);
            }

            // Performance first, then OS (Système d'exploitation), then rest
            var perf = sections.FirstOrDefault(s => s.Domain == HealthDomain.Performance);
            if (perf != null)
            {
                sections.Remove(perf);
                sections.Insert(0, perf);
            }
            
            return sections;
        }

        private static int CalculateSectionScore(HealthDomain domain, ScoreV2Data scoreV2, List<(string sectionName, JsonElement data, string status)> sectionData)
        {
            int score = 100;
            
            // Pénalités basées sur le statut de collecte
            foreach (var (_, _, status) in sectionData)
            {
                if (status == "FAILED") score -= 20;
                else if (status == "PARTIAL") score -= 5;
            }
            
            // Pénalités depuis topPenalties
            foreach (var penalty in scoreV2.TopPenalties)
            {
                // Vérifier si la pénalité concerne ce domaine
                if (SectionToDomain.TryGetValue(penalty.Source, out var penaltyDomain) && penaltyDomain == domain)
                {
                    score -= penalty.Penalty;
                }
            }
            
            return Math.Max(0, Math.Min(100, score));
        }

        private static void RecomputeSectionScores(HealthReport report, ScoreV2Data scoreV2)
        {
            foreach (var section in report.Sections)
            {
                section.ScoreDeductions ??= new List<ScoreDeduction>();
                section.ScoreDeductions.Clear();
                section.ScoreUnavailableReason = string.Empty;
                section.EvidenceData ??= new Dictionary<string, string>();
                section.EvidenceTooltips ??= new Dictionary<string, string>();
                RemoveLegacyScoreExplanationEntries(section);

                if (section.Domain == HealthDomain.Performance)
                {
                    if (!section.IsPerformanceEvaluationAvailable || section.Score < 0)
                    {
                        MarkSectionScoreUnavailable(section, "Évaluation de performance indisponible.");
                    }
                    else
                    {
                        WriteScoreBreakdown(section);
                    }
                    continue;
                }

                if (!section.HasData || section.EvidenceData.Count == 0 || section.EvidenceData.Values.All(IsUnavailableEvidence))
                {
                    MarkSectionScoreUnavailable(section, BuildUnavailableReason(section));
                    continue;
                }

                var deductions = new List<ScoreDeduction>();

                ApplyCollectionStatusDeductions(section, deductions);
                ApplyTopPenaltyFallbackDeductions(section.Domain, scoreV2, deductions);

                switch (section.Domain)
                {
                    case HealthDomain.Storage:
                        ApplyStorageDeductions(section, deductions);
                        break;
                    case HealthDomain.GPU:
                        ApplyGpuDeductions(section, deductions);
                        break;
                    case HealthDomain.SystemStability:
                        ApplyStabilityDeductions(section, deductions);
                        break;
                    case HealthDomain.OS:
                        ApplyOsDeductions(section, deductions);
                        break;
                }

                var totalPenalty = deductions.Sum(d => Math.Max(0, d.Delta));
                section.Score = Math.Max(0, Math.Min(100, 100 - totalPenalty));
                section.Severity = HealthReport.ScoreToSeverity(section.Score);
                section.ScoreDeductions = deductions;
                WriteScoreBreakdown(section);
            }
        }

        private static void MarkSectionScoreUnavailable(HealthSection section, string reason)
        {
            section.Score = -1;
            section.Severity = HealthSeverity.Unknown;
            section.ScoreUnavailableReason = reason;
            section.ScoreDeductions.Clear();
            RemoveLegacyScoreExplanationEntries(section);
        }

        private static string BuildUnavailableReason(HealthSection section)
        {
            if (!section.HasData)
                return "Aucune donnée collectée pour cette section.";
            if (section.EvidenceData.Count == 0)
                return "Aucune métrique exploitable.";
            return "Toutes les métriques sont indisponibles.";
        }

        private static void ApplyCollectionStatusDeductions(HealthSection section, List<ScoreDeduction> deductions)
        {
            if (section.CollectionStatus.Equals("FAILED", StringComparison.OrdinalIgnoreCase))
            {
                AddDeduction(
                    deductions,
                    "collection.failed",
                    "Collecte de la section en échec.",
                    20,
                    "section.status",
                    "high");
                return;
            }

            if (section.CollectionStatus.Equals("PARTIAL", StringComparison.OrdinalIgnoreCase))
            {
                AddDeduction(
                    deductions,
                    "collection.partial",
                    "Collecte partielle de la section.",
                    6,
                    "section.status",
                    "high");
            }
        }

        private static void ApplyTopPenaltyFallbackDeductions(HealthDomain domain, ScoreV2Data scoreV2, List<ScoreDeduction> deductions)
        {
            if (scoreV2?.TopPenalties == null || scoreV2.TopPenalties.Count == 0)
                return;

            var domainPenalties = scoreV2.TopPenalties
                .Where(p => SectionToDomain.TryGetValue(p.Source, out var mappedDomain) && mappedDomain == domain)
                .Take(3)
                .ToList();

            if (domainPenalties.Count == 0)
                return;

            int applied = 0;
            foreach (var penalty in domainPenalties)
            {
                if (applied >= 12)
                    break;

                var delta = Math.Max(2, Math.Min(8, (int)Math.Round(Math.Max(0, penalty.Penalty) * 0.5)));
                if (applied + delta > 12)
                    delta = 12 - applied;
                if (delta <= 0)
                    continue;

                AddDeduction(
                    deductions,
                    $"scorev2.{penalty.Source}".ToLowerInvariant(),
                    string.IsNullOrWhiteSpace(penalty.Message) ? "Anomalie remontée par scoreV2." : penalty.Message,
                    delta,
                    $"scoreV2.topPenalties[{penalty.Source}]",
                    "medium");

                applied += delta;
            }
        }

        private static void ApplyOsDeductions(HealthSection section, List<ScoreDeduction> deductions)
        {
            var pendingValue = TryGetEvidenceValue(section, "Updates en attente", "Updates Windows");
            if (TryExtractCount(pendingValue, out var pendingCount) && pendingCount > 0)
            {
                var delta = pendingCount >= 10 ? 5 : 3;
                AddDeduction(
                    deductions,
                    "os.windows_updates.pending",
                    $"{pendingCount} mise(s) à jour en attente.",
                    delta,
                    "windows_updates.pendingCount",
                    "high");
            }

            var rebootValue = TryGetEvidenceValue(section, "Redémarrage requis");
            if (IsAffirmative(rebootValue))
            {
                AddDeduction(
                    deductions,
                    "os.windows_updates.reboot_required",
                    "Redémarrage requis après mise à jour.",
                    8,
                    "windows_updates.rebootRequired",
                    "high");
            }
        }

        private static void ApplyStorageDeductions(HealthSection section, List<ScoreDeduction> deductions)
        {
            var tempValue = TryGetEvidenceValue(section, "TempMax Disques", "Températures disques", "Temperature disques");
            if (TryExtractNumber(tempValue, out var maxDiskTemp))
            {
                if (maxDiskTemp >= 65)
                {
                    AddDeduction(
                        deductions,
                        "storage.disk_temp.danger_high",
                        $"Température disque dangereuse ({maxDiskTemp:F0}°C).",
                        14,
                        "storage.disk.maxTempC",
                        "high");
                }
                else if (maxDiskTemp >= 60)
                {
                    AddDeduction(
                        deductions,
                        "storage.disk_temp.danger",
                        $"Température disque élevée ({maxDiskTemp:F0}°C).",
                        8,
                        "storage.disk.maxTempC",
                        "high");
                }
                else if (maxDiskTemp >= 50)
                {
                    AddDeduction(
                        deductions,
                        "storage.disk_temp.caution",
                        $"Température disque en prudence ({maxDiskTemp:F0}°C).",
                        3,
                        "storage.disk.maxTempC",
                        "high");
                }
            }

            var smartValue = TryGetEvidenceValue(section, "Santé SMART", "Sante SMART", "SMART");
            if (!string.IsNullOrWhiteSpace(smartValue))
            {
                var normalized = smartValue.ToLowerInvariant();
                var hasCriticalSmartSignal =
                    normalized.Contains("défaillance", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Contains("defaillance", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Contains("predict") ||
                    normalized.Contains("failure") ||
                    normalized.Contains("failing") ||
                    normalized.Contains("critique") ||
                    normalized.Contains("critical") ||
                    normalized.Contains("bad") ||
                    normalized.Contains("danger");
                var hasWarningSmartSignal =
                    normalized.Contains("warning") ||
                    normalized.Contains("avertissement") ||
                    normalized.Contains("prudence") ||
                    normalized.Contains("caution") ||
                    normalized.Contains("attention");

                if (hasCriticalSmartSignal)
                {
                    AddDeduction(
                        deductions,
                        "storage.smart.critical",
                        $"SMART signale un risque critique ({smartValue}).",
                        18,
                        "storage.smart.health",
                        "high");
                }
                else if (hasWarningSmartSignal)
                {
                    AddDeduction(
                        deductions,
                        "storage.smart.warning",
                        $"SMART signale un avertissement ({smartValue}).",
                        8,
                        "storage.smart.health",
                        "medium");
                }
            }
        }

        private static void ApplyGpuDeductions(HealthSection section, List<ScoreDeduction> deductions)
        {
            var tdrValue = TryGetEvidenceValue(section, "TDR (crashes GPU)", "TDR 30j", "TDR", "TDR video");
            if (TryExtractCount(tdrValue, out var tdrCount) && tdrCount > 0)
            {
                var delta = tdrCount switch
                {
                    1 => 6,
                    <= 3 => 10,
                    _ => 16
                };

                AddDeduction(
                    deductions,
                    "gpu.tdr.recent_events",
                    $"{tdrCount} événement(s) TDR récent(s).",
                    delta,
                    "diagnostic_signals.tdr_video.count",
                    "high");
            }
        }

        private static void ApplyStabilityDeductions(HealthSection section, List<ScoreDeduction> deductions)
        {
            var wheaValue = TryGetEvidenceValue(section, "Erreurs WHEA", "WHEA", "WHEA 30j");
            if (TryExtractCount(wheaValue, out var wheaCount) && wheaCount > 0)
            {
                var delta = wheaCount switch
                {
                    1 => 6,
                    <= 3 => 10,
                    _ => 15
                };

                AddDeduction(
                    deductions,
                    "stability.whea.errors",
                    $"{wheaCount} erreur(s) WHEA détectée(s).",
                    delta,
                    "diagnostic_signals.whea_errors.count",
                    "high");
            }

            var bsodValue = TryGetEvidenceValue(section, "BSOD", "BSOD 30j", "BugCheck", "Bugcheck");
            if (TryExtractCount(bsodValue, out var bsodCount) && bsodCount > 0)
            {
                var delta = bsodCount switch
                {
                    1 => 5,
                    <= 3 => 10,
                    _ => 16
                };

                AddDeduction(
                    deductions,
                    "stability.bsod.recent_events",
                    $"{bsodCount} crash(s) BSOD/BugCheck récent(s).",
                    delta,
                    "diagnostic_signals.bsod_minidump.count",
                    "high");
            }

            var restorePointsValue = TryGetEvidenceValue(section, "Points de restauration", "Restore points");
            if (!string.IsNullOrWhiteSpace(restorePointsValue) &&
                !IsUnavailableEvidence(restorePointsValue) &&
                TryExtractCount(restorePointsValue, out var restorePointsCount) &&
                restorePointsCount == 0)
            {
                AddDeduction(
                    deductions,
                    "stability.restore_points.none",
                    "Aucun point de restauration disponible.",
                    4,
                    "scan_powershell.sections.RestorePoints.data.restorePointCount",
                    "medium");
            }
        }

        private static void WriteScoreBreakdown(HealthSection section)
        {
            // Product requirement: no "Pourquoi ce score ?" rows in section evidence.
            RemoveLegacyScoreExplanationEntries(section);
        }

        private static readonly string[] LegacyScoreExplanationKeys =
        {
            "Pourquoi ce score ?",
            "Pourquoi ce score?"
        };

        private static void RemoveLegacyScoreExplanationEntries(HealthSection section)
        {
            if (section.EvidenceData != null)
            {
                foreach (var key in LegacyScoreExplanationKeys)
                    section.EvidenceData.Remove(key);
            }

            if (section.EvidenceTooltips != null)
            {
                foreach (var key in LegacyScoreExplanationKeys)
                    section.EvidenceTooltips.Remove(key);
            }
        }

        private static void AddDeduction(List<ScoreDeduction> deductions, string ruleId, string reason, int delta, string sourceMetric, string confidence)
        {
            if (delta <= 0)
                return;

            deductions.Add(new ScoreDeduction
            {
                RuleId = ruleId,
                Reason = reason,
                Delta = delta,
                SourceMetric = sourceMetric,
                Confidence = confidence
            });
        }

        private static string? TryGetEvidenceValue(HealthSection section, params string[] keys)
        {
            foreach (var key in keys)
            {
                foreach (var kvp in section.EvidenceData)
                {
                    if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(kvp.Value))
                    {
                        return kvp.Value;
                    }
                }
            }

            return null;
        }

        private static bool TryExtractCount(string? text, out int count)
        {
            if (TryExtractNumber(text, out var number))
            {
                count = (int)Math.Round(number);
                return true;
            }

            count = 0;
            return false;
        }

        private static bool TryExtractNumber(string? text, out double number)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                var match = FirstNumberRegex.Match(text);
                if (match.Success)
                {
                    var normalized = match.Value.Replace(',', '.');
                    if (double.TryParse(normalized, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number))
                        return true;
                }
            }

            number = 0;
            return false;
        }

        private static bool IsAffirmative(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var normalized = value.Trim().ToLowerInvariant();
            return normalized.Contains("oui") ||
                   normalized.Contains("yes") ||
                   normalized.Contains("true");
        }

        private static bool IsUnavailableEvidence(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            var normalized = value.ToLowerInvariant();
            return normalized.Contains("indisponible") ||
                   normalized.Contains("non disponible") ||
                   normalized.Contains("inconnu") ||
                   normalized.Contains("unknown") ||
                   normalized.Contains("n/a");
        }

        private static string GetWorstStatus(List<(string sectionName, JsonElement data, string status)> sectionData)
        {
            if (sectionData.Any(s => s.status == "FAILED")) return "FAILED";
            if (sectionData.Any(s => s.status == "PARTIAL")) return "PARTIAL";
            return "OK";
        }

        private static Dictionary<string, string> ExtractEvidenceData(HealthDomain domain, List<(string sectionName, JsonElement data, string status)> sectionData)
        {
            var evidence = new Dictionary<string, string>();
            
            foreach (var (sectionName, data, _) in sectionData)
            {
                try
                {
                    switch (domain)
                    {
                        case HealthDomain.OS:
                            if (sectionName == "OS" && data.ValueKind == JsonValueKind.Object)
                            {
                                // Version (caption)
                                if (data.TryGetProperty("caption", out var caption))
                                {
                                    var capStr = caption.GetString() ?? "";
                                    if (!string.IsNullOrEmpty(capStr))
                                        evidence["Version"] = capStr;
                                }
                                
                                // Build number
                                if (data.TryGetProperty("buildNumber", out var build))
                                {
                                    var buildStr = build.GetString() ?? build.ToString();
                                    if (!string.IsNullOrEmpty(buildStr))
                                        evidence["Build"] = buildStr;
                                }
                                
                                // Architecture
                                if (data.TryGetProperty("architecture", out var arch))
                                {
                                    var archStr = arch.GetString() ?? "";
                                    if (!string.IsNullOrEmpty(archStr))
                                        evidence["Architecture"] = archStr;
                                }
                                
                                // Computer name
                                if (data.TryGetProperty("computerName", out var compName))
                                {
                                    var nameStr = compName.GetString() ?? "";
                                    if (!string.IsNullOrEmpty(nameStr))
                                        evidence["Nom machine"] = nameStr;
                                }
                                
                                // Install date
                                if (data.TryGetProperty("installDate", out var installDate))
                                {
                                    var dateStr = installDate.GetString();
                                    if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var dt))
                                    {
                                        evidence["Date installation"] = dt.ToString("d MMMM yyyy");
                                    }
                                    else if (!string.IsNullOrEmpty(dateStr))
                                    {
                                        evidence["Date installation"] = dateStr;
                                    }
                                }
                                
                                // Last boot time / Uptime
                                if (data.TryGetProperty("lastBootUpTime", out var lastBoot))
                                {
                                    var bootStr = lastBoot.GetString();
                                    if (!string.IsNullOrEmpty(bootStr) && DateTime.TryParse(bootStr, out var bootDt))
                                    {
                                        var uptime = DateTime.Now - bootDt;
                                        var uptimeStr = uptime.TotalDays >= 1 
                                            ? $"{(int)uptime.TotalDays}j {uptime.Hours}h {uptime.Minutes}min"
                                            : $"{uptime.Hours}h {uptime.Minutes}min";
                                        evidence["Uptime"] = uptimeStr;
                                    }
                                }
                            }
                            break;
                            
                        case HealthDomain.CPU:
                            if (sectionName == "CPU" && data.ValueKind == JsonValueKind.Object)
                            {
                                // Supporte 'cpus' (sortie PS) et 'cpuList' comme alias
                                JsonElement cpuArray = default;
                                bool hasCpuArray = false;
                                
                                if (data.TryGetProperty("cpus", out var cpusEl) && cpusEl.ValueKind == JsonValueKind.Array)
                                {
                                    cpuArray = cpusEl;
                                    hasCpuArray = true;
                                }
                                else if (data.TryGetProperty("cpuList", out var cpuListEl) && cpuListEl.ValueKind == JsonValueKind.Array)
                                {
                                    cpuArray = cpuListEl;
                                    hasCpuArray = true;
                                }
                                
                                if (hasCpuArray)
                                {
                                    var firstCpu = cpuArray.EnumerateArray().FirstOrDefault();
                                    if (firstCpu.ValueKind == JsonValueKind.Object)
                                    {
                                        // Modèle
                                        if (firstCpu.TryGetProperty("name", out var name))
                                        {
                                            var nameStr = name.GetString()?.Trim() ?? "";
                                            if (!string.IsNullOrEmpty(nameStr))
                                                evidence["Modèle"] = nameStr;
                                        }
                                        
                                        // CÅ“urs
                                        if (firstCpu.TryGetProperty("cores", out var cores))
                                            evidence["CÅ“urs"] = cores.ToString();
                                        
                                        // Threads
                                        if (firstCpu.TryGetProperty("threads", out var threads))
                                            evidence["Threads"] = threads.ToString();
                                        
                                        // Fréquence max
                                        if (firstCpu.TryGetProperty("maxClockSpeed", out var maxClock))
                                        {
                                            var mhz = SafeGetDouble(maxClock, 0);
                                            if (mhz > 0)
                                                evidence["Fréquence max"] = $"{mhz:F0} MHz";
                                        }
                                        
                                        // Charge actuelle (currentLoad or load)
                                        if (firstCpu.TryGetProperty("currentLoad", out var load))
                                        {
                                            evidence["Charge actuelle"] = $"{SafeGetDouble(load, 0):F0} %";
                                        }
                                        else if (firstCpu.TryGetProperty("load", out var load2))
                                        {
                                            evidence["Charge actuelle"] = $"{SafeGetDouble(load2, 0):F0} %";
                                        }
                                    }
                                }
                                
                                // Nombre de CPU
                                if (data.TryGetProperty("cpuCount", out var cpuCount))
                                {
                                    var count = SafeGetInt(cpuCount, 0);
                                    if (count > 0)
                                        evidence["Nombre de CPU"] = count.ToString();
                                }
                            }
                            break;
                            
                        case HealthDomain.GPU:
                            if (sectionName == "GPU" && data.ValueKind == JsonValueKind.Object)
                            {
                                // Try 'gpuList' first, then 'gpus' for compatibility
                                JsonElement gpuArray = default;
                                bool hasGpuArray = false;
                                
                                if (data.TryGetProperty("gpuList", out var gpuListEl) && gpuListEl.ValueKind == JsonValueKind.Array)
                                {
                                    gpuArray = gpuListEl;
                                    hasGpuArray = true;
                                }
                                else if (data.TryGetProperty("gpus", out var gpusEl) && gpusEl.ValueKind == JsonValueKind.Array)
                                {
                                    gpuArray = gpusEl;
                                    hasGpuArray = true;
                                }
                                
                                if (hasGpuArray)
                                {
                                    var firstGpu = gpuArray.EnumerateArray().FirstOrDefault();
                                    if (firstGpu.ValueKind == JsonValueKind.Object)
                                    {
                                        // Nom
                                        if (firstGpu.TryGetProperty("name", out var name))
                                        {
                                            var nameStr = name.GetString()?.Trim() ?? "";
                                            if (!string.IsNullOrEmpty(nameStr))
                                                evidence["Nom"] = nameStr;
                                        }
                                        
                                        // Fabricant (vendor)
                                        if (firstGpu.TryGetProperty("vendor", out var vendor))
                                        {
                                            var vendorStr = vendor.GetString()?.Trim() ?? "";
                                            if (!string.IsNullOrEmpty(vendorStr))
                                                evidence["Fabricant"] = vendorStr;
                                        }
                                        
                                        // Résolution
                                        if (firstGpu.TryGetProperty("resolution", out var res))
                                        {
                                            var resStr = res.GetString() ?? "";
                                            if (!string.IsNullOrEmpty(resStr))
                                                evidence["Résolution"] = resStr;
                                        }
                                        
                                        // Version pilote
                                        if (firstGpu.TryGetProperty("driverVersion", out var driverVer))
                                        {
                                            var verStr = driverVer.GetString() ?? "";
                                            if (!string.IsNullOrEmpty(verStr))
                                                evidence["Version pilote"] = verStr;
                                        }
                                        
                                        // Date pilote (nested: driverDate.DateTime or driverDate directly)
                                        if (firstGpu.TryGetProperty("driverDate", out var driverDateEl))
                                        {
                                            string? dateStr = null;
                                            if (driverDateEl.ValueKind == JsonValueKind.Object && 
                                                driverDateEl.TryGetProperty("DateTime", out var dateTimeEl))
                                            {
                                                dateStr = dateTimeEl.GetString();
                                            }
                                            else if (driverDateEl.ValueKind == JsonValueKind.String)
                                            {
                                                dateStr = driverDateEl.GetString();
                                            }
                                            if (!string.IsNullOrEmpty(dateStr))
                                                evidence["Date pilote"] = dateStr;
                                        }
                                        
                                        // VRAM: Try vramTotalMB first, fallback to vramNote, then adapterRAM_GB
                                        bool vramFound = false;
                                        
                                        if (firstGpu.TryGetProperty("vramTotalMB", out var vramMB))
                                        {
                                            var mb = SafeGetDouble(vramMB, 0);
                                            if (mb > 0)
                                            {
                                                evidence["VRAM totale"] = mb >= 1024 
                                                    ? $"{mb / 1024:F1} GB" 
                                                    : $"{mb:F0} MB";
                                                vramFound = true;
                                            }
                                        }
                                        
                                        // Fallback to vramNote if vramTotalMB is null/0
                                        if (!vramFound && firstGpu.TryGetProperty("vramNote", out var vramNote))
                                        {
                                            var noteStr = vramNote.GetString();
                                            if (!string.IsNullOrEmpty(noteStr))
                                            {
                                                evidence["VRAM totale"] = noteStr;
                                                vramFound = true;
                                            }
                                        }
                                        
                                        // Fallback to adapterRAM_GB (legacy field)
                                        if (!vramFound && firstGpu.TryGetProperty("adapterRAM_GB", out var adapterRam))
                                        {
                                            var gb = SafeGetDouble(adapterRam, 0);
                                            if (gb > 0)
                                                evidence["VRAM totale"] = $"{gb:F1} GB";
                                        }
                                    }
                                }
                                
                                // Nombre de GPU
                                if (data.TryGetProperty("gpuCount", out var gpuCount))
                                {
                                    var count = SafeGetInt(gpuCount, 0);
                                    if (count > 0)
                                        evidence["Nombre de GPU"] = count.ToString();
                                }
                            }
                            break;
                            
                        case HealthDomain.RAM:
                            if (sectionName == "Memory" && data.ValueKind == JsonValueKind.Object)
                            {
                                double? totalGB = null;
                                double? availableGB = null;
                                
                                if (data.TryGetProperty("totalGB", out var total))
                                {
                                    var t = SafeGetDouble(total, -1);
                                    totalGB = t >= 0 ? t : (double?)null;
                                    if (totalGB > 0)
                                        evidence["Total"] = $"{totalGB:F1} GB";
                                }
                                
                                if (data.TryGetProperty("availableGB", out var avail))
                                {
                                    var a = SafeGetDouble(avail, -1);
                                    availableGB = a >= 0 ? a : (double?)null;
                                    evidence["Disponible"] = $"{availableGB:F1} GB";
                                }
                                
                                // Compute and show usage percentage
                                if (totalGB.HasValue && totalGB > 0 && availableGB.HasValue)
                                {
                                    var usedGB = totalGB.Value - availableGB.Value;
                                    var usedPercent = (usedGB / totalGB.Value) * 100;
                                    evidence["Utilisée"] = $"{usedGB:F1} GB ({usedPercent:F0} %)";
                                }
                                
                                // Memory modules (slots) if available
                                if (data.TryGetProperty("modules", out var modulesEl) && modulesEl.ValueKind == JsonValueKind.Array)
                                {
                                    var moduleCount = modulesEl.GetArrayLength();
                                    if (moduleCount > 0)
                                        evidence["Barrettes"] = moduleCount.ToString();
                                }
                                else if (data.TryGetProperty("moduleCount", out var modCount))
                                {
                                    var count = SafeGetInt(modCount, 0);
                                    if (count > 0)
                                        evidence["Barrettes"] = count.ToString();
                                }
                            }
                            break;
                            
                        case HealthDomain.Storage:
                            if (sectionName == "Storage" && data.ValueKind == JsonValueKind.Object)
                            {
                                if (data.TryGetProperty("disks", out var disks) && disks.ValueKind == JsonValueKind.Array)
                                {
                                    int diskCount = 0;
                                    double totalSpace = 0;
                                    foreach (var disk in disks.EnumerateArray())
                                    {
                                        diskCount++;
                                        if (disk.TryGetProperty("sizeGB", out var size)) totalSpace += SafeGetDouble(size, 0);
                                    }
                                    evidence["Disques"] = diskCount.ToString();
                                    evidence["Capacité totale"] = $"{totalSpace:F0} GB";
                                }
                                
                                // === P0-C: Volume C: espace libre ===
                                if (data.TryGetProperty("volumes", out var volumes) && volumes.ValueKind == JsonValueKind.Array)
                                {
                                    double? minFreePercent = null;
                                    string criticalVolume = "";
                                    
                                    foreach (var vol in volumes.EnumerateArray())
                                    {
                                        string letter = "";
                                        double sizeGB = 0, freeGB = 0;
                                        
                                        if (vol.TryGetProperty("driveLetter", out var dl)) letter = dl.GetString() ?? "";
                                        if (vol.TryGetProperty("sizeGB", out var s)) sizeGB = SafeGetDouble(s, 0);
                                        if (vol.TryGetProperty("freeSpaceGB", out var f)) freeGB = SafeGetDouble(f, 0);
                                        
                                        double freePercent = sizeGB > 0 ? (freeGB / sizeGB * 100) : 0;
                                        
                                        // Volume C: spécifiquement
                                        if (letter.ToUpper() == "C")
                                        {
                                            evidence["C: Espace libre"] = $"{freeGB:F1} GB ({freePercent:F0}%)";
                                            evidence["C: Taille"] = $"{sizeGB:F1} GB";
                                        }
                                        
                                        // Trouver le volume le plus critique
                                        if (!minFreePercent.HasValue || freePercent < minFreePercent)
                                        {
                                            minFreePercent = freePercent;
                                            criticalVolume = $"{letter}: {freeGB:F1} GB ({freePercent:F0}%)";
                                        }
                                    }
                                    
                                    if (!string.IsNullOrEmpty(criticalVolume))
                                    {
                                        evidence["Volume critique"] = criticalVolume;
                                    }
                                }
                            }
                            break;
                            
                        case HealthDomain.Network:
                            if (sectionName == "Network" && data.ValueKind == JsonValueKind.Object)
                            {
                                if (data.TryGetProperty("adapters", out var adapters) && adapters.ValueKind == JsonValueKind.Array)
                                {
                                    var activeAdapter = adapters.EnumerateArray().FirstOrDefault();
                                    if (activeAdapter.ValueKind == JsonValueKind.Object)
                                    {
                                        // Adapter name
                                        if (activeAdapter.TryGetProperty("name", out var name))
                                        {
                                            var nameStr = name.GetString() ?? "";
                                            if (!string.IsNullOrEmpty(nameStr))
                                                evidence["Adaptateur"] = nameStr;
                                        }
                                        
                                        // IP address
                                        if (activeAdapter.TryGetProperty("ipv4", out var ipv4))
                                        {
                                            var ipStr = ipv4.GetString() ?? "";
                                            if (!string.IsNullOrEmpty(ipStr))
                                                evidence["Adresse IP"] = ipStr;
                                        }
                                        
                                        // MAC address
                                        if (activeAdapter.TryGetProperty("macAddress", out var mac))
                                        {
                                            var macStr = mac.GetString() ?? "";
                                            if (!string.IsNullOrEmpty(macStr))
                                                evidence["Adresse MAC"] = macStr;
                                        }
                                        
                                        // Connection status
                                        if (activeAdapter.TryGetProperty("status", out var status))
                                        {
                                            var statusStr = status.GetString() ?? "";
                                            if (!string.IsNullOrEmpty(statusStr))
                                                evidence["Statut"] = statusStr;
                                        }
                                        
                                        // Speed
                                        if (activeAdapter.TryGetProperty("speed", out var speed))
                                        {
                                            var speedStr = speed.GetString() ?? "";
                                            if (!string.IsNullOrEmpty(speedStr))
                                                evidence["Vitesse"] = speedStr;
                                        }
                                        else if (activeAdapter.TryGetProperty("speedMbps", out var speedMbps))
                                        {
                                            var mbps = SafeGetDouble(speedMbps, 0);
                                            if (mbps > 0)
                                                evidence["Vitesse"] = $"{mbps:F0} Mbps";
                                        }
                                        
                                        // Gateway
                                        if (activeAdapter.TryGetProperty("gateway", out var gateway))
                                        {
                                            var gwStr = gateway.GetString() ?? "";
                                            if (!string.IsNullOrEmpty(gwStr))
                                                evidence["Passerelle"] = gwStr;
                                        }
                                        
                                        // DNS
                                        if (activeAdapter.TryGetProperty("dns", out var dns))
                                        {
                                            if (dns.ValueKind == JsonValueKind.Array)
                                            {
                                                var dnsServers = string.Join(", ", dns.EnumerateArray()
                                                    .Select(d => d.GetString())
                                                    .Where(s => !string.IsNullOrEmpty(s)));
                                                if (!string.IsNullOrEmpty(dnsServers))
                                                    evidence["DNS"] = dnsServers;
                                            }
                                            else if (dns.ValueKind == JsonValueKind.String)
                                            {
                                                var dnsStr = dns.GetString() ?? "";
                                                if (!string.IsNullOrEmpty(dnsStr))
                                                    evidence["DNS"] = dnsStr;
                                            }
                                        }
                                    }
                                    
                                    // Total adapters count
                                    var adapterCount = adapters.GetArrayLength();
                                    if (adapterCount > 1)
                                        evidence["Adaptateurs détectés"] = adapterCount.ToString();
                                }
                            }
                            break;
                            
                        case HealthDomain.Drivers:
                            if (sectionName == "DevicesDrivers" && data.ValueKind == JsonValueKind.Object)
                            {
                                var problemCount = data.TryGetProperty("problemDeviceCount", out var pc) ? SafeGetInt(pc, -1) : 
                                                   data.TryGetProperty("ProblemDeviceCount", out var pc2) ? SafeGetInt(pc2, -1) : -1;
                                if (problemCount >= 0)
                                {
                                    evidence["Périph. en erreur"] = problemCount > 0 ? $"⚠️ {problemCount}" : "0 ✅";
                                }
                                if (data.TryGetProperty("problemDevices", out var pd) && pd.ValueKind == JsonValueKind.Array)
                                {
                                    var count = pd.GetArrayLength();
                                    if (count > 0 && problemCount < 0)
                                    {
                                        evidence["Périph. en erreur"] = $"⚠️ {count}";
                                    }
                                }
                            }
                            else if (sectionName == "Audio" && data.ValueKind == JsonValueKind.Object)
                            {
                                var deviceCount = data.TryGetProperty("deviceCount", out var dc) ? SafeGetInt(dc, -1) :
                                                  data.TryGetProperty("DeviceCount", out var dc2) ? SafeGetInt(dc2, -1) : -1;
                                if (deviceCount >= 0)
                                {
                                    evidence["Périph. audio"] = deviceCount.ToString();
                                }
                            }
                            else if (sectionName == "Printers" && data.ValueKind == JsonValueKind.Object)
                            {
                                var printerCount = data.TryGetProperty("printerCount", out var prc) ? SafeGetInt(prc, -1) :
                                                   data.TryGetProperty("PrinterCount", out var prc2) ? SafeGetInt(prc2, -1) : -1;
                                if (printerCount >= 0)
                                {
                                    evidence["Imprimantes"] = printerCount.ToString();
                                }
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[HealthReportBuilder] Warning: Erreur extraction evidence {domain}/{sectionName}: {ex.Message}");
                }
            }
            
            return evidence;
        }

        private static List<HealthFinding> ExtractFindings(HealthDomain domain, ScoreV2Data scoreV2)
        {
            var findings = new List<HealthFinding>();
            
            foreach (var penalty in scoreV2.TopPenalties)
            {
                if (SectionToDomain.TryGetValue(penalty.Source, out var penaltyDomain) && penaltyDomain == domain)
                {
                    findings.Add(new HealthFinding
                    {
                        Severity = penalty.Type switch
                        {
                            "CRITICAL" => HealthSeverity.Critical,
                            "COLLECTOR_ERROR" => HealthSeverity.Degraded,
                            "WARN" or "WARNING" => HealthSeverity.Warning,
                            _ => HealthSeverity.Healthy
                        },
                        Title = penalty.Type,
                        Description = penalty.Message,
                        Source = penalty.Source,
                        PenaltyApplied = penalty.Penalty
                    });
                }
            }
            
            return findings;
        }

        private static string GenerateSectionMessage(HealthSection section)
        {
            return section.Severity switch
            {
                HealthSeverity.Excellent => "Excellent état",
                HealthSeverity.Healthy => "Bon état",
                HealthSeverity.Warning => "Attention recommandée",
                HealthSeverity.Degraded => "Action requise",
                HealthSeverity.Critical => "Intervention urgente",
                _ => "Données non disponibles"
            };
        }

        private static string GenerateDetailedExplanation(HealthSection section)
        {
            var explanation = $"Le {section.DisplayName.ToLower()} de votre ordinateur ";
            
            explanation += section.Severity switch
            {
                HealthSeverity.Excellent => "fonctionne de manière optimale. Aucune action n'est nécessaire.",
                HealthSeverity.Healthy => "fonctionne correctement. Continuez à maintenir votre système à jour.",
                HealthSeverity.Warning => "présente des signes de dégradation légère. Il est recommandé de surveiller cette composante.",
                HealthSeverity.Degraded => "nécessite votre attention. Des problèmes ont été détectés qui pourraient affecter les performances.",
                HealthSeverity.Critical => "présente des problèmes critiques qui nécessitent une intervention immédiate.",
                _ => "n'a pas pu être analysé correctement."
            };
            
            if (section.Findings.Count > 0)
            {
                explanation += $"\n\nProblèmes détectés : {section.Findings.Count}";
            }
            
            return explanation;
        }

        private static List<string> GenerateSectionRecommendations(HealthSection section)
        {
            var recommendations = new List<string>();
            
            if (section.Severity >= HealthSeverity.Warning)
            {
                switch (section.Domain)
                {
                    case HealthDomain.OS:
                        recommendations.Add("Vérifiez les mises à jour Windows");
                        recommendations.Add("Exécutez une analyse antivirus");
                        break;
                    case HealthDomain.CPU:
                        recommendations.Add("Vérifiez la ventilation de l'ordinateur");
                        recommendations.Add("Fermez les programmes inutilisés");
                        break;
                    case HealthDomain.GPU:
                        recommendations.Add("Mettez à jour les pilotes graphiques");
                        break;
                    case HealthDomain.RAM:
                        recommendations.Add("Fermez les programmes gourmands en mémoire");
                        recommendations.Add("Envisagez d'ajouter de la RAM si récurrent");
                        break;
                    case HealthDomain.Storage:
                        recommendations.Add("Libérez de l'espace disque");
                        recommendations.Add("Vérifiez l'état SMART des disques");
                        break;
                    case HealthDomain.Network:
                        recommendations.Add("Vérifiez votre connexion internet");
                        recommendations.Add("Redémarrez votre routeur si nécessaire");
                        break;
                    case HealthDomain.SystemStability:
                        recommendations.Add("Consultez les journaux d'événements");
                        recommendations.Add("Créez un point de restauration");
                        break;
                    case HealthDomain.Drivers:
                        recommendations.Add("Mettez à jour les pilotes obsolètes");
                        recommendations.Add("Désinstallez les pilotes inutilisés");
                        break;
                }
            }
            
            return recommendations;
        }

        private static string GenerateGlobalMessage(HealthReport report)
        {
            return report.GlobalSeverity switch
            {
                HealthSeverity.Excellent => "Votre PC est en excellent état ! Tout fonctionne parfaitement.",
                HealthSeverity.Healthy => "Votre PC est en bon état. Quelques optimisations mineures sont possibles.",
                HealthSeverity.Warning => "Votre PC nécessite une attention particulière. Des problèmes mineurs ont été détectés.",
                HealthSeverity.Degraded => "Votre PC présente des problèmes significatifs qui affectent ses performances.",
                HealthSeverity.Critical => "Votre PC nécessite une intervention urgente ! Des problèmes critiques ont été détectés.",
                _ => "Impossible d'évaluer l'état de votre PC. Certaines données sont manquantes."
            };
        }

        private static List<HealthRecommendation> GenerateRecommendations(HealthReport report)
        {
            var recommendations = new List<HealthRecommendation>();
            
            // Recommandations depuis les top penalties
            foreach (var penalty in report.ScoreV2.TopPenalties.Take(5))
            {
                var severity = penalty.Type switch
                {
                    "CRITICAL" => HealthSeverity.Critical,
                    "COLLECTOR_ERROR" => HealthSeverity.Degraded,
                    "WARN" or "WARNING" => HealthSeverity.Warning,
                    _ => HealthSeverity.Healthy
                };
                
                SectionToDomain.TryGetValue(penalty.Source, out var domain);
                
                recommendations.Add(new HealthRecommendation
                {
                    Priority = severity,
                    RelatedDomain = domain,
                    Title = $"Problème: {penalty.Source}",
                    Description = penalty.Message,
                    ActionText = "Voir les détails"
                });
            }
            
            // Trier par priorité
            return recommendations.OrderByDescending(r => r.Priority).ToList();
        }
        
        /// <summary>
        /// Récupère les pilotes essentiels via WMI pour le domaine Drivers (fallback quand PS est vide)
        /// </summary>
        private static List<(string cls, string? name, string? version, string date)> GetEssentialDriversFromWmiForHealth()
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
                        var date = ParseWmiDateForHealth(dateRaw);
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
                App.LogMessage($"[HealthReportBuilder] GetEssentialDriversFromWmiForHealth WMI failed: {ex.Message}");
            }
            return result;
        }
        
        private static string ParseWmiDateForHealth(string? wmiDate)
        {
            if (string.IsNullOrEmpty(wmiDate) || wmiDate.Length < 8) return "";
            try
            {
                var y = wmiDate.Substring(0, 4);
                var m = wmiDate.Substring(4, 2);
                var d = wmiDate.Substring(6, 2);
                return $"{y}-{m}-{d}";
            }
            catch { return wmiDate ?? ""; }
        }
        
        /// <summary>Helper: extrait un int depuis un JsonElement de façon sûre (Number, String, ou Object avec "value").</summary>
        private static int SafeGetInt(JsonElement el, int defaultValue = 0)
        {
            if (el.ValueKind == JsonValueKind.Number) return el.GetInt32();
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var i)) return i;
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("value", out var v)) return SafeGetInt(v, defaultValue);
            return defaultValue;
        }
        
        /// <summary>Helper: extrait un double depuis un JsonElement de façon sûre (Number, String, ou Object avec "value").</summary>
        private static double SafeGetDouble(JsonElement el, double defaultValue = 0)
        {
            if (el.ValueKind == JsonValueKind.Number) return el.GetDouble();
            if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), out var d)) return d;
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("value", out var v)) return SafeGetDouble(v, defaultValue);
            return defaultValue;
        }
    }
}



