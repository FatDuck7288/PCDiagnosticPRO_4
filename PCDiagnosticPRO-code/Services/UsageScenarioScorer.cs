using System.Collections.Generic;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Scores 8 usage scenarios 0-100 and assigns classification (Not Recommended / Acceptable / Good / Excellent).
    /// All formulas are deterministic and documented. No external API.
    /// </summary>
    public static class UsageScenarioScorer
    {
        // Classification bands: Not Recommended &lt;40, Acceptable 40-55, Good 56-70, Excellent &gt;70
        private const int ThresholdNotRecommended = 40;
        private const int ThresholdAcceptable = 55;
        private const int ThresholdGood = 70;

        public static List<ScenarioScore> Score(HardwareProfile profile)
        {
            var list = new List<ScenarioScore>
            {
                ScoreOfficeBrowsing(profile),
                ScoreMultitasking(profile),
                ScoreGaming1080p(profile),
                ScoreGaming1440p(profile),
                Score4KVideoEditing(profile),
                ScoreStreamingGaming(profile),
                ScoreVirtualMachines(profile),
                ScoreAIBasicInference(profile)
            };
            return list;
        }

        private static string Classify(int score)
        {
            if (score < ThresholdNotRecommended) return ScenarioClassification.NotRecommended;
            if (score < ThresholdAcceptable) return ScenarioClassification.Acceptable;
            if (score < ThresholdGood) return ScenarioClassification.Good;
            return ScenarioClassification.Excellent;
        }

        /// <summary>Office/Browsing: CPU ≥ Entry, RAM ≥ 8GB; HDD penalized.</summary>
        private static ScenarioScore ScoreOfficeBrowsing(HardwareProfile p)
        {
            int score = 50;
            if (PerformanceTierTable.TierOrder(p.CpuTier) >= 1) score += 25;
            if (p.RamGb >= 8) score += 15;
            if (p.RamGb >= 16) score += 5;
            if (p.StorageKind == PerformanceTierTable.StorageHdd) score -= 15;
            else if (p.StorageKind == PerformanceTierTable.StorageNvme) score += 5;
            return new ScenarioScore
            {
                ScenarioId = "office",
                Name = "Office / Browsing",
                Score = Clamp(score),
                Classification = Classify(Clamp(score))
            };
        }

        /// <summary>Multitasking: CPU Mid preferred, RAM ≥ 16GB.</summary>
        private static ScenarioScore ScoreMultitasking(HardwareProfile p)
        {
            int score = 30;
            if (PerformanceTierTable.TierOrder(p.CpuTier) >= 2) score += 25;
            if (PerformanceTierTable.TierOrder(p.CpuTier) >= 3) score += 15;
            if (p.RamGb >= 16) score += 25;
            if (p.RamGb >= 32) score += 5;
            return new ScenarioScore { ScenarioId = "multitasking", Name = "Multitasking", Score = Clamp(score), Classification = Classify(Clamp(score)) };
        }

        /// <summary>1080p Gaming: GPU ≥ Mid, VRAM ≥ 6GB, RAM ≥ 16GB. Formula: 40 base + 30 GPU + 20 VRAM + 10 RAM.</summary>
        private static ScenarioScore ScoreGaming1080p(HardwareProfile p)
        {
            int score = 40;
            if (PerformanceTierTable.TierOrder(p.GpuTier) >= 2) score += 30;
            else if (PerformanceTierTable.TierOrder(p.GpuTier) >= 1) score += 15;
            if (p.GpuVramMb >= 6144) score += 20; // 6GB
            else if (p.GpuVramMb >= 4096) score += 10;
            if (p.RamGb >= 16) score += 10;
            else if (p.RamGb >= 8) score += 5;
            return new ScenarioScore { ScenarioId = "gaming_1080p", Name = "Gaming (1080p)", Score = Clamp(score), Classification = Classify(Clamp(score)) };
        }

        /// <summary>1440p Gaming: GPU Upper Mid/High, VRAM ≥ 8GB, RAM ≥ 16GB.</summary>
        private static ScenarioScore ScoreGaming1440p(HardwareProfile p)
        {
            int score = 20;
            if (PerformanceTierTable.TierOrder(p.GpuTier) >= 4) score += 40;
            else if (PerformanceTierTable.TierOrder(p.GpuTier) >= 3) score += 30;
            else if (PerformanceTierTable.TierOrder(p.GpuTier) >= 2) score += 15;
            if (p.GpuVramMb >= 8192) score += 25;
            else if (p.GpuVramMb >= 6144) score += 15;
            if (p.RamGb >= 16) score += 15;
            return new ScenarioScore { ScenarioId = "gaming_1440p", Name = "Gaming (1440p)", Score = Clamp(score), Classification = Classify(Clamp(score)) };
        }

        /// <summary>4K Video Editing: CPU High, RAM ≥ 32GB, GPU capable, fast storage.</summary>
        private static ScenarioScore Score4KVideoEditing(HardwareProfile p)
        {
            int score = 0;
            if (PerformanceTierTable.TierOrder(p.CpuTier) >= 4) score += 30;
            else if (PerformanceTierTable.TierOrder(p.CpuTier) >= 3) score += 20;
            if (p.RamGb >= 32) score += 30;
            else if (p.RamGb >= 16) score += 15;
            if (PerformanceTierTable.TierOrder(p.GpuTier) >= 2) score += 20;
            if (p.StorageKind == PerformanceTierTable.StorageNvme) score += 20;
            else if (p.StorageKind == PerformanceTierTable.StorageSataSsd) score += 10;
            return new ScenarioScore { ScenarioId = "4k_editing", Name = "4K Video Editing", Score = Clamp(score), Classification = Classify(Clamp(score)) };
        }

        /// <summary>Streaming + Gaming: CPU + GPU both strong, RAM ≥ 16GB.</summary>
        private static ScenarioScore ScoreStreamingGaming(HardwareProfile p)
        {
            int score = 25;
            if (PerformanceTierTable.TierOrder(p.CpuTier) >= 3) score += 25;
            else if (PerformanceTierTable.TierOrder(p.CpuTier) >= 2) score += 15;
            if (PerformanceTierTable.TierOrder(p.GpuTier) >= 3) score += 25;
            else if (PerformanceTierTable.TierOrder(p.GpuTier) >= 2) score += 15;
            if (p.RamGb >= 16) score += 25;
            return new ScenarioScore { ScenarioId = "streaming_gaming", Name = "Streaming + Gaming", Score = Clamp(score), Classification = Classify(Clamp(score)) };
        }

        /// <summary>Virtual Machines: CPU cores/threads, RAM ≥ 16GB (32GB for multiple VMs).</summary>
        private static ScenarioScore ScoreVirtualMachines(HardwareProfile p)
        {
            int score = 20;
            if (p.CpuThreads >= 16) score += 35;
            else if (p.CpuThreads >= 8) score += 25;
            else if (p.CpuThreads >= 4) score += 15;
            if (p.RamGb >= 32) score += 35;
            else if (p.RamGb >= 16) score += 25;
            else if (p.RamGb >= 8) score += 10;
            return new ScenarioScore { ScenarioId = "vms", Name = "Virtual Machines", Score = Clamp(score), Classification = Classify(Clamp(score)) };
        }

        /// <summary>AI (basic inference): GPU VRAM ≥ 6GB, RAM ≥ 16GB.</summary>
        private static ScenarioScore ScoreAIBasicInference(HardwareProfile p)
        {
            int score = 20;
            if (p.GpuVramMb >= 8192) score += 40;
            else if (p.GpuVramMb >= 6144) score += 30;
            else if (p.GpuVramMb >= 4096) score += 20;
            if (p.RamGb >= 32) score += 20;
            else if (p.RamGb >= 16) score += 15;
            return new ScenarioScore { ScenarioId = "ai_inference", Name = "AI (basic inference)", Score = Clamp(score), Classification = Classify(Clamp(score)) };
        }

        private static int Clamp(int score)
        {
            if (score < 0) return 0;
            if (score > 100) return 100;
            return score;
        }
    }
}
