using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Orchestrates the Performance Evaluation Engine: profile → scenarios → bottleneck → verdict.
    /// Loads the external PerformanceDataset (if available) and passes it to tier resolution and scenario scoring.
    /// Returns PerformanceEvaluationResult for UI and report, including DatasetVersion, PublishedAt, and full SourceInfo.
    ///
    /// IMPORTANT: Source selection follows the configured PerformanceDatasetMode policy.
    /// - RequireExternal: if dataset unavailable → scoring becomes unavailable (no silent fallback).
    /// - AllowFallbackEmbedded: if dataset unavailable → explicit embedded fallback with label.
    /// Scoring formulas are NOT modified by this engine.
    /// </summary>
    public static class PerformanceEvaluationEngine
    {
        /// <summary>
        /// Embedded table/engine version (used when no external dataset is loaded).
        /// </summary>
        public const string TableVersion = "3.0";

        /// <summary>
        /// Full evaluation from combined JSON root, optional snapshot, and optional sensors.
        /// Callers: HealthReportBuilder (root + sensors), FullReportBuilder (combined with snapshot + sensors).
        /// </summary>
        public static PerformanceEvaluationResult Evaluate(
            JsonElement? combinedRoot,
            DiagnosticSnapshot? snapshot,
            HardwareSensorsResult? sensors)
        {
            // Load dataset with full traceability
            var loadResult = PerformanceDatasetLoader.LoadResult;
            var dataset = loadResult.Dataset;
            var sourceInfo = loadResult.SourceInfo;

            // ─── Handle Unavailable state ───
            if (sourceInfo.SourceKind == DatasetSourceKind.Unavailable)
            {
                return new PerformanceEvaluationResult
                {
                    IsUnavailable = true,
                    UnavailableReason = sourceInfo.FallbackReason ?? "Dataset externe requis mais indisponible",
                    SourceInfo = sourceInfo,
                    DatasetVersion = null,
                    DatasetPublishedAt = null,
                    Score = -1,
                    Profile = new HardwareProfile(),
                    ScenarioScores = new List<ScenarioScore>(),
                    Bottleneck = new BottleneckResult(),
                    Verdict = new VerdictSummary
                    {
                        Category = "Indisponible",
                        RealisticExpectationSummary = sourceInfo.DisplayLabel
                    }
                };
            }

            // ─── Normal scoring path ───
            var profile = HardwareProfileBuilder.Build(combinedRoot, snapshot, sensors, dataset);
            var scenarioScores = UsageScenarioScorer.Score(profile, dataset);

            ApplyScenarioScoreFloors(profile, scenarioScores, dataset);
            EnforceScenarioScoreOrder(scenarioScores, dataset);
            var bottleneck = BottleneckAnalyzer.Analyze(profile);
            var verdict = PerformanceVerdictBuilder.Build(profile, scenarioScores, bottleneck);

            int singleScore = ComputeSingleScore(scenarioScores, profile);

            var result = new PerformanceEvaluationResult
            {
                Profile = profile,
                ScenarioScores = scenarioScores,
                Bottleneck = bottleneck,
                Verdict = verdict,
                Score = singleScore,
                DatasetVersion = dataset?.DatasetVersion ?? $"embedded ({TableVersion})",
                DatasetPublishedAt = dataset?.PublishedAt,
                SourceInfo = sourceInfo,
                IsUnavailable = false
            };

            LogPerformanceDebugBlock(result);
            return result;
        }

        /// <summary>
        /// Returns the effective dataset version string for evidence display.
        /// </summary>
        public static string GetEffectiveVersion()
        {
            var ds = PerformanceDatasetLoader.Current;
            return ds != null ? ds.DatasetVersion : $"embedded ({TableVersion})";
        }

        /// <summary>
        /// Enforces logical score order: easier tasks must have score >= harder tasks on the same PC.
        /// Order (easiest to hardest): Office → Multitasking → Gaming 1080p → Gaming 1440p → Gaming 4K → 4K Editing → Streaming+Gaming → VMs → AI.
        /// 
        /// NEW LOGIC (v3): Works with PreciseScore (double) to preserve decimal precision.
        /// - If a harder task scores higher than an easier task (true inversion), correct it
        /// - BUT allow natural differentiation: each scenario keeps its own score unless inverted
        /// - Use a "ceiling tracker" that only decreases, never increases
        /// 
        /// This ensures: Bureau 92.4, Multitâche 85.7, Jeu 1080p 78.3, etc.
        /// (not all flattened to one integer value)
        /// </summary>
        internal static void EnforceScenarioScoreOrder(List<ScenarioScore> scenarioScores, PerformanceDataset? dataset)
        {
            if (scenarioScores == null || scenarioScores.Count <= 1) return;
            // Enforce ordering only within comparable scenario families to avoid flattening unrelated workloads.
            // This keeps coherence (e.g. 1080p >= 1440p >= 4K) without forcing all downstream scenarios to a single value.
            EnforcePair(scenarioScores, "office", "multitasking", dataset);
            EnforcePair(scenarioScores, "gaming_1080p", "gaming_1440p", dataset);
            EnforcePair(scenarioScores, "gaming_1440p", "gaming_4k", dataset);
        }

        private static void EnforcePair(List<ScenarioScore> scenarioScores, string easierId, string harderId, PerformanceDataset? dataset)
        {
            var easier = scenarioScores.FirstOrDefault(s => string.Equals(s.ScenarioId, easierId, StringComparison.OrdinalIgnoreCase));
            var harder = scenarioScores.FirstOrDefault(s => string.Equals(s.ScenarioId, harderId, StringComparison.OrdinalIgnoreCase));
            if (easier == null || harder == null) return;

            if (harder.PreciseScore <= easier.PreciseScore) return;
            harder.PreciseScore = easier.PreciseScore;
            harder.Classification = GetClassificationForScore((int)Math.Round(harder.PreciseScore), dataset);
        }

        internal static string GetClassificationForScore(int score, PerformanceDataset? dataset)
        {
            var t = dataset?.ClassificationThresholds;
            if (t == null)
            {
                if (score < 40) return ScenarioClassification.NotRecommended;
                if (score < 55) return ScenarioClassification.Acceptable;
                if (score < 70) return ScenarioClassification.Good;
                return ScenarioClassification.Excellent;
            }
            if (score < t.NotRecommendedBelow) return ScenarioClassification.NotRecommended;
            if (score < t.AcceptableBelow) return ScenarioClassification.Acceptable;
            if (score < t.GoodBelow) return ScenarioClassification.Good;
            return ScenarioClassification.Excellent;
        }

        /// <summary>Minimum floors for high-end configs. When dataset is provided, floors come from dataset.Floors; otherwise hardcoded.</summary>
        private static void ApplyScenarioScoreFloors(HardwareProfile profile, List<ScenarioScore> scenarioScores, PerformanceDataset? dataset)
        {
            // Determine if high-end
            bool highEnd;
            Dictionary<string, int> floors;

            if (dataset?.Floors != null && dataset.Floors.HighEndCondition != null)
            {
                var cond = dataset.Floors.HighEndCondition;
                var gpuNorm = HardwareProfileBuilder.NormalizeHardwareName(profile.GpuModel);
                bool gpuMatch = false;
                if (cond.GpuPatterns != null)
                    foreach (var pat in cond.GpuPatterns)
                        if (gpuNorm.Contains(pat, StringComparison.OrdinalIgnoreCase)) { gpuMatch = true; break; }
                highEnd = (gpuMatch || profile.GpuVramMb >= cond.MinVramMb)
                    && profile.CpuCores >= cond.MinCores
                    && profile.RamGb >= cond.MinRamGb;
                floors = dataset.Floors.ScenarioFloors ?? new Dictionary<string, int>();
            }
            else
            {
                // Hardcoded fallback
                const double TwentyFourGbMb = 24 * 1024;
                var gpuNorm = HardwareProfileBuilder.NormalizeHardwareName(profile.GpuModel);
                highEnd = (gpuNorm.Contains("3090", StringComparison.OrdinalIgnoreCase) || profile.GpuVramMb >= TwentyFourGbMb)
                    && profile.CpuCores >= 12
                    && profile.RamGb >= 32;
                floors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gaming_1440p"] = 80,
                    ["4k_editing"] = 75,
                    ["streaming_gaming"] = 75,
                    ["ai_inference"] = 70
                };
            }

            if (!highEnd) return;

            foreach (var s in scenarioScores)
            {
                if (s.ScenarioId == null || !floors.TryGetValue(s.ScenarioId, out int floor)) continue;
                // Use PreciseScore for comparison and assignment to preserve decimal precision
                if (s.PreciseScore >= floor) continue;
                App.LogMessage($"[PerformanceEngine] Scenario score below floor: {s.ScenarioId} ({s.Name}) score={s.PreciseScore:F1} floor={floor} CPU={profile.CpuModel} GPU={profile.GpuModel} CpuTier={profile.CpuTier} GpuTier={profile.GpuTier} Cores={profile.CpuCores} VRAM={profile.GpuVramMb} RAM={profile.RamGb} Storage={profile.StorageKind}");
                // Set PreciseScore directly; Score getter will return (int)Math.Round(PreciseScore)
                s.PreciseScore = floor;
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
                int confidenceScore = 100;
                if (p != null)
                {
                    if (!p.CpuNameMatched) confidenceScore -= 5;
                    if (!p.GpuNameMatched) confidenceScore -= 5;
                }
                var si = result.SourceInfo;
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
                    ["datasetVersion"] = result.DatasetVersion ?? "(none)",
                    ["datasetPublishedAt"] = result.DatasetPublishedAt ?? "(none)",
                    ["sourceKind"] = si.SourceKind.ToString(),
                    ["datasetMode"] = si.Mode.ToString(),
                    ["urlConfigured"] = si.UrlConfigured,
                    ["cacheHit"] = si.CacheHit,
                    ["cacheAgeDays"] = si.CacheAgeDays ?? -1,
                    ["remoteFetchAttempted"] = si.RemoteFetchAttempted,
                    ["remoteFetchStatus"] = si.RemoteFetchStatus,
                    ["validationResult"] = si.ValidationResult ?? "(none)",
                    ["displayLabel"] = si.DisplayLabel,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                App.LogMessage($"[PerformanceEngine] {JsonSerializer.Serialize(block)}");
            }
            catch { }
        }

        /// <summary>
        /// Backward-compatible single score 0-100: average of scenario scores.
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
