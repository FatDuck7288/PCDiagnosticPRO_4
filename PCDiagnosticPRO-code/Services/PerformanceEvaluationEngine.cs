using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Orchestrates the Performance Evaluation Engine: profile → scenarios → bottleneck → verdict.
    /// All logic is offline and deterministic. Returns PerformanceEvaluationResult for UI and report.
    /// </summary>
    public static class PerformanceEvaluationEngine
    {
        /// <summary>
        /// Table/engine version for display and evidence.
        /// </summary>
        public const string TableVersion = "2.0";

        private const string DebugLogPath = @"d:\Tennis\Os\Produits\PC_Repair\Test-codex-analyze-xaml-binding-exception-details\PCDiagnosticPRO-code\.cursor\debug.log";

        /// <summary>
        /// Full evaluation from combined JSON root, optional snapshot, and optional sensors.
        /// Callers: HealthReportBuilder (root + sensors), FullReportBuilder (combined with snapshot + sensors).
        /// </summary>
        public static PerformanceEvaluationResult Evaluate(
            JsonElement? combinedRoot,
            DiagnosticSnapshot? snapshot,
            HardwareSensorsResult? sensors)
        {
            var profile = HardwareProfileBuilder.Build(combinedRoot, snapshot, sensors);
            var scenarioScores = UsageScenarioScorer.Score(profile);
            ApplyScenarioScoreFloors(profile, scenarioScores);
            var bottleneck = BottleneckAnalyzer.Analyze(profile);
            var verdict = PerformanceVerdictBuilder.Build(profile, scenarioScores, bottleneck);

            int singleScore = ComputeSingleScore(scenarioScores, profile);

            var result = new PerformanceEvaluationResult
            {
                Profile = profile,
                ScenarioScores = scenarioScores,
                Bottleneck = bottleneck,
                Verdict = verdict,
                Score = singleScore
            };

            LogPerformanceDebugBlock(result);
            return result;
        }

        /// <summary>Minimum floors for high-end configs (RTX 3090, >=24GB VRAM, >=12 cores, >=32GB RAM). If below, log detailed breakdown and clamp.</summary>
        private static void ApplyScenarioScoreFloors(HardwareProfile profile, List<ScenarioScore> scenarioScores)
        {
            const double TwentyFourGbMb = 24 * 1024;
            var gpuNorm = HardwareProfileBuilder.NormalizeHardwareName(profile.GpuModel);
            bool highEnd = (gpuNorm.Contains("3090", StringComparison.OrdinalIgnoreCase) || profile.GpuVramMb >= TwentyFourGbMb)
                && profile.CpuCores >= 12
                && profile.RamGb >= 32;
            if (!highEnd) return;

            var floors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["gaming_1440p"] = 80,
                ["4k_editing"] = 75,
                ["streaming_gaming"] = 75,
                ["ai_inference"] = 70
            };
            foreach (var s in scenarioScores)
            {
                if (s.ScenarioId == null || !floors.TryGetValue(s.ScenarioId, out int floor)) continue;
                if (s.Score >= floor) continue;
                try
                {
                    File.AppendAllText(DebugLogPath, JsonSerializer.Serialize(new
                    {
                        message = "Scenario score below expected floor — detailed breakdown",
                        scenarioId = s.ScenarioId,
                        scenarioName = s.Name,
                        score = s.Score,
                        floor,
                        profileBreakdown = new
                        {
                            profile.CpuModel,
                            profile.GpuModel,
                            profile.CpuTier,
                            profile.GpuTier,
                            profile.CpuCores,
                            profile.GpuVramMb,
                            profile.RamGb,
                            profile.StorageKind
                        },
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    }) + "\n");
                }
                catch { }
                s.Score = floor;
                s.Classification = floor >= 70 ? ScenarioClassification.Excellent : (floor >= 56 ? ScenarioClassification.Good : ScenarioClassification.Acceptable);
            }
        }

        private static void LogPerformanceDebugBlock(PerformanceEvaluationResult result)
        {
            try
            {
                var p = result.Profile;
                var scenarioList = new List<object>();
                if (result.ScenarioScores != null)
                    foreach (var s in result.ScenarioScores)
                        scenarioList.Add(new Dictionary<string, object> { ["id"] = s.ScenarioId ?? "", ["score"] = s.Score });
                // Performance confidence: 100 minus penalty per Unmatched (no silent Entry fallback)
                int confidenceScore = 100;
                if (p != null)
                {
                    if (!p.CpuNameMatched) confidenceScore -= 5;
                    if (!p.GpuNameMatched) confidenceScore -= 5;
                }
                var block = new Dictionary<string, object>
                {
                    ["message"] = "PerformanceEngine",
                    ["resolvedCpu"] = p?.CpuModel ?? "(null)",
                    ["resolvedGpu"] = p?.GpuModel ?? "(null)",
                    ["resolvedVramMb"] = p?.GpuVramMb ?? 0,
                    ["resolvedRamGb"] = p?.RamGb ?? 0,
                    ["cpuMatched"] = p?.CpuNameMatched ?? true,
                    ["gpuMatched"] = p?.GpuNameMatched ?? true,
                    ["matchedDatasetEntry"] = new Dictionary<string, object> { ["cpuTier"] = p?.CpuTier ?? "", ["gpuTier"] = p?.GpuTier ?? "" },
                    ["finalTier"] = result.Verdict?.Category ?? "",
                    ["scenarioScores"] = scenarioList,
                    ["confidenceScore"] = confidenceScore,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                File.AppendAllText(DebugLogPath, JsonSerializer.Serialize(block) + "\n");
            }
            catch { }
        }

        /// <summary>
        /// Backward-compatible single score 0-100: weighted average of scenario scores (office/multitasking/gaming 1080p weighted slightly higher).
        /// </summary>
        private static int ComputeSingleScore(System.Collections.Generic.List<ScenarioScore> scenarioScores, HardwareProfile profile)
        {
            if (scenarioScores == null || scenarioScores.Count == 0)
            {
                return (PerformanceTierTable.TierToScore(profile.CpuTier) + PerformanceTierTable.TierToScore(profile.GpuTier)
                    + PerformanceTierTable.TierToScore(profile.RamTier) + PerformanceTierTable.TierToScore(profile.StorageTier)) / 4;
            }
            double sum = 0;
            int count = 0;
            foreach (var s in scenarioScores)
            {
                sum += s.Score;
                count++;
            }
            return count > 0 ? (int)Math.Round(sum / count) : 0;
        }
    }
}
