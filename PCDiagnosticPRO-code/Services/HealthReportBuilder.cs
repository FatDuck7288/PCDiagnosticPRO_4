using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Construit un HealthReport industriel depuis le JSON PowerShell.
    /// Utilise scoreV2 du PS comme source de vérité pour le score.
    /// </summary>
    public static class HealthReportBuilder
    {
        /// <summary>
        /// Mapping des sections JSON vers les domaines de santé
        /// Extended to cover all PS sections (Schema 2.1.0)
        /// </summary>
        private static readonly Dictionary<string, HealthDomain> SectionToDomain = new(StringComparer.OrdinalIgnoreCase)
        {
            // OS
            { "OS", HealthDomain.OS },
            { "MachineIdentity", HealthDomain.OS },
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
            { HealthDomain.CPU, "⚡" },
            { HealthDomain.GPU, "🎮" },
            { HealthDomain.RAM, "🧠" },
            { HealthDomain.Storage, "💾" },
            { HealthDomain.Network, "🌐" },
            { HealthDomain.SystemStability, "🛡️" },
            { HealthDomain.Drivers, "🔧" },
            { HealthDomain.Applications, "📦" },
            { HealthDomain.Performance, "📊" },
            { HealthDomain.Security, "🔒" },
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
            { HealthDomain.Power, "Alimentation" }
        };

        /// <summary>
        /// Construit un HealthReport depuis le JSON brut du PowerShell (sans capteurs)
        /// </summary>
        public static HealthReport Build(string jsonContent)
        {
            return Build(jsonContent, null, null, null);
        }

        /// <summary>
        /// Construit un HealthReport depuis le JSON brut du PowerShell AVEC données capteurs hardware.
        /// P0/P1: collectorErrorsLogical, missingData/topPenalties flexibles, FinalScoreCalculator, DataSanitizer.
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
                
                // 1. Extraire metadata
                report.Metadata = ExtractMetadata(root);
                
                // 2. Extraire scoreV2 (SOURCE DE VÉRITÉ PS)
                report.ScoreV2 = ExtractScoreV2(root);
                report.GlobalScore = report.ScoreV2.Score;
                report.Grade = report.ScoreV2.Grade;
                report.GlobalSeverity = HealthReport.ScoreToSeverity(report.GlobalScore);
                
                // 3. DIAGNOSTICS COLLECTE (P0.1, P1.2): Analyse root + sanitize sensors, missingData/topPenalties flexibles
                var diagnostics = CollectorDiagnosticsService.Analyze(root, sensors);
                report.Errors = diagnostics.Errors;
                report.MissingData = diagnostics.MissingDataNormalized;
                report.ScoreV2.TopPenalties = diagnostics.TopPenaltiesNormalized;
                report.CollectorErrorsLogical = diagnostics.CollectorErrorsLogical;
                if (report.Metadata.PartialFailure || diagnostics.CollectionStatus == "FAILED")
                    report.CollectorErrorsLogical = Math.Max(report.CollectorErrorsLogical, 1);
                report.CollectionStatus = diagnostics.CollectionStatus;
                
                App.LogMessage($"COLLECTOR_ERRORS_LOGICAL={report.CollectorErrorsLogical} (from errors[]={report.Errors.Count})");
                
                // 4. Construire les sections par domaine avec extraction complète (PS + C# sensors + diagnostics)
                report.Sections = BuildHealthSections(root, report.ScoreV2, sensors);
                
                // 4.1 Injecter les données C# pour Drivers / Updates si disponibles
                if (driverInventory != null)
                    InjectDriverInventory(report, driverInventory);
                if (updatesCsharp != null)
                    InjectUpdatesCsharp(report, updatesCsharp);
                
                // 5. INJECTER LES DONNÉES CAPTEURS HARDWARE (déjà sanitized par Analyze)
                if (sensors != null)
                    InjectHardwareSensors(report, sensors);
                
                // 6. Modèle de confiance (pour DRS et affichage)
                report.ConfidenceModel = BuildConfidenceModel(report, sensors);
                report.ConfidenceModel.ConfidenceScore = CollectorDiagnosticsService.ApplyConfidenceGating(report.ConfidenceModel.ConfidenceScore, diagnostics);
                
                // 7. UDIS — Unified Diagnostic Intelligence Scoring (remplace GradeEngine + ScoreV2)
                var udis = UnifiedDiagnosticScoreEngine.Compute(report, root, sensors, diagnostics);
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
                report.Divergence.PowerShellScore = report.ScoreV2.Score;
                report.Divergence.PowerShellGrade = report.ScoreV2.Grade;
                report.Divergence.GradeEngineScore = udis.UdisScore;
                report.Divergence.GradeEngineGrade = udis.Grade;
                report.Divergence.SourceOfTruth = "UDIS (Unified Diagnostic Intelligence Scoring)";
                
                // 8. Verdict si collecte FAILED/PARTIAL
                if (report.CollectionStatus == "FAILED" || report.CollectionStatus == "PARTIAL")
                {
                    report.GlobalMessage = report.CollectionStatus == "FAILED"
                        ? "Collecte échouée : interprétation prudente"
                        : "Collecte partielle : interprétation prudente";
                }
                
                // 9. Recommandations
                report.Recommendations = GenerateRecommendations(report);
                
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
            }
            
            return report;
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

            // Injection CPU
            if (cpuSection != null && sensors.Cpu.CpuTempC.Available)
            {
                cpuSection.EvidenceData["Temperature"] = $"{sensors.Cpu.CpuTempC.Value:F1}°C";
                cpuSection.HasData = true;
                App.LogMessage($"[Sensors→CPU] Température injectée: {sensors.Cpu.CpuTempC.Value:F1}°C");
            }

            // Injection GPU
            if (gpuSection != null)
            {
                if (sensors.Gpu.Name.Available)
                    gpuSection.EvidenceData["GPU"] = sensors.Gpu.Name.Value ?? "N/A";
                
                if (sensors.Gpu.GpuTempC.Available)
                {
                    gpuSection.EvidenceData["Temperature"] = $"{sensors.Gpu.GpuTempC.Value:F1}°C";
                    App.LogMessage($"[Sensors→GPU] Température injectée: {sensors.Gpu.GpuTempC.Value:F1}°C");
                }
                
                if (sensors.Gpu.GpuLoadPercent.Available)
                {
                    gpuSection.EvidenceData["Load"] = $"{sensors.Gpu.GpuLoadPercent.Value:F0}%";
                    App.LogMessage($"[Sensors→GPU] Charge injectée: {sensors.Gpu.GpuLoadPercent.Value:F0}%");
                }
                
                if (sensors.Gpu.VramTotalMB.Available && sensors.Gpu.VramUsedMB.Available)
                {
                    var vramUsedPct = (sensors.Gpu.VramUsedMB.Value / sensors.Gpu.VramTotalMB.Value) * 100;
                    gpuSection.EvidenceData["VRAM Total"] = $"{sensors.Gpu.VramTotalMB.Value:F0} MB";
                    gpuSection.EvidenceData["VRAM Utilisée"] = $"{sensors.Gpu.VramUsedMB.Value:F0} MB ({vramUsedPct:F0}%)";
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
            driversSection.EvidenceData["Pilotes détectés"] = driverInventory.TotalCount.ToString();
            if (driverInventory.UnsignedCount > 0)
                driversSection.EvidenceData["Non signés"] = driverInventory.UnsignedCount.ToString();
            if (driverInventory.ProblemCount > 0)
                driversSection.EvidenceData["Périph. en erreur"] = driverInventory.ProblemCount.ToString();

            var outdated = driverInventory.Drivers.Count(d => d.UpdateStatus == "Outdated");
            if (outdated > 0)
                driversSection.EvidenceData["Obsolètes"] = outdated.ToString();

            if (driverInventory.ByClass.Count > 0)
            {
                var topClasses = string.Join(", ", driverInventory.ByClass.Keys.Take(5));
                driversSection.EvidenceData["Classes"] = topClasses;
            }

            // Only override status if section was previously empty/unknown
            if (driversSection.Score == 0 && driversSection.Severity == HealthSeverity.Unknown)
            {
                driversSection.Score = outdated > 0 ? 70 : 85;
                driversSection.Severity = outdated > 0 ? HealthSeverity.Warning : HealthSeverity.Healthy;
                driversSection.StatusMessage = outdated > 0 ? "Mises à jour recommandées" : "Pilotes détectés";
                driversSection.DetailedExplanation = outdated > 0
                    ? "Des mises à jour de pilotes sont disponibles via Windows Update."
                    : "Inventaire pilotes détecté via WMI (Windows).";
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
            osSection.EvidenceData["UpdateStatus"] = updatesCsharp.PendingCount > 0
                ? $"Obsolète ({updatesCsharp.PendingCount})"
                : "À jour";

            if (updatesCsharp.RebootRequired.HasValue)
                osSection.EvidenceData["Redémarrage requis"] = updatesCsharp.RebootRequired.Value ? "Oui" : "Non";
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
            var collectorErrors = report.CollectorErrorsLogical > 0 ? report.CollectorErrorsLogical : report.ScoreV2.Breakdown.CollectorErrors;
            if (collectorErrors > 0)
            {
                var penalty = Math.Min(collectorErrors * 3, 15);
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
            
            if (root.TryGetProperty("metadata", out var metaElement))
            {
                try
                {
                    if (metaElement.TryGetProperty("version", out var v)) metadata.Version = v.GetString() ?? "unknown";
                    if (metaElement.TryGetProperty("runId", out var r)) metadata.RunId = r.GetString() ?? "";
                    if (metaElement.TryGetProperty("timestamp", out var t) && DateTime.TryParse(t.GetString(), out var dt)) metadata.Timestamp = dt;
                    if (metaElement.TryGetProperty("isAdmin", out var a)) metadata.IsAdmin = a.GetBoolean();
                    if (metaElement.TryGetProperty("redactLevel", out var rl)) metadata.RedactLevel = rl.GetString() ?? "standard";
                    if (metaElement.TryGetProperty("quickScan", out var q)) metadata.QuickScan = q.GetBoolean();
                    if (metaElement.TryGetProperty("monitorSeconds", out var m)) metadata.MonitorSeconds = m.GetInt32();
                    if (metaElement.TryGetProperty("durationSeconds", out var d)) metadata.DurationSeconds = d.GetDouble();
                    if (metaElement.TryGetProperty("partialFailure", out var p)) metadata.PartialFailure = p.GetBoolean();
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

            JsonElement scoreElement = default;
            var hasScoreV2 = root.TryGetProperty("scoreV2", out scoreElement);
            if (!hasScoreV2 && root.TryGetProperty("scan_powershell", out var scanPs) &&
                scanPs.TryGetProperty("scoreV2", out var psScore))
            {
                scoreElement = psScore;
                hasScoreV2 = true;
            }

            if (hasScoreV2)
            {
                try
                {
                    if (scoreElement.TryGetProperty("score", out var s)) scoreV2.Score = s.GetInt32();
                    if (scoreElement.TryGetProperty("baseScore", out var bs)) scoreV2.BaseScore = bs.GetInt32();
                    if (scoreElement.TryGetProperty("totalPenalty", out var tp)) scoreV2.TotalPenalty = tp.GetInt32();
                    if (scoreElement.TryGetProperty("grade", out var g)) scoreV2.Grade = g.GetString() ?? "N/A";
                    
                    // Breakdown
                    if (scoreElement.TryGetProperty("breakdown", out var bdElement))
                    {
                        var bd = new ScoreBreakdown();
                        if (bdElement.TryGetProperty("critical", out var c)) bd.Critical = c.GetInt32();
                        if (bdElement.TryGetProperty("collectorErrors", out var ce)) bd.CollectorErrors = ce.GetInt32();
                        if (bdElement.TryGetProperty("warnings", out var w)) bd.Warnings = w.GetInt32();
                        if (bdElement.TryGetProperty("timeouts", out var to)) bd.Timeouts = to.GetInt32();
                        if (bdElement.TryGetProperty("infoIssues", out var ii)) bd.InfoIssues = ii.GetInt32();
                        if (bdElement.TryGetProperty("excludedLimitations", out var el)) bd.ExcludedLimitations = el.GetInt32();
                        scoreV2.Breakdown = bd;
                    }
                    
                    // Top penalties
                    if (scoreElement.TryGetProperty("topPenalties", out var tpArray))
                    {
                        if (tpArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var penalty in tpArray.EnumerateArray())
                            {
                                var p = new PenaltyInfo();
                                if (penalty.TryGetProperty("type", out var pt)) p.Type = pt.GetString() ?? "";
                                if (penalty.TryGetProperty("source", out var ps)) p.Source = ps.GetString() ?? "";
                                if (penalty.TryGetProperty("penalty", out var pp)) p.Penalty = pp.GetInt32();
                                if (penalty.TryGetProperty("msg", out var pm)) p.Message = pm.GetString() ?? "";
                                scoreV2.TopPenalties.Add(p);
                            }
                        }
                        else if (tpArray.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var penaltyProp in tpArray.EnumerateObject())
                            {
                                if (penaltyProp.Value.ValueKind != JsonValueKind.Object) continue;
                                var penalty = penaltyProp.Value;
                                var p = new PenaltyInfo
                                {
                                    Type = penaltyProp.Name
                                };
                                if (penalty.TryGetProperty("type", out var pt)) p.Type = pt.GetString() ?? p.Type;
                                if (penalty.TryGetProperty("source", out var ps)) p.Source = ps.GetString() ?? "";
                                if (penalty.TryGetProperty("penalty", out var pp)) p.Penalty = pp.GetInt32();
                                if (penalty.TryGetProperty("msg", out var pm)) p.Message = pm.GetString() ?? "";
                                if (!string.IsNullOrEmpty(p.Type) || !string.IsNullOrEmpty(p.Source))
                                    scoreV2.TopPenalties.Add(p);
                            }
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
            // Fallback: calculer depuis summary ou sections
            var score = new ScoreV2Data { Score = 100, BaseScore = 100, Grade = "A" };
            
            if (root.TryGetProperty("summary", out var summary))
            {
                if (summary.TryGetProperty("score", out var s)) score.Score = s.GetInt32();
                if (summary.TryGetProperty("grade", out var g)) score.Grade = g.GetString() ?? "A";
                if (summary.TryGetProperty("criticalCount", out var cc)) score.Breakdown.Critical = cc.GetInt32();
                if (summary.TryGetProperty("warningCount", out var wc)) score.Breakdown.Warnings = wc.GetInt32();
            }
            
            score.TotalPenalty = 100 - score.Score;
            return score;
        }

        private static List<ScanErrorInfo> ExtractErrors(JsonElement root)
        {
            var errors = new List<ScanErrorInfo>();
            
            if (root.TryGetProperty("errors", out var errArray) && errArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var err in errArray.EnumerateArray())
                {
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
            
            // Parser les sections JSON (scan_powershell.sections ou sections directement)
            JsonElement sectionsElement = default;
            bool hasSections = false;
            
            // Try scan_powershell.sections first
            if (root.TryGetProperty("scan_powershell", out var psRoot) && 
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
                        if (sectionData.TryGetProperty("status", out var statusProp))
                            status = statusProp.GetString() ?? "OK";
                            
                        var data = sectionData.TryGetProperty("data", out var dataProp) ? dataProp : sectionData;
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
                
                // FIX: Pour le domaine Drivers, utiliser les données WMI en fallback si PS est vide
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
                            ["Pilotes essentiels"] = wmiDriverData.Count.ToString(),
                            ["Classes détectées"] = string.Join(", ", wmiDriverData.Select(d => d.cls).Distinct().Take(5))
                        };
                        section.DetailedExplanation = $"Les pilotes ont été détectés via WMI. {wmiDriverData.Count} pilotes essentiels trouvés.";
                        section.SectionRecommendations = new List<string> { "Mettez à jour les pilotes obsolètes" };
                        App.LogMessage($"[HealthReportBuilder] Drivers domain: WMI fallback utilisé, {wmiDriverData.Count} pilotes");
                    }
                }
                
                // Calculer le score de la section depuis les pénalités associées
                if (section.HasData || domain == HealthDomain.Performance || domain == HealthDomain.Security)
                {
                    section.Score = CalculateSectionScore(domain, scoreV2, domainData[domain]);
                    section.Severity = HealthReport.ScoreToSeverity(section.Score);
                    section.CollectionStatus = GetWorstStatus(domainData[domain]);
                    
                    // === NOUVEAU: Utiliser ComprehensiveEvidenceExtractor pour données complètes ===
                    // Extrait données de: PS sections, sensors C#, diagnostic_signals, network_diagnostics, etc.
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
                                // FIX: Use correct field name 'cpus' (PS script), with 'cpuList' fallback
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
                                        
                                        // Cœurs
                                        if (firstCpu.TryGetProperty("cores", out var cores))
                                            evidence["Cœurs"] = cores.ToString();
                                        
                                        // Threads
                                        if (firstCpu.TryGetProperty("threads", out var threads))
                                            evidence["Threads"] = threads.ToString();
                                        
                                        // Fréquence max
                                        if (firstCpu.TryGetProperty("maxClockSpeed", out var maxClock))
                                        {
                                            var mhz = maxClock.ValueKind == JsonValueKind.Number ? maxClock.GetDouble() : 0;
                                            if (mhz > 0)
                                                evidence["Fréquence max"] = $"{mhz:F0} MHz";
                                        }
                                        
                                        // Charge actuelle (currentLoad or load)
                                        if (firstCpu.TryGetProperty("currentLoad", out var load))
                                        {
                                            evidence["Charge actuelle"] = $"{load.GetDouble():F0} %";
                                        }
                                        else if (firstCpu.TryGetProperty("load", out var load2))
                                        {
                                            evidence["Charge actuelle"] = $"{load2.GetDouble():F0} %";
                                        }
                                    }
                                }
                                
                                // Nombre de CPU
                                if (data.TryGetProperty("cpuCount", out var cpuCount))
                                {
                                    var count = cpuCount.ValueKind == JsonValueKind.Number ? cpuCount.GetInt32() : 0;
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
                                        
                                        if (firstGpu.TryGetProperty("vramTotalMB", out var vramMB) && 
                                            vramMB.ValueKind == JsonValueKind.Number)
                                        {
                                            var mb = vramMB.GetDouble();
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
                                        if (!vramFound && firstGpu.TryGetProperty("adapterRAM_GB", out var adapterRam) &&
                                            adapterRam.ValueKind == JsonValueKind.Number)
                                        {
                                            var gb = adapterRam.GetDouble();
                                            if (gb > 0)
                                                evidence["VRAM totale"] = $"{gb:F1} GB";
                                        }
                                    }
                                }
                                
                                // Nombre de GPU
                                if (data.TryGetProperty("gpuCount", out var gpuCount))
                                {
                                    var count = gpuCount.ValueKind == JsonValueKind.Number ? gpuCount.GetInt32() : 0;
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
                                
                                if (data.TryGetProperty("totalGB", out var total) && total.ValueKind == JsonValueKind.Number)
                                {
                                    totalGB = total.GetDouble();
                                    if (totalGB > 0)
                                        evidence["Total"] = $"{totalGB:F1} GB";
                                }
                                
                                if (data.TryGetProperty("availableGB", out var avail) && avail.ValueKind == JsonValueKind.Number)
                                {
                                    availableGB = avail.GetDouble();
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
                                else if (data.TryGetProperty("moduleCount", out var modCount) && modCount.ValueKind == JsonValueKind.Number)
                                {
                                    var count = modCount.GetInt32();
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
                                        if (disk.TryGetProperty("sizeGB", out var size)) totalSpace += size.GetDouble();
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
                                        if (vol.TryGetProperty("sizeGB", out var s)) sizeGB = s.GetDouble();
                                        if (vol.TryGetProperty("freeSpaceGB", out var f)) freeGB = f.GetDouble();
                                        
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
                                        else if (activeAdapter.TryGetProperty("speedMbps", out var speedMbps) &&
                                            speedMbps.ValueKind == JsonValueKind.Number)
                                        {
                                            var mbps = speedMbps.GetDouble();
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
                            
                        // FIX: Ajouter extraction evidence pour le domaine Drivers
                        case HealthDomain.Drivers:
                            if (sectionName == "DevicesDrivers" && data.ValueKind == JsonValueKind.Object)
                            {
                                var problemCount = data.TryGetProperty("problemDeviceCount", out var pc) ? pc.GetInt32() : 
                                                   data.TryGetProperty("ProblemDeviceCount", out var pc2) ? pc2.GetInt32() : -1;
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
                                var deviceCount = data.TryGetProperty("deviceCount", out var dc) ? dc.GetInt32() :
                                                  data.TryGetProperty("DeviceCount", out var dc2) ? dc2.GetInt32() : -1;
                                if (deviceCount >= 0)
                                {
                                    evidence["Périph. audio"] = deviceCount.ToString();
                                }
                            }
                            else if (sectionName == "Printers" && data.ValueKind == JsonValueKind.Object)
                            {
                                var printerCount = data.TryGetProperty("printerCount", out var prc) ? prc.GetInt32() :
                                                   data.TryGetProperty("PrinterCount", out var prc2) ? prc2.GetInt32() : -1;
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
        /// FIX: Récupère les pilotes essentiels via WMI pour le domaine Drivers (fallback quand PS est vide)
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
                            result.Add((devClass, name, version ?? "—", date));
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
    }
}
