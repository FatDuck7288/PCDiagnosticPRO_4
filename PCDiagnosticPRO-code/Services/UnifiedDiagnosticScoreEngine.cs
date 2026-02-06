using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Unified Diagnostic Intelligence Scoring (UDIS).
    /// UDIS = 0.7 * MachineHealthScore + 0.2 * DataReliabilityScore + 0.1 * DiagnosticClarityScore.
    /// Source de vérité unique pour le scoring. Lit les sorties JSON/TXT du scan.
    /// </summary>
    public static class UnifiedDiagnosticScoreEngine
    {
        /// <summary>
        /// Priorité critique → haute → moyenne → faible pour MHS.
        /// </summary>
        private static readonly Dictionary<HealthDomain, string> DomainPriority = new()
        {
            { HealthDomain.OS, "Critical" },
            { HealthDomain.Storage, "Critical" },
            { HealthDomain.CPU, "High" },
            { HealthDomain.GPU, "High" },
            { HealthDomain.RAM, "High" },
            { HealthDomain.SystemStability, "High" },
            { HealthDomain.Network, "Medium" },
            { HealthDomain.Drivers, "Low" }
        };

        private static readonly Dictionary<string, int> PriorityWeight = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Critical", 25 },
            { "High", 20 },
            { "Medium", 10 },
            { "Low", 5 }
        };

        /// <summary>
        /// Calcule le rapport UDIS complet depuis les données observées (report + JSON + sensors).
        /// </summary>
        public static UdisReport Compute(
            HealthReport report,
            JsonElement? psRoot,
            HardwareSensorsResult? sensors,
            CollectorDiagnosticsService.CollectorDiagnosticsResult? diagnostics)
        {
            var udis = new UdisReport();
            try
            {
                diagnostics ??= psRoot.HasValue ? CollectorDiagnosticsService.Analyze(psRoot.Value, sensors) : null;
                int collectorErrors = report.CollectorErrorsLogical;
                var missingData = report.MissingData ?? new List<string>();

                bool hasSecurityData = !missingData.Any(m => m.Contains("Security", StringComparison.OrdinalIgnoreCase) || m.Contains("Defender", StringComparison.OrdinalIgnoreCase));
                bool hasSmartData = report.Sections.Any(s => s.Domain == HealthDomain.Storage && s.HasData);
                bool smartSuspect = report.Errors.Any(e => e.Code.Contains("SMART", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("SMART", StringComparison.OrdinalIgnoreCase));

                udis.DataReliabilityScore = DataReliabilityEngine.Compute(collectorErrors, missingData, hasSecurityData, hasSmartData);
                udis.MachineHealthScore = ComputeMachineHealthScore(report, sensors, psRoot);
                udis.DiagnosticClarityScore = ComputeDiagnosticClarityScore(report, diagnostics, psRoot);

                double rawUdis = 0.7 * udis.MachineHealthScore + 0.2 * udis.DataReliabilityScore + 0.1 * udis.DiagnosticClarityScore;
                udis.UdisScore = Math.Max(0, Math.Min(100, (int)Math.Round(rawUdis)));
                udis.Grade = ScoreToGrade(udis.UdisScore);
                udis.Message = ScoreToMessage(udis.UdisScore);

                if (psRoot.HasValue)
                {
                    var cpuProfile = CpuProfileAnalyzer.Analyze(psRoot.Value);
                    udis.CpuPerformanceTier = cpuProfile.PerformanceTier;
                    var stabilityInputs = SystemStabilityAnalyzer.ExtractFromJson(psRoot.Value);
                    udis.SystemStabilityIndex = SystemStabilityAnalyzer.ComputeIndex(stabilityInputs);

                    // Boot Time Health
                    var bootHealth = BootTimeHealthAnalyzer.Analyze(psRoot);
                    udis.BootHealthScore = bootHealth.BootHealthScore;
                    udis.BootHealthTier = bootHealth.BootHealthTier;
                    udis.BootTimeSeconds = bootHealth.BootTimeSeconds;

                    // Storage IO Health
                    var storageIo = StorageIoHealthAnalyzer.Analyze(psRoot);
                    udis.StorageIoHealthScore = storageIo.IoHealthScore;
                    udis.StorageIoStatus = storageIo.IoHealthStatus;
                }

                // Thermal Envelope (basé sur capteurs C#)
                var thermal = ThermalEnvelopeAnalyzer.Analyze(sensors);
                udis.ThermalScore = thermal.ThermalScore;
                udis.ThermalStatus = thermal.ThermalStatus;

                // Build sections summary for UI
                udis.SectionsSummary = BuildSectionsSummary(report);

                udis.Findings = DiagnosticFindingsBuilder.Build(report, psRoot, udis.DataReliabilityScore, collectorErrors, missingData);
                var (autoFixAllowed, blockReason) = DiagnosticFindingsBuilder.EvaluateSafetyGate(
                    udis.DataReliabilityScore, collectorErrors, missingData, hasSecurityData, smartSuspect && !hasSmartData);
                udis.AutoFixAllowed = autoFixAllowed;
                udis.AutoFixBlockReason = blockReason;

                App.LogMessage($"[UDIS] MHS={udis.MachineHealthScore} DRS={udis.DataReliabilityScore} DCS={udis.DiagnosticClarityScore} => UDIS={udis.UdisScore} Grade={udis.Grade}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[UDIS] Erreur: {ex.Message}");
                udis.UdisScore = 0;
                udis.Grade = "?";
                udis.Message = "Impossible d'évaluer — erreur moteur.";
            }
            return udis;
        }

        /// <summary>
        /// Machine Health Score — priorité critique / haute / moyenne / faible, basé sur données réelles observées.
        /// </summary>
        private static int ComputeMachineHealthScore(HealthReport report, HardwareSensorsResult? sensors, JsonElement? psRoot)
        {
            int score = 100;
            var breakdown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Critical"] = 0, ["High"] = 0, ["Medium"] = 0, ["Low"] = 0 };

            foreach (var section in report.Sections.Where(s => s.HasData))
            {
                string priority = DomainPriority.TryGetValue(section.Domain, out var p) ? p : "Low";
                int weight = PriorityWeight.TryGetValue(priority, out var w) ? w : 5;
                int sectionScore = Math.Max(0, Math.Min(100, section.Score));
                int penalty = 100 - sectionScore;
                if (penalty > 0)
                {
                    int deduct = (int)Math.Round(penalty * (weight / 25.0));
                    score -= Math.Min(deduct, 15);
                    breakdown[priority] = breakdown[priority] + deduct;
                }
            }

            if (report.Errors.Any(e => e.Code.Contains("WMI", StringComparison.OrdinalIgnoreCase) || e.Code.Contains("SMART", StringComparison.OrdinalIgnoreCase)))
                score -= 10;
            if (report.Errors.Any(e => e.Code.Contains("Security", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("Defender", StringComparison.OrdinalIgnoreCase)))
                score -= 15;

            if (sensors != null)
            {
                if (sensors.Cpu.CpuTempC.Available && sensors.Cpu.CpuTempC.Value > 90)
                    score -= 20;
                else if (sensors.Cpu.CpuTempC.Available && sensors.Cpu.CpuTempC.Value > 80)
                    score -= 10;
                if (sensors.Gpu.GpuTempC.Available && sensors.Gpu.GpuTempC.Value > 95)
                    score -= 20;
                else if (sensors.Gpu.GpuTempC.Available && sensors.Gpu.GpuTempC.Value > 85)
                    score -= 10;
            }

            return Math.Max(0, Math.Min(100, score));
        }

        /// <summary>
        /// Diagnostic Clarity Score — qualité JSON, présence findings structurés, absence sentinelles exploitées.
        /// </summary>
        private static int ComputeDiagnosticClarityScore(HealthReport report, CollectorDiagnosticsService.CollectorDiagnosticsResult? diagnostics, JsonElement? psRoot)
        {
            int score = 100;
            if (diagnostics != null)
            {
                if (diagnostics.InvalidatedMetrics.Count > 0)
                    score -= Math.Min(20, diagnostics.InvalidatedMetrics.Count * 5);
                if (diagnostics.MissingDataNormalized.Count > 5)
                    score -= 10;
            }
            if (report.FindingsCount() == 0 && report.Sections.All(s => !s.HasData))
                score -= 15;
            return Math.Max(0, Math.Min(100, score));
        }

        private static int FindingsCount(this HealthReport report)
        {
            return report.Sections?.Sum(s => s.Findings?.Count ?? 0) ?? 0;
        }

        private static string ScoreToGrade(int score)
        {
            return score switch { >= 95 => "A+", >= 90 => "A", >= 80 => "B+", >= 70 => "B", >= 60 => "C", >= 50 => "D", _ => "F" };
        }

        private static string ScoreToMessage(int score)
        {
            return score switch
            {
                >= 95 => "État excellent — diagnostic fiable.",
                >= 80 => "Bon état — quelques points à surveiller.",
                >= 60 => "État dégradé — actions recommandées.",
                >= 40 => "État préoccupant — intervention conseillée.",
                _ => "État critique — intervention urgente."
            };
        }

        /// <summary>
        /// Construit le résumé des sections pour l'UI.
        /// </summary>
        private static List<UdisSectionSummary> BuildSectionsSummary(HealthReport report)
        {
            var summaries = new List<UdisSectionSummary>();
            foreach (var section in report.Sections)
            {
                string priority = DomainPriority.TryGetValue(section.Domain, out var p) ? p : "Low";
                string status = section.Score switch
                {
                    >= 90 => "Excellent",
                    >= 70 => "Bon",
                    >= 50 => "À surveiller",
                    >= 30 => "Dégradé",
                    _ => "Critique"
                };
                string recommendation = section.Findings?.FirstOrDefault()?.Description ?? "";

                summaries.Add(new UdisSectionSummary
                {
                    SectionName = section.Domain.ToString(),
                    Score = section.Score,
                    Status = status,
                    Priority = priority,
                    HasData = section.HasData,
                    Recommendation = recommendation
                });
            }
            return summaries;
        }

        /// <summary>
        /// Ajoute les résultats du SpeedTest au rapport UDIS (async, à appeler séparément).
        /// </summary>
        public static async System.Threading.Tasks.Task<UdisReport> AddNetworkSpeedTestAsync(
            UdisReport udis,
            System.Threading.CancellationToken cancellationToken = default)
        {
            try
            {
                var speedResult = await NetworkRealSpeedAnalyzer.MeasureAsync(cancellationToken).ConfigureAwait(false);
                if (speedResult.Success)
                {
                    udis.DownloadMbps = speedResult.DownloadMbps;
                    udis.UploadMbps = speedResult.UploadMbps;
                    udis.LatencyMs = speedResult.LatencyMs;
                    udis.NetworkSpeedTier = speedResult.SpeedTier;
                    udis.NetworkRecommendation = speedResult.Recommendation;
                    App.LogMessage($"[UDIS.SpeedTest] Download={speedResult.DownloadMbps:F1} Mbps, Latency={speedResult.LatencyMs:F0}ms, Tier={speedResult.SpeedTier}");
                }
                else
                {
                    udis.NetworkSpeedTier = "Non mesuré";
                    udis.NetworkRecommendation = speedResult.ErrorMessage ?? "Test réseau échoué.";
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[UDIS.SpeedTest] Erreur: {ex.Message}");
                udis.NetworkSpeedTier = "Erreur";
            }
            return udis;
        }
    }
}
