using System.Linq;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Builds final verdict: system category (Entry-Level to Workstation Grade) and realistic expectation summary.
    /// Technical tone, no marketing. Deterministic from profile, scenario scores, and bottleneck.
    /// </summary>
    public static class PerformanceVerdictBuilder
    {
        public static VerdictSummary Build(HardwareProfile profile, System.Collections.Generic.List<ScenarioScore> scenarioScores, BottleneckResult bottleneck)
        {
            string category = ResolveSystemCategory(profile);
            string summary = BuildRealisticSummary(profile, scenarioScores, bottleneck, category);
            return new VerdictSummary { Category = category, RealisticExpectationSummary = summary };
        }

        private static string ResolveSystemCategory(HardwareProfile profile)
        {
            int cpuOrder = PerformanceTierTable.TierOrder(profile.CpuTier);
            int gpuOrder = PerformanceTierTable.TierOrder(profile.GpuTier);
            int ramOrder = PerformanceTierTable.TierOrder(profile.RamTier);
            int storageOrder = PerformanceTierTable.TierOrder(profile.StorageTier);

            // Workstation: 12+ cores equivalent, 32GB+ RAM, strong GPU
            if (profile.CpuThreads >= 12 && profile.RamGb >= 32 && gpuOrder >= 3)
                return SystemCategory.WorkstationGrade;

            // High-End: most tiers High or Upper Mid
            if (cpuOrder >= 4 && gpuOrder >= 4 && ramOrder >= 3)
                return SystemCategory.HighEnd;
            if (cpuOrder >= 3 && gpuOrder >= 3 && ramOrder >= 2)
                return SystemCategory.HighEnd;

            // Upper Mid: mix of Mid and Upper Mid
            if (cpuOrder >= 3 && (gpuOrder >= 3 || ramOrder >= 3))
                return SystemCategory.UpperMid;
            if (gpuOrder >= 3 && ramOrder >= 2)
                return SystemCategory.UpperMid;

            // Mid-Range: at least two components Mid or better
            if (cpuOrder >= 2 && gpuOrder >= 2) return SystemCategory.MidRange;
            if (cpuOrder >= 2 && ramOrder >= 2) return SystemCategory.MidRange;
            if (gpuOrder >= 2 && ramOrder >= 2) return SystemCategory.MidRange;
            if (cpuOrder >= 2 || gpuOrder >= 2 || ramOrder >= 2)
                return SystemCategory.MidRange;

            // Entry-Level
            return SystemCategory.EntryLevel;
        }

        private static string BuildRealisticSummary(HardwareProfile profile, System.Collections.Generic.List<ScenarioScore> scores, BottleneckResult bottleneck, string category)
        {
            var excellent = scores.Where(s => s.Classification == ScenarioClassification.Excellent).ToList();
            var good = scores.Where(s => s.Classification == ScenarioClassification.Good).ToList();
            var acceptable = scores.Where(s => s.Classification == ScenarioClassification.Acceptable).ToList();
            var notRec = scores.Where(s => s.Classification == ScenarioClassification.NotRecommended).ToList();

            string canDo = "";
            if (excellent.Count > 0)
                canDo = string.Join(", ", excellent.Select(s => s.Name.ToLowerInvariant()));
            if (good.Count > 0)
                canDo += (canDo.Length > 0 ? "; " : "") + string.Join(", ", good.Select(s => s.Name.ToLowerInvariant())) + " (good)";
            if (string.IsNullOrEmpty(canDo)) canDo = "light office and browsing";

            string limits = "";
            if (notRec.Count > 0)
                limits = string.Join(", ", notRec.Select(s => s.Name.ToLowerInvariant()));
            if (acceptable.Count > 0)
                limits += (limits.Length > 0 ? "; " : "") + string.Join(", ", acceptable.Select(s => s.Name.ToLowerInvariant())) + " (acceptable only)";
            if (string.IsNullOrEmpty(limits)) limits = "no major limitations identified for common use.";

            string bottleneckLine = "";
            if (bottleneck.PrimaryLimitingFactor != BottleneckAnalyzer.FactorNone && bottleneck.UpgradePriorityRank.Count > 0)
            {
                var first = bottleneck.UpgradePriorityRank[0];
                bottleneckLine = $" Primary limiting factor: {bottleneck.PrimaryLimitingFactor}. Highest impact upgrade: {first.Component} — {first.Reason}";
            }

            return $"System category: {category}. Capable of: {canDo}. Limits or reduced performance in: {limits}.{bottleneckLine}";
        }
    }
}
