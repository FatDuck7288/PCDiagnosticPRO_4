using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Diagnostics.Rules
{
    /// <summary>
    /// Moteur de score basé sur règles (scoring par règles, complète UDIS).
    /// Calcule ScoreSanté (état machine) et ScoreConfiance (qualité collecte) séparément.
    /// </summary>
    public static class HealthRulesEngine
    {
        #region Domain Weights (dérivés automatiquement par priorité fonctionnelle)

        /// <summary>
        /// Priorité fonctionnelle imposée:
        /// CPU > GPU > RAM > Storage système > OS > Security/Malware > Stability > Drivers > Network > Devices > Updates
        /// </summary>
        private static readonly string[] PriorityOrder = new[]
        {
            "CPU", "GPU", "RAM", "StorageSystem", "OS", "Security", 
            "Stability", "Drivers", "Network", "Devices", "Updates"
        };

        /// <summary>Poids bruts décroissants puis normalisés à 100</summary>
        public static Dictionary<string, double> ComputeNormalizedWeights()
        {
            var rawWeights = new Dictionary<string, int>();
            int baseWeight = 20; // Poids de départ pour le plus important
            
            for (int i = 0; i < PriorityOrder.Length; i++)
            {
                // Décroissance: 20, 18, 16, 14, 12, 10, 8, 6, 5, 4, 3
                rawWeights[PriorityOrder[i]] = Math.Max(3, baseWeight - (i * 2));
            }
            
            double totalRaw = rawWeights.Values.Sum();
            var normalized = new Dictionary<string, double>();
            
            foreach (var kvp in rawWeights)
            {
                normalized[kvp.Key] = (kvp.Value / totalRaw) * 100.0;
            }
            
            return normalized;
        }

        #endregion

        #region Temperature Thresholds (seuils officiels)

        public static class CpuTempZones
        {
            public const double TresFroid = 30;
            public const double OptimalReposMax = 50;
            public const double ChargeNormaleMax = 70;
            public const double SurveillanceMax = 85;
            public const double ThrottlingMax = 90;
            // > 90 = Danger
        }

        public static class GpuTempZones
        {
            public const double TresFroid = 35;
            public const double OptimalReposMax = 60;
            public const double ChargeNormaleMax = 75;
            public const double SurveillanceMax = 85;
            public const double ThrottlingMax = 95;
            // > 95 = Danger
        }

        public static class HddTempZones
        {
            public const double TresFroid = 5;
            public const double IdealMax = 30;
            public const double NormalMax = 45;
            public const double RisqueMax = 55;
            // > 55 = Danger
        }

        public static class SsdNvmeTempZones
        {
            public const double TresFroid = 0;
            public const double OptimalMax = 30;
            public const double IdealLongeviteMax = 50;
            public const double AcceptableMax = 60;
            public const double ThrottlingMax = 70;
            // > 70 = Danger
        }

        public static class RamUsageZones
        {
            public const double UltraFluideMax = 30;
            public const double OptimalMax = 50;
            public const double NormalMultitacheMax = 70;
            public const double ChargeEleveeMax = 85;
            public const double SurchargeSwapMax = 95;
            // > 95 = Saturation critique
        }

        public static class StorageZones
        {
            public const double CriticalPercentFree = 10;
            public const double MinimumPercentFree = 15;
            public const double IdealPercentFree = 20;
            public const double MinimumGBFree = 15;
            public const double RecommendedGBFree = 30;
        }

        #endregion

        #region Main Compute Method

        /// <summary>
        /// Calcule le rapport de score complet depuis les données brutes.
        /// </summary>
        public static RulesScoreReport ComputeScore(
            JsonElement? psData,
            HardwareSensorsResult? sensors,
            List<string> collectorErrors)
        {
            var report = new RulesScoreReport();
            report.ComputedAt = DateTime.Now;
            report.Weights = ComputeNormalizedWeights();
            
            // === SECTIONS ===
            report.Sections = new List<SectionScore>();
            
            // CPU Section
            var cpuSection = EvaluateCpuSection(psData, sensors);
            report.Sections.Add(cpuSection);
            
            // GPU Section
            var gpuSection = EvaluateGpuSection(psData, sensors);
            report.Sections.Add(gpuSection);
            
            // RAM Section
            var ramSection = EvaluateRamSection(psData);
            report.Sections.Add(ramSection);
            
            // Storage System Section
            var storageSection = EvaluateStorageSection(psData, sensors);
            report.Sections.Add(storageSection);
            
            // OS Section
            var osSection = EvaluateOsSection(psData);
            report.Sections.Add(osSection);
            
            // Security Section
            var securitySection = EvaluateSecuritySection(psData);
            report.Sections.Add(securitySection);
            
            // Stability Section
            var stabilitySection = EvaluateStabilitySection(psData);
            report.Sections.Add(stabilitySection);
            
            // Drivers Section
            var driversSection = EvaluateDriversSection(psData);
            report.Sections.Add(driversSection);
            
            // Network Section
            var networkSection = EvaluateNetworkSection(psData);
            report.Sections.Add(networkSection);
            
            // Devices Section
            var devicesSection = EvaluateDevicesSection(psData);
            report.Sections.Add(devicesSection);
            
            // Updates Section
            var updatesSection = EvaluateUpdatesSection(psData);
            report.Sections.Add(updatesSection);
            
            // === CALCUL SCORE SANTÉ (moyenne pondérée) ===
            double weightedSum = 0;
            double totalWeight = 0;
            
            foreach (var section in report.Sections)
            {
                if (section.Status != SectionStatus.Unknown && report.Weights.TryGetValue(section.SectionName, out var weight))
                {
                    weightedSum += section.Score * weight;
                    totalWeight += weight;
                }
            }
            
            report.GlobalHealthScore = totalWeight > 0 ? (int)Math.Round(weightedSum / totalWeight) : 0;
            
            // === PÉNALITÉS CRITIQUES (appliquées APRÈS moyenne pondérée) ===
            var criticalPenalties = ApplyCriticalPenalties(report, psData, sensors);
            report.CriticalPenalties = criticalPenalties;
            
            foreach (var penalty in criticalPenalties)
            {
                report.GlobalHealthScore -= penalty.PenaltyPoints;
            }
            
            // === HARD CAPS ===
            ApplyHardCaps(report, psData);
            
            // Clamp 0-100
            report.GlobalHealthScore = Math.Max(0, Math.Min(100, report.GlobalHealthScore));
            
            // === GRADE ===
            report.GlobalHealthGrade = ScoreToGrade(report.GlobalHealthScore);
            report.GlobalHealthLabel = ScoreToLabel(report.GlobalHealthScore);
            
            // === CONFIDENCE MODEL ===
            report.Confidence = ComputeConfidence(psData, sensors, collectorErrors);
            
            // === COLLECTION STATUS ===
            report.CollectionComplete = EvaluateCollectionComplete(psData, sensors, collectorErrors);
            report.CollectionFailureReason = report.CollectionComplete ? null : 
                GetCollectionFailureReason(psData, sensors, collectorErrors);
            
            return report;
        }

        #endregion

        #region Section Evaluators

        private static SectionScore EvaluateCpuSection(JsonElement? psData, HardwareSensorsResult? sensors)
        {
            var section = new SectionScore { SectionName = "CPU", Weight = 0 };
            section.RawInputs = new Dictionary<string, string>();
            section.AppliedRules = new List<AppliedRule>();
            
            int score = 100;
            
            // CPU Temperature (from C# sensors)
            if (sensors?.Cpu?.CpuTempC?.Available == true)
            {
                var temp = sensors.Cpu.CpuTempC.Value;
                section.RawInputs["CpuTempC"] = $"{temp:F1}°C (source: {sensors.Cpu.CpuTempSource})";
                
                if (temp > CpuTempZones.ThrottlingMax)
                {
                    score -= 40;
                    section.AppliedRules.Add(new AppliedRule("CPU_TEMP_DANGER", $"Température CPU dangereuse ({temp:F0}°C > 90°C)", -40));
                    section.Status = SectionStatus.Critical;
                }
                else if (temp > CpuTempZones.SurveillanceMax)
                {
                    score -= 25;
                    section.AppliedRules.Add(new AppliedRule("CPU_TEMP_THROTTLING", $"Température CPU throttling ({temp:F0}°C > 85°C)", -25));
                    section.Status = SectionStatus.Degraded;
                }
                else if (temp > CpuTempZones.ChargeNormaleMax)
                {
                    score -= 10;
                    section.AppliedRules.Add(new AppliedRule("CPU_TEMP_SURVEILLANCE", $"Température CPU à surveiller ({temp:F0}°C > 70°C)", -10));
                    section.Status = SectionStatus.Warning;
                }
                else
                {
                    section.AppliedRules.Add(new AppliedRule("CPU_TEMP_OK", $"Température CPU normale ({temp:F0}°C)", 0));
                }
            }
            else
            {
                section.RawInputs["CpuTempC"] = sensors?.Cpu?.CpuTempC?.Reason ?? "Non collectée";
                // Note: données manquantes n'impactent pas le score santé, seulement la confiance
            }
            
            // CPU Load (from PowerShell)
            var cpuLoad = ExtractCpuLoadFromPs(psData);
            if (cpuLoad.HasValue)
            {
                section.RawInputs["CpuLoadPercent"] = $"{cpuLoad.Value:F0}% (source: PowerShell)";
                
                // CPU load élevé en permanence peut indiquer un problème
                if (cpuLoad.Value > 95)
                {
                    score -= 15;
                    section.AppliedRules.Add(new AppliedRule("CPU_LOAD_CRITICAL", $"Charge CPU critique ({cpuLoad.Value:F0}% > 95%)", -15));
                }
                else if (cpuLoad.Value > 80)
                {
                    score -= 5;
                    section.AppliedRules.Add(new AppliedRule("CPU_LOAD_HIGH", $"Charge CPU élevée ({cpuLoad.Value:F0}% > 80%)", -5));
                }
            }
            else
            {
                section.RawInputs["CpuLoadPercent"] = "Non disponible";
            }
            
            section.Score = Math.Max(0, score);
            if (section.Status == SectionStatus.Unknown)
                section.Status = ScoreToStatus(section.Score);
            
            section.RecommendedActions = GenerateCpuRecommendations(section);
            
            return section;
        }

        private static SectionScore EvaluateGpuSection(JsonElement? psData, HardwareSensorsResult? sensors)
        {
            var section = new SectionScore { SectionName = "GPU", Weight = 0 };
            section.RawInputs = new Dictionary<string, string>();
            section.AppliedRules = new List<AppliedRule>();
            
            int score = 100;
            bool hasGpu = sensors?.Gpu?.Name?.Available == true;
            
            if (!hasGpu)
            {
                section.RawInputs["GPU"] = "Pas de GPU dédié détecté";
                section.Status = SectionStatus.OK;
                section.Score = 100;
                section.AppliedRules.Add(new AppliedRule("GPU_INTEGRATED", "GPU intégré ou non détecté - section ignorée", 0));
                return section;
            }
            
            section.RawInputs["GPU"] = sensors.Gpu.Name.Value ?? "Inconnu";
            
            // GPU Temperature
            if (sensors.Gpu.GpuTempC.Available)
            {
                var temp = sensors.Gpu.GpuTempC.Value;
                section.RawInputs["GpuTempC"] = $"{temp:F1}°C";
                
                if (temp > GpuTempZones.ThrottlingMax)
                {
                    score -= 40;
                    section.AppliedRules.Add(new AppliedRule("GPU_TEMP_DANGER", $"Température GPU dangereuse ({temp:F0}°C > 95°C)", -40));
                    section.Status = SectionStatus.Critical;
                }
                else if (temp > GpuTempZones.SurveillanceMax)
                {
                    score -= 20;
                    section.AppliedRules.Add(new AppliedRule("GPU_TEMP_HIGH", $"Température GPU élevée ({temp:F0}°C > 85°C)", -20));
                    section.Status = SectionStatus.Warning;
                }
                else if (temp > GpuTempZones.ChargeNormaleMax)
                {
                    score -= 5;
                    section.AppliedRules.Add(new AppliedRule("GPU_TEMP_SURVEILLANCE", $"Température GPU à surveiller ({temp:F0}°C > 75°C)", -5));
                }
            }
            else
            {
                section.RawInputs["GpuTempC"] = sensors.Gpu.GpuTempC.Reason ?? "Non collectée";
            }
            
            // GPU Load
            if (sensors.Gpu.GpuLoadPercent.Available)
            {
                var load = sensors.Gpu.GpuLoadPercent.Value;
                section.RawInputs["GpuLoadPercent"] = $"{load:F0}%";
                
                if (load > 95)
                {
                    score -= 10;
                    section.AppliedRules.Add(new AppliedRule("GPU_LOAD_SUSTAINED", $"Charge GPU très élevée ({load:F0}%)", -10));
                }
            }
            else
            {
                section.RawInputs["GpuLoadPercent"] = sensors.Gpu.GpuLoadPercent.Reason ?? "Non collectée";
            }
            
            // VRAM
            if (sensors.Gpu.VramTotalMB.Available && sensors.Gpu.VramUsedMB.Available)
            {
                var total = sensors.Gpu.VramTotalMB.Value;
                var used = sensors.Gpu.VramUsedMB.Value;
                var pct = total > 0 ? (used / total * 100) : 0;
                section.RawInputs["VramTotalMB"] = $"{total:F0} MB";
                section.RawInputs["VramUsedMB"] = $"{used:F0} MB ({pct:F0}%)";
                
                if (pct > 95)
                {
                    score -= 15;
                    section.AppliedRules.Add(new AppliedRule("VRAM_SATURATED", $"VRAM saturée ({pct:F0}%)", -15));
                }
                else if (pct > 85)
                {
                    score -= 5;
                    section.AppliedRules.Add(new AppliedRule("VRAM_HIGH", $"VRAM très utilisée ({pct:F0}%)", -5));
                }
            }
            
            section.Score = Math.Max(0, score);
            if (section.Status == SectionStatus.Unknown)
                section.Status = ScoreToStatus(section.Score);
            
            section.RecommendedActions = GenerateGpuRecommendations(section);
            
            return section;
        }

        private static SectionScore EvaluateRamSection(JsonElement? psData)
        {
            var section = new SectionScore { SectionName = "RAM", Weight = 0 };
            section.RawInputs = new Dictionary<string, string>();
            section.AppliedRules = new List<AppliedRule>();
            
            int score = 100;
            
            var ramData = ExtractRamDataFromPs(psData);
            if (ramData.HasValue)
            {
                section.RawInputs["TotalGB"] = $"{ramData.Value.totalGB:F1} GB";
                section.RawInputs["AvailableGB"] = $"{ramData.Value.availableGB:F1} GB";
                section.RawInputs["UsagePercent"] = $"{ramData.Value.usagePercent:F1}%";
                
                if (ramData.Value.usagePercent > RamUsageZones.SurchargeSwapMax)
                {
                    score -= 40;
                    section.AppliedRules.Add(new AppliedRule("RAM_SATURATION", $"RAM saturée ({ramData.Value.usagePercent:F0}% > 95%)", -40));
                    section.Status = SectionStatus.Critical;
                }
                else if (ramData.Value.usagePercent > RamUsageZones.ChargeEleveeMax)
                {
                    score -= 20;
                    section.AppliedRules.Add(new AppliedRule("RAM_SWAP_ACTIF", $"RAM en surcharge, swap actif ({ramData.Value.usagePercent:F0}% > 85%)", -20));
                    section.Status = SectionStatus.Degraded;
                }
                else if (ramData.Value.usagePercent > RamUsageZones.NormalMultitacheMax)
                {
                    score -= 5;
                    section.AppliedRules.Add(new AppliedRule("RAM_CHARGE_ELEVEE", $"Charge RAM élevée ({ramData.Value.usagePercent:F0}% > 70%)", -5));
                    section.Status = SectionStatus.Warning;
                }
                else
                {
                    section.AppliedRules.Add(new AppliedRule("RAM_OK", $"Utilisation RAM normale ({ramData.Value.usagePercent:F0}%)", 0));
                }
            }
            else
            {
                section.RawInputs["RAM"] = "Données non disponibles";
            }
            
            section.Score = Math.Max(0, score);
            if (section.Status == SectionStatus.Unknown)
                section.Status = ScoreToStatus(section.Score);
            
            return section;
        }

        private static SectionScore EvaluateStorageSection(JsonElement? psData, HardwareSensorsResult? sensors)
        {
            var section = new SectionScore { SectionName = "StorageSystem", Weight = 0 };
            section.RawInputs = new Dictionary<string, string>();
            section.AppliedRules = new List<AppliedRule>();
            
            int score = 100;
            
            // Volume C: (système)
            var volumeC = ExtractVolumeCFromPs(psData);
            if (volumeC.HasValue)
            {
                section.RawInputs["C_TotalGB"] = $"{volumeC.Value.totalGB:F1} GB";
                section.RawInputs["C_FreeGB"] = $"{volumeC.Value.freeGB:F1} GB";
                section.RawInputs["C_FreePercent"] = $"{volumeC.Value.freePercent:F1}%";
                
                // Priorité: % libre > GB libres
                if (volumeC.Value.freePercent < StorageZones.CriticalPercentFree)
                {
                    score -= 40;
                    section.AppliedRules.Add(new AppliedRule("STORAGE_C_CRITICAL", $"Espace C: critique ({volumeC.Value.freePercent:F0}% < 10%)", -40));
                    section.Status = SectionStatus.Critical;
                }
                else if (volumeC.Value.freePercent < StorageZones.MinimumPercentFree || volumeC.Value.freeGB < StorageZones.MinimumGBFree)
                {
                    score -= 25;
                    section.AppliedRules.Add(new AppliedRule("STORAGE_C_LOW", $"Espace C: faible ({volumeC.Value.freeGB:F0} GB libre)", -25));
                    section.Status = SectionStatus.Degraded;
                }
                else if (volumeC.Value.freePercent < StorageZones.IdealPercentFree)
                {
                    score -= 10;
                    section.AppliedRules.Add(new AppliedRule("STORAGE_C_WARNING", $"Espace C: à surveiller ({volumeC.Value.freePercent:F0}% < 20%)", -10));
                    section.Status = SectionStatus.Warning;
                }
                else
                {
                    section.AppliedRules.Add(new AppliedRule("STORAGE_C_OK", $"Espace C: OK ({volumeC.Value.freeGB:F0} GB libre, {volumeC.Value.freePercent:F0}%)", 0));
                }
            }
            
            // Températures disques (from C# sensors)
            if (sensors?.Disks != null && sensors.Disks.Any())
            {
                foreach (var disk in sensors.Disks)
                {
                    if (disk.TempC.Available)
                    {
                        var temp = disk.TempC.Value;
                        var name = disk.Name.Available ? disk.Name.Value : "Disque";
                        section.RawInputs[$"Temp_{name}"] = $"{temp:F0}°C";
                        
                        // Utiliser seuils SSD/NVMe (plus stricts)
                        if (temp > SsdNvmeTempZones.ThrottlingMax)
                        {
                            score -= 25;
                            section.AppliedRules.Add(new AppliedRule("DISK_TEMP_DANGER", $"{name}: temp dangereuse ({temp:F0}°C > 70°C)", -25));
                        }
                        else if (temp > SsdNvmeTempZones.AcceptableMax)
                        {
                            score -= 10;
                            section.AppliedRules.Add(new AppliedRule("DISK_TEMP_HIGH", $"{name}: temp élevée ({temp:F0}°C > 60°C)", -10));
                        }
                    }
                }
            }
            
            // SMART (from PS)
            var smartIssues = ExtractSmartIssuesFromPs(psData);
            if (smartIssues.pendingSectors > 5)
            {
                score -= 50;
                section.AppliedRules.Add(new AppliedRule("SMART_PENDING_CRITICAL", $"SMART: {smartIssues.pendingSectors} pending sectors (critique)", -50));
                section.Status = SectionStatus.Critical;
            }
            else if (smartIssues.pendingSectors > 0)
            {
                score -= 20;
                section.AppliedRules.Add(new AppliedRule("SMART_PENDING", $"SMART: {smartIssues.pendingSectors} pending sector(s)", -20));
            }
            
            if (smartIssues.reallocatedSectors > 10)
            {
                score -= 70;
                section.AppliedRules.Add(new AppliedRule("SMART_REALLOCATED_CRITICAL", $"SMART: {smartIssues.reallocatedSectors} reallocated sectors (critique)", -70));
                section.Status = SectionStatus.Critical;
            }
            else if (smartIssues.reallocatedSectors > 0)
            {
                score -= 30;
                section.AppliedRules.Add(new AppliedRule("SMART_REALLOCATED", $"SMART: {smartIssues.reallocatedSectors} reallocated sector(s)", -30));
            }
            
            section.Score = Math.Max(0, score);
            if (section.Status == SectionStatus.Unknown)
                section.Status = ScoreToStatus(section.Score);
            
            return section;
        }

        private static SectionScore EvaluateOsSection(JsonElement? psData)
        {
            var section = new SectionScore { SectionName = "OS", Weight = 0 };
            section.RawInputs = new Dictionary<string, string>();
            section.AppliedRules = new List<AppliedRule>();
            section.Score = 100;
            section.Status = SectionStatus.OK;
            
            // OS info (pas de pénalité directe, info seulement)
            var osInfo = ExtractOsInfoFromPs(psData);
            if (osInfo != null)
            {
                section.RawInputs["OS"] = osInfo;
            }
            
            return section;
        }

        private static SectionScore EvaluateSecuritySection(JsonElement? psData)
        {
            var section = new SectionScore { SectionName = "Security", Weight = 0 };
            section.RawInputs = new Dictionary<string, string>();
            section.AppliedRules = new List<AppliedRule>();
            
            int score = 100;
            
            // Defender / AV status
            var defenderOff = IsDefenderOff(psData);
            var hasThirdPartyAv = HasThirdPartyAntivirus(psData);
            
            if (defenderOff && !hasThirdPartyAv)
            {
                score -= 50;
                section.AppliedRules.Add(new AppliedRule("DEFENDER_OFF_NO_AV", "Defender désactivé sans AV tiers", -50));
                section.Status = SectionStatus.Critical;
            }
            
            // Malware / PUA detection
            var malwareInfo = ExtractMalwareFromPs(psData);
            if (malwareInfo.realThreats > 0)
            {
                score -= 50;
                section.AppliedRules.Add(new AppliedRule("MALWARE_DETECTED", $"{malwareInfo.realThreats} menace(s) réelle(s) détectée(s)", -50));
                section.Status = SectionStatus.Critical;
            }
            
            if (malwareInfo.vulnerableDrivers > 0)
            {
                score -= 25;
                section.AppliedRules.Add(new AppliedRule("VULNERABLE_DRIVER", $"{malwareInfo.vulnerableDrivers} driver(s) vulnérable(s)", -25));
            }
            
            if (malwareInfo.pua > 0)
            {
                score -= malwareInfo.pua * 5; // -5 par PUA
                section.AppliedRules.Add(new AppliedRule("PUA_DETECTED", $"{malwareInfo.pua} PUA détecté(s)", -malwareInfo.pua * 5));
            }
            
            section.Score = Math.Max(0, score);
            if (section.Status == SectionStatus.Unknown)
                section.Status = ScoreToStatus(section.Score);
            
            return section;
        }

        private static SectionScore EvaluateStabilitySection(JsonElement? psData)
        {
            var section = new SectionScore { SectionName = "Stability", Weight = 0 };
            section.RawInputs = new Dictionary<string, string>();
            section.AppliedRules = new List<AppliedRule>();
            
            int score = 100;
            
            // Event logs (7 jours)
            var eventLogs = ExtractEventLogsFromPs(psData);
            section.RawInputs["SystemErrors7d"] = eventLogs.systemErrors.ToString();
            section.RawInputs["AppErrors7d"] = eventLogs.appErrors.ToString();
            
            var totalErrors = eventLogs.systemErrors + eventLogs.appErrors;
            if (totalErrors > 50)
            {
                score -= 30;
                section.AppliedRules.Add(new AppliedRule("EVENTS_CRITICAL", $"{totalErrors} erreurs (7j) - critique", -30));
                section.Status = SectionStatus.Critical;
            }
            else if (totalErrors > 20)
            {
                score -= 15;
                section.AppliedRules.Add(new AppliedRule("EVENTS_DEGRADED", $"{totalErrors} erreurs (7j) - dégradé", -15));
                section.Status = SectionStatus.Degraded;
            }
            else if (totalErrors > 5)
            {
                score -= 5;
                section.AppliedRules.Add(new AppliedRule("EVENTS_WARNING", $"{totalErrors} erreurs (7j)", -5));
            }
            
            // BSOD (30 jours)
            var bsodCount = ExtractBsodCountFromPs(psData);
            section.RawInputs["BSOD30d"] = bsodCount.ToString();
            
            if (bsodCount > 0)
            {
                score -= 40;
                section.AppliedRules.Add(new AppliedRule("BSOD_DETECTED", $"{bsodCount} BSOD en 30 jours - CRITIQUE", -40));
                section.Status = SectionStatus.Critical;
            }
            
            // Reliability crashes
            var crashes = ExtractCrashCountFromPs(psData);
            section.RawInputs["Crashes30d"] = crashes.ToString();
            
            if (crashes > 10)
            {
                score -= 25;
                section.AppliedRules.Add(new AppliedRule("CRASHES_CRITICAL", $"{crashes} crashs (30j) - critique", -25));
            }
            else if (crashes > 5)
            {
                score -= 15;
                section.AppliedRules.Add(new AppliedRule("CRASHES_DEGRADED", $"{crashes} crashs (30j)", -15));
            }
            else if (crashes > 2)
            {
                score -= 5;
                section.AppliedRules.Add(new AppliedRule("CRASHES_WARNING", $"{crashes} crashs (30j)", -5));
            }
            
            section.Score = Math.Max(0, score);
            if (section.Status == SectionStatus.Unknown)
                section.Status = ScoreToStatus(section.Score);
            
            return section;
        }

        private static SectionScore EvaluateDriversSection(JsonElement? psData)
        {
            var section = new SectionScore { SectionName = "Drivers", Weight = 0 };
            section.RawInputs = new Dictionary<string, string>();
            section.AppliedRules = new List<AppliedRule>();
            section.Score = 100;
            section.Status = SectionStatus.OK;
            
            if (psData.HasValue)
            {
                try
                {
                    if (psData.Value.TryGetProperty("sections", out var sections) &&
                        sections.TryGetProperty("DevicesDrivers", out var dd) &&
                        dd.TryGetProperty("data", out var data))
                    {
                        // Compter les périphériques en erreur
                        int errorDevices = 0;
                        if (data.TryGetProperty("errorDevicesCount", out var edc) && edc.ValueKind == JsonValueKind.Number)
                            errorDevices = edc.GetInt32();
                        else if (data.TryGetProperty("ErrorDevicesCount", out edc) && edc.ValueKind == JsonValueKind.Number)
                            errorDevices = edc.GetInt32();
                        
                        section.RawInputs["errorDevicesCount"] = errorDevices.ToString();
                        
                        if (errorDevices > 0)
                        {
                            int penalty = Math.Min(errorDevices * 5, 25);
                            section.Score -= penalty;
                            section.Status = errorDevices >= 3 ? SectionStatus.Degraded : SectionStatus.Warning;
                            section.AppliedRules.Add(new AppliedRule(
                                "DriverErrors",
                                $"{errorDevices} périphérique(s) en erreur",
                                -penalty));
                        }
                    }
                }
                catch { /* Pas de données drivers exploitables */ }
            }
            
            return section;
        }

        private static SectionScore EvaluateNetworkSection(JsonElement? psData)
        {
            var section = new SectionScore { SectionName = "Network", Weight = 0 };
            section.RawInputs = new Dictionary<string, string>();
            section.AppliedRules = new List<AppliedRule>();
            section.Score = 100;
            section.Status = SectionStatus.OK;
            
            // Network tests disabled is informational only
            
            return section;
        }

        private static SectionScore EvaluateDevicesSection(JsonElement? psData)
        {
            var section = new SectionScore { SectionName = "Devices", Weight = 0 };
            section.RawInputs = new Dictionary<string, string>();
            section.AppliedRules = new List<AppliedRule>();
            
            int score = 100;
            
            var deviceIssues = ExtractDeviceIssuesFromPs(psData);
            section.RawInputs["ErrorDevices"] = deviceIssues.errors.ToString();
            section.RawInputs["DegradedDevices"] = deviceIssues.degraded.ToString();
            
            // WD SES ou périphérique USB externe = conseil seulement, pas de grosse chute
            if (deviceIssues.criticalErrors > 0)
            {
                score -= 50;
                section.AppliedRules.Add(new AppliedRule("DEVICE_CRITICAL", $"{deviceIssues.criticalErrors} périphérique(s) critique(s)", -50));
            }
            else if (deviceIssues.errors > 3)
            {
                score -= 20;
                section.AppliedRules.Add(new AppliedRule("DEVICE_ERRORS", $"{deviceIssues.errors} périphérique(s) en erreur", -20));
            }
            else if (deviceIssues.errors > 0)
            {
                score -= 5;
                section.AppliedRules.Add(new AppliedRule("DEVICE_MINOR", $"{deviceIssues.errors} périphérique(s) mineur(s)", -5));
            }
            
            section.Score = Math.Max(0, score);
            if (section.Status == SectionStatus.Unknown)
                section.Status = ScoreToStatus(section.Score);
            
            return section;
        }

        private static SectionScore EvaluateUpdatesSection(JsonElement? psData)
        {
            var section = new SectionScore { SectionName = "Updates", Weight = 0 };
            section.RawInputs = new Dictionary<string, string>();
            section.AppliedRules = new List<AppliedRule>();
            
            int score = 100;
            
            var pendingUpdates = ExtractPendingUpdatesFromPs(psData);
            section.RawInputs["PendingUpdates"] = pendingUpdates.ToString();
            
            if (pendingUpdates > 0)
            {
                score -= 5;
                section.AppliedRules.Add(new AppliedRule("UPDATES_PENDING", $"{pendingUpdates} mise(s) à jour en attente", -5));
            }
            
            section.Score = Math.Max(0, score);
            if (section.Status == SectionStatus.Unknown)
                section.Status = ScoreToStatus(section.Score);
            
            return section;
        }

        #endregion

        #region Critical Penalties & Hard Caps

        private static List<CriticalPenalty> ApplyCriticalPenalties(RulesScoreReport report, JsonElement? psData, HardwareSensorsResult? sensors)
        {
            var penalties = new List<CriticalPenalty>();
            
            // Ces pénalités sont appliquées APRÈS la moyenne pondérée
            
            return penalties;
        }

        private static void ApplyHardCaps(RulesScoreReport report, JsonElement? psData)
        {
            // Hard cap: Defender OFF sans AV tiers ou malware réel → max 40
            var securitySection = report.Sections.FirstOrDefault(s => s.SectionName == "Security");
            if (securitySection?.AppliedRules?.Any(r => r.RuleId == "DEFENDER_OFF_NO_AV" || r.RuleId == "MALWARE_DETECTED") == true)
            {
                if (report.GlobalHealthScore > 40)
                {
                    report.GlobalHealthScore = 40;
                    report.HardCapApplied = "SecurityCritical: Score plafonné à 40";
                }
            }
            
            // Hard cap: BSOD récent → max 50
            var stabilitySection = report.Sections.FirstOrDefault(s => s.SectionName == "Stability");
            if (stabilitySection?.AppliedRules?.Any(r => r.RuleId == "BSOD_DETECTED") == true)
            {
                if (report.GlobalHealthScore > 50)
                {
                    report.GlobalHealthScore = 50;
                    report.HardCapApplied = "BSOD: Score plafonné à 50";
                }
            }
            
            // Hard cap: SMART critique → max 35
            var storageSection = report.Sections.FirstOrDefault(s => s.SectionName == "StorageSystem");
            if (storageSection?.AppliedRules?.Any(r => r.RuleId == "SMART_REALLOCATED_CRITICAL" || r.RuleId == "SMART_PENDING_CRITICAL") == true)
            {
                if (report.GlobalHealthScore > 35)
                {
                    report.GlobalHealthScore = 35;
                    report.HardCapApplied = "SMART: Score plafonné à 35";
                }
            }
        }

        #endregion

        #region Confidence Model

        private static ConfidenceModel ComputeConfidence(JsonElement? psData, HardwareSensorsResult? sensors, List<string> collectorErrors)
        {
            var model = new ConfidenceModel();
            model.BaseScore = 100;
            model.MissingSignals = new List<string>();
            model.CollectorErrors = new List<string>(collectorErrors ?? new List<string>());
            
            int score = 100;
            
            // Pénalités pour données manquantes
            if (sensors?.Cpu?.CpuTempC?.Available != true)
            {
                score -= 10;
                model.MissingSignals.Add("CPU Température (C#)");
            }
            
            if (sensors?.Gpu?.GpuTempC?.Available != true && sensors?.Gpu?.Name?.Available == true)
            {
                score -= 8;
                model.MissingSignals.Add("GPU Température (C#)");
            }
            
            if (sensors?.Gpu?.VramTotalMB?.Available != true && sensors?.Gpu?.Name?.Available == true)
            {
                score -= 5;
                model.MissingSignals.Add("VRAM (C#)");
            }
            
            // PerfCounters manquants = collecte échouée
            if (IsPerfCountersEmpty(psData))
            {
                score -= 30;
                model.MissingSignals.Add("PerfCounters (PS) - CRITIQUE");
                model.CollectionFailed = true;
            }
            
            // Erreurs de collecte
            score -= collectorErrors?.Count * 5 ?? 0;
            
            model.ConfidenceScore = Math.Max(0, Math.Min(100, score));
            model.ConfidenceLevel = model.ConfidenceScore >= 90 ? "Fiable" :
                                    model.ConfidenceScore >= 70 ? "Moyen" : "Faible";
            
            return model;
        }

        private static bool EvaluateCollectionComplete(JsonElement? psData, HardwareSensorsResult? sensors, List<string> collectorErrors)
        {
            // Conditions de collecte terminée:
            // 1. CPU temp OK (si disponible via LHM)
            // 2. GPU temp/load/VRAM OK si GPU dédié
            // 3. Espace volumes OK
            // 4. MissingData cohérent
            // 5. PerfCounters présents et non vides
            
            if (IsPerfCountersEmpty(psData))
                return false;
            
            // On n'exige pas que tous les capteurs soient disponibles (dépend du matériel)
            // Mais on note les erreurs de collecte
            
            return collectorErrors == null || collectorErrors.Count == 0;
        }

        private static string GetCollectionFailureReason(JsonElement? psData, HardwareSensorsResult? sensors, List<string> collectorErrors)
        {
            var reasons = new List<string>();
            
            if (IsPerfCountersEmpty(psData))
                reasons.Add("PerfCounters absents ou vides");
            
            if (collectorErrors?.Count > 0)
                reasons.Add($"{collectorErrors.Count} erreur(s) de collecte");
            
            return string.Join("; ", reasons);
        }

        #endregion

        #region Data Extractors (from PowerShell JSON)

        private static double? ExtractCpuLoadFromPs(JsonElement? psData)
        {
            if (!psData.HasValue) return null;
            
            try
            {
                // Path: sections.CPU.data.cpuList[0].currentLoad ou load
                if (psData.Value.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("CPU", out var cpu) &&
                    cpu.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("cpuList", out var cpuList) &&
                    cpuList.GetArrayLength() > 0)
                {
                    var firstCpu = cpuList[0];
                    if (firstCpu.TryGetProperty("currentLoad", out var load))
                        return load.GetDouble();
                    if (firstCpu.TryGetProperty("load", out var load2))
                        return load2.GetDouble();
                }
            }
            catch { }
            
            return null;
        }

        private static (double totalGB, double availableGB, double usagePercent)? ExtractRamDataFromPs(JsonElement? psData)
        {
            if (!psData.HasValue) return null;
            
            try
            {
                if (psData.Value.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("Memory", out var mem) &&
                    mem.TryGetProperty("data", out var data))
                {
                    double total = 0, available = 0;
                    if (data.TryGetProperty("totalGB", out var t)) total = t.GetDouble();
                    if (data.TryGetProperty("availableGB", out var a)) available = a.GetDouble();
                    
                    double usage = total > 0 ? ((total - available) / total * 100) : 0;
                    return (total, available, usage);
                }
            }
            catch { }
            
            return null;
        }

        private static (double totalGB, double freeGB, double freePercent)? ExtractVolumeCFromPs(JsonElement? psData)
        {
            if (!psData.HasValue) return null;
            
            try
            {
                if (psData.Value.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("Storage", out var storage) &&
                    storage.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("volumes", out var volumes))
                {
                    foreach (var vol in volumes.EnumerateArray())
                    {
                        if (vol.TryGetProperty("driveLetter", out var letter) && 
                            letter.GetString()?.ToUpper() == "C")
                        {
                            double total = 0, free = 0;
                            if (vol.TryGetProperty("sizeGB", out var s)) total = s.GetDouble();
                            if (vol.TryGetProperty("freeSpaceGB", out var f)) free = f.GetDouble();
                            
                            double freePercent = total > 0 ? (free / total * 100) : 0;
                            return (total, free, freePercent);
                        }
                    }
                }
            }
            catch { }
            
            return null;
        }

        private static (int pendingSectors, int reallocatedSectors) ExtractSmartIssuesFromPs(JsonElement? psData)
        {
            if (!psData.HasValue) return (0, 0);
            
            try
            {
                if (psData.Value.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("SmartDetails", out var smart) &&
                    smart.TryGetProperty("data", out var data))
                {
                    int pending = 0, reallocated = 0;
                    
                    if (data.TryGetProperty("pendingSectorCount", out var ps) && ps.ValueKind == JsonValueKind.Number)
                        pending = ps.GetInt32();
                    else if (data.TryGetProperty("PendingSectorCount", out ps) && ps.ValueKind == JsonValueKind.Number)
                        pending = ps.GetInt32();
                    
                    if (data.TryGetProperty("reallocatedSectorCount", out var rs) && rs.ValueKind == JsonValueKind.Number)
                        reallocated = rs.GetInt32();
                    else if (data.TryGetProperty("ReallocatedSectorCount", out rs) && rs.ValueKind == JsonValueKind.Number)
                        reallocated = rs.GetInt32();
                    
                    return (pending, reallocated);
                }
            }
            catch { /* SmartDetails non exploitable */ }
            
            return (0, 0);
        }

        private static string? ExtractOsInfoFromPs(JsonElement? psData)
        {
            if (!psData.HasValue) return null;
            
            try
            {
                if (psData.Value.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("OS", out var os) &&
                    os.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("caption", out var caption))
                {
                    return caption.GetString();
                }
            }
            catch { }
            
            return null;
        }

        private static bool IsDefenderOff(JsonElement? psData)
        {
            if (!psData.HasValue) return false;
            
            try
            {
                if (psData.Value.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("Security", out var sec) &&
                    sec.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("defender", out var defender) &&
                    defender.TryGetProperty("realTimeProtection", out var rtp))
                {
                    return !rtp.GetBoolean();
                }
            }
            catch { }
            
            return false;
        }

        private static bool HasThirdPartyAntivirus(JsonElement? psData)
        {
            if (!psData.HasValue) return false;
            
            try
            {
                if (psData.Value.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("Security", out var sec) &&
                    sec.TryGetProperty("data", out var data))
                {
                    // Vérifier si un AV tiers est signalé
                    if (data.TryGetProperty("antivirusName", out var avName) && avName.ValueKind == JsonValueKind.String)
                    {
                        var name = avName.GetString()?.ToLowerInvariant() ?? "";
                        // Windows Defender n'est pas un AV tiers
                        if (!string.IsNullOrEmpty(name) && !name.Contains("defender") && !name.Contains("microsoft"))
                            return true;
                    }
                    if (data.TryGetProperty("AntivirusName", out avName) && avName.ValueKind == JsonValueKind.String)
                    {
                        var name = avName.GetString()?.ToLowerInvariant() ?? "";
                        if (!string.IsNullOrEmpty(name) && !name.Contains("defender") && !name.Contains("microsoft"))
                            return true;
                    }
                }
            }
            catch { /* Pas de données SecurityCenter exploitables */ }
            
            return false;
        }

        private static (int pua, int vulnerableDrivers, int realThreats) ExtractMalwareFromPs(JsonElement? psData)
        {
            int pua = 0, vulnDrivers = 0, realThreats = 0;
            
            if (!psData.HasValue) return (pua, vulnDrivers, realThreats);
            
            try
            {
                if (psData.Value.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("AdvancedAnalysis", out var aa) &&
                    aa.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("malware", out var malware) &&
                    malware.TryGetProperty("threats", out var threats))
                {
                    foreach (var threat in threats.EnumerateArray())
                    {
                        var name = "";
                        int severity = 1;
                        
                        if (threat.TryGetProperty("name", out var n)) name = n.GetString() ?? "";
                        if (threat.TryGetProperty("severity", out var s)) severity = s.GetInt32();
                        
                        if (name.Contains("VulnerableDriver", StringComparison.OrdinalIgnoreCase))
                            vulnDrivers++;
                        else if (name.StartsWith("PUA", StringComparison.OrdinalIgnoreCase) || 
                                 name.StartsWith("PUABundler", StringComparison.OrdinalIgnoreCase) ||
                                 name.StartsWith("PUADlManager", StringComparison.OrdinalIgnoreCase))
                            pua++;
                        else if (severity >= 3)
                            realThreats++;
                    }
                }
            }
            catch { }
            
            return (pua, vulnDrivers, realThreats);
        }

        private static (int systemErrors, int appErrors) ExtractEventLogsFromPs(JsonElement? psData)
        {
            int sysErr = 0, appErr = 0;
            
            if (!psData.HasValue) return (sysErr, appErr);
            
            try
            {
                if (psData.Value.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("EventLogs", out var el) &&
                    el.TryGetProperty("data", out var data))
                {
                    if (data.TryGetProperty("system", out var sys) && sys.TryGetProperty("errors", out var se))
                        sysErr = se.GetInt32();
                    if (data.TryGetProperty("application", out var app) && app.TryGetProperty("errors", out var ae))
                        appErr = ae.GetInt32();
                }
            }
            catch { }
            
            return (sysErr, appErr);
        }

        private static int ExtractBsodCountFromPs(JsonElement? psData)
        {
            if (!psData.HasValue) return 0;
            
            try
            {
                if (psData.Value.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("EventLogs", out var el) &&
                    el.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("bsodCount", out var bc))
                {
                    return bc.GetInt32();
                }
            }
            catch { }
            
            return 0;
        }

        private static int ExtractCrashCountFromPs(JsonElement? psData)
        {
            if (!psData.HasValue) return 0;
            
            try
            {
                if (psData.Value.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("ReliabilityHistory", out var rh) &&
                    rh.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("crashes", out var c))
                {
                    return c.GetInt32();
                }
            }
            catch { }
            
            return 0;
        }

        private static (int errors, int degraded, int criticalErrors) ExtractDeviceIssuesFromPs(JsonElement? psData)
        {
            int errors = 0, degraded = 0, critical = 0;
            
            if (!psData.HasValue) return (errors, degraded, critical);
            
            try
            {
                if (psData.Value.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("DevicesDrivers", out var dd) &&
                    dd.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("problemDevices", out var pd))
                {
                    foreach (var dev in pd.EnumerateArray())
                    {
                        var status = "";
                        if (dev.TryGetProperty("status", out var s)) status = s.GetString() ?? "";
                        
                        if (status.Contains("Error", StringComparison.OrdinalIgnoreCase))
                            errors++;
                        else if (status.Contains("Degraded", StringComparison.OrdinalIgnoreCase))
                            degraded++;
                        
                        // Critical = error on internal/important device
                        var name = "";
                        if (dev.TryGetProperty("name", out var n)) name = n.GetString() ?? "";
                        
                        if (status.Contains("Error") && !name.Contains("USB", StringComparison.OrdinalIgnoreCase))
                            critical++;
                    }
                }
            }
            catch { }
            
            return (errors, degraded, critical);
        }

        private static int ExtractPendingUpdatesFromPs(JsonElement? psData)
        {
            if (!psData.HasValue) return 0;
            
            try
            {
                if (psData.Value.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("WindowsUpdate", out var wu) &&
                    wu.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("pendingCount", out var pc))
                {
                    return pc.GetInt32();
                }
            }
            catch { }
            
            return 0;
        }

        private static bool IsPerfCountersEmpty(JsonElement? psData)
        {
            if (!psData.HasValue) return true;
            
            try
            {
                if (psData.Value.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("PerformanceCounters", out var pc))
                {
                    if (pc.TryGetProperty("status", out var status))
                    {
                        var s = status.GetString();
                        if (s == "FAILED" || s == "PARTIAL") return true;
                    }
                    
                    if (pc.TryGetProperty("data", out var data))
                    {
                        // Check if data is empty or has no meaningful content
                        if (data.ValueKind == JsonValueKind.Null) return true;
                        if (data.ValueKind == JsonValueKind.Object)
                        {
                            var propCount = 0;
                            foreach (var _ in data.EnumerateObject()) propCount++;
                            return propCount == 0;
                        }
                    }
                    
                    return false;
                }
            }
            catch { }
            
            return true;
        }

        #endregion

        #region Helpers

        private static string ScoreToGrade(int score)
        {
            return score switch
            {
                >= 95 => "A+",
                >= 90 => "A",
                >= 80 => "B+",
                >= 70 => "B",
                >= 60 => "C",
                >= 50 => "D",
                _ => "F"
            };
        }

        private static string ScoreToLabel(int score)
        {
            return score switch
            {
                >= 90 => "Excellent",
                >= 70 => "Bon",
                >= 50 => "À surveiller",
                >= 30 => "Dégradé",
                _ => "Critique"
            };
        }

        private static SectionStatus ScoreToStatus(int score)
        {
            return score switch
            {
                >= 90 => SectionStatus.OK,
                >= 70 => SectionStatus.Warning,
                >= 50 => SectionStatus.Degraded,
                _ => SectionStatus.Critical
            };
        }

        private static List<string> GenerateCpuRecommendations(SectionScore section)
        {
            var recs = new List<string>();
            
            if (section.AppliedRules.Any(r => r.RuleId.Contains("TEMP_DANGER")))
                recs.Add("Vérifier immédiatement le refroidissement CPU");
            else if (section.AppliedRules.Any(r => r.RuleId.Contains("TEMP_THROTTLING")))
                recs.Add("Améliorer le refroidissement ou réduire la charge");
            
            if (section.AppliedRules.Any(r => r.RuleId.Contains("LOAD_CRITICAL")))
                recs.Add("Identifier les processus consommant le CPU");
            
            return recs;
        }

        private static List<string> GenerateGpuRecommendations(SectionScore section)
        {
            var recs = new List<string>();
            
            if (section.AppliedRules.Any(r => r.RuleId.Contains("TEMP_DANGER")))
                recs.Add("Vérifier immédiatement le refroidissement GPU");
            
            if (section.AppliedRules.Any(r => r.RuleId.Contains("VRAM")))
                recs.Add("Fermer les applications graphiques non utilisées");
            
            return recs;
        }

        #endregion
    }
}
