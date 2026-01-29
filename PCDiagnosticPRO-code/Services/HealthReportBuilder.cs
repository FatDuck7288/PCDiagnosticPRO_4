using System;
using System.Collections.Generic;
using System.Linq;
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
        /// </summary>
        private static readonly Dictionary<string, HealthDomain> SectionToDomain = new(StringComparer.OrdinalIgnoreCase)
        {
            // OS
            { "OS", HealthDomain.OS },
            { "MachineIdentity", HealthDomain.OS },
            { "WindowsUpdate", HealthDomain.OS },
            { "Security", HealthDomain.OS },
            { "SystemIntegrity", HealthDomain.OS },
            
            // CPU
            { "CPU", HealthDomain.CPU },
            { "Temperatures", HealthDomain.CPU },
            { "PerformanceCounters", HealthDomain.CPU },
            
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
            { "Processes", HealthDomain.SystemStability },
            
            // Drivers
            { "DevicesDrivers", HealthDomain.Drivers },
            { "Audio", HealthDomain.Drivers },
            { "Printers", HealthDomain.Drivers }
        };

        /// <summary>
        /// Icônes par domaine
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
            { HealthDomain.Drivers, "🔧" }
        };

        /// <summary>
        /// Noms affichés par domaine
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
            { HealthDomain.Drivers, "Pilotes" }
        };

        /// <summary>
        /// Construit un HealthReport depuis le JSON brut du PowerShell (sans capteurs)
        /// </summary>
        public static HealthReport Build(string jsonContent)
        {
            return Build(jsonContent, null);
        }

        /// <summary>
        /// Construit un HealthReport depuis le JSON brut du PowerShell AVEC données capteurs hardware
        /// </summary>
        public static HealthReport Build(string jsonContent, HardwareSensorsResult? sensors)
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
                
                // 3. Extraire erreurs
                report.Errors = ExtractErrors(root);
                
                // 4. Extraire missingData
                report.MissingData = ExtractMissingData(root);
                
                // 5. Construire les sections par domaine
                report.Sections = BuildHealthSections(root, report.ScoreV2);
                
                // 6. INJECTER LES DONNÉES CAPTEURS HARDWARE dans EvidenceData
                // C'est ici que les températures/charges réelles sont connectées au scoring
                if (sensors != null)
                {
                    InjectHardwareSensors(report, sensors);
                }
                
                // 7. Sauvegarder le score PS original avant GradeEngine
                var psScore = report.ScoreV2.Score;
                var psGrade = report.ScoreV2.Grade;
                
                // 8. APPLIQUER LE GRADE ENGINE APPLICATION (remplace le score PS)
                // Le GradeEngine est la SOURCE DE VÉRITÉ pour les grades UI
                // Il peut maintenant utiliser les données capteurs via EvidenceData
                GradeEngine.ApplyGrades(report);
                
                // 9. Documenter la divergence PS vs GradeEngine
                report.Divergence = new ScoreDivergence
                {
                    PowerShellScore = psScore,
                    PowerShellGrade = psGrade,
                    GradeEngineScore = report.GlobalScore,
                    GradeEngineGrade = report.Grade,
                    SourceOfTruth = "GradeEngine",
                    Explanation = report.GlobalScore != psScore 
                        ? $"Score recalculé avec capteurs hardware (PS:{psScore} → UI:{report.GlobalScore})"
                        : "Scores cohérents entre PS et GradeEngine"
                };
                
                if (!report.Divergence.IsCoherent)
                {
                    App.LogMessage($"[Divergence] Score PS={psScore} vs GradeEngine={report.GlobalScore} (delta={report.Divergence.Delta})");
                }
                
                // 10. Générer les recommandations
                report.Recommendations = GenerateRecommendations(report);
                
                // 9. Calculer le modèle de confiance
                report.ConfidenceModel = BuildConfidenceModel(report, sensors);
                
                App.LogMessage($"[HealthReportBuilder] Rapport construit: Score={report.GlobalScore}, Grade={report.Grade}, " +
                    $"Sections={report.Sections.Count}, SensorsCoverage={report.ConfidenceModel.SensorsCoverage:P0}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[HealthReportBuilder] ERREUR parsing JSON: {ex.Message}");
                report.GlobalScore = 0;
                report.GlobalSeverity = HealthSeverity.Unknown;
                report.GlobalMessage = "Impossible d'analyser les résultats du scan.";
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
        /// Construit le modèle de confiance (coverage + cohérence).
        /// ConfidenceScore pénalise l'ABSENCE de données, pas les anomalies (c'est HealthScore).
        /// </summary>
        private static ConfidenceModel BuildConfidenceModel(HealthReport report, HardwareSensorsResult? sensors)
        {
            var model = new ConfidenceModel();
            
            // 1. Coverage des sections PS
            int expectedSections = 8; // 8 domaines
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
            
            // Erreurs de collecteurs (WMI, SMART, etc.)
            if (report.ScoreV2.Breakdown.CollectorErrors > 0)
            {
                var penalty = Math.Min(report.ScoreV2.Breakdown.CollectorErrors * 3, 15);
                model.ConfidenceScore -= penalty;
                model.Warnings.Add($"Erreurs collecteurs: {report.ScoreV2.Breakdown.CollectorErrors} (pénalité -{penalty})");
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
            
            if (root.TryGetProperty("scoreV2", out var scoreElement))
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
                    if (scoreElement.TryGetProperty("topPenalties", out var tpArray) && tpArray.ValueKind == JsonValueKind.Array)
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

        private static List<HealthSection> BuildHealthSections(JsonElement root, ScoreV2Data scoreV2)
        {
            var sections = new List<HealthSection>();
            var domainData = new Dictionary<HealthDomain, List<(string sectionName, JsonElement data, string status)>>();
            
            // Initialiser tous les domaines
            foreach (HealthDomain domain in Enum.GetValues<HealthDomain>())
            {
                domainData[domain] = new List<(string, JsonElement, string)>();
            }
            
            // Parser les sections JSON
            if (root.TryGetProperty("sections", out var sectionsElement) && sectionsElement.ValueKind == JsonValueKind.Object)
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
                
                if (section.HasData)
                {
                    // Calculer le score de la section depuis les pénalités associées
                    section.Score = CalculateSectionScore(domain, scoreV2, domainData[domain]);
                    section.Severity = HealthReport.ScoreToSeverity(section.Score);
                    section.CollectionStatus = GetWorstStatus(domainData[domain]);
                    
                    // Extraire les données clés pour l'affichage
                    section.EvidenceData = ExtractEvidenceData(domain, domainData[domain]);
                    
                    // Générer le message de statut
                    section.StatusMessage = GenerateSectionMessage(section);
                    
                    // Extraire les findings
                    section.Findings = ExtractFindings(domain, scoreV2);
                    
                    // Générer l'explication détaillée
                    section.DetailedExplanation = GenerateDetailedExplanation(section);
                    
                    // Générer les recommandations
                    section.SectionRecommendations = GenerateSectionRecommendations(section);
                }
                else
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
                                if (data.TryGetProperty("caption", out var caption)) evidence["Version"] = caption.GetString() ?? "";
                                if (data.TryGetProperty("architecture", out var arch)) evidence["Architecture"] = arch.GetString() ?? "";
                            }
                            break;
                            
                        case HealthDomain.CPU:
                            if (sectionName == "CPU" && data.ValueKind == JsonValueKind.Object)
                            {
                                if (data.TryGetProperty("cpuList", out var cpuList) && cpuList.ValueKind == JsonValueKind.Array)
                                {
                                    var firstCpu = cpuList.EnumerateArray().FirstOrDefault();
                                    if (firstCpu.TryGetProperty("name", out var name)) evidence["Modèle"] = name.GetString() ?? "";
                                    if (firstCpu.TryGetProperty("cores", out var cores)) evidence["Cœurs"] = cores.ToString();
                                    // === P0-B: CPU LOAD depuis PowerShell ===
                                    if (firstCpu.TryGetProperty("currentLoad", out var load))
                                    {
                                        evidence["Charge CPU (PS)"] = $"{load.GetDouble():F0}%";
                                    }
                                    else if (firstCpu.TryGetProperty("load", out var load2))
                                    {
                                        evidence["Charge CPU (PS)"] = $"{load2.GetDouble():F0}%";
                                    }
                                }
                            }
                            break;
                            
                        case HealthDomain.GPU:
                            if (sectionName == "GPU" && data.ValueKind == JsonValueKind.Object)
                            {
                                if (data.TryGetProperty("gpuList", out var gpuList) && gpuList.ValueKind == JsonValueKind.Array)
                                {
                                    var firstGpu = gpuList.EnumerateArray().FirstOrDefault();
                                    if (firstGpu.TryGetProperty("name", out var name)) evidence["Modèle"] = name.GetString() ?? "";
                                    if (firstGpu.TryGetProperty("adapterRAM_GB", out var ram)) evidence["VRAM"] = $"{ram.GetDouble():F1} GB";
                                }
                            }
                            break;
                            
                        case HealthDomain.RAM:
                            if (sectionName == "Memory" && data.ValueKind == JsonValueKind.Object)
                            {
                                if (data.TryGetProperty("totalGB", out var total)) evidence["Total"] = $"{total.GetDouble():F1} GB";
                                if (data.TryGetProperty("availableGB", out var avail)) evidence["Disponible"] = $"{avail.GetDouble():F1} GB";
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
                                    if (activeAdapter.TryGetProperty("name", out var name)) evidence["Adaptateur"] = name.GetString() ?? "";
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
    }
}
