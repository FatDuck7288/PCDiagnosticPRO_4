using System.Collections.Generic;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Identifies primary limiting factor and upgrade priority (1-3). Deterministic rules, no external API.
    /// </summary>
    public static class BottleneckAnalyzer
    {
        public const string FactorNone = "None significant";
        public const string FactorCpu = "CPU";
        public const string FactorGpu = "GPU";
        public const string FactorRam = "RAM";
        public const string FactorStorage = "Storage";

        public static BottleneckResult Analyze(HardwareProfile profile)
        {
            var result = new BottleneckResult();
            int cpuOrder = PerformanceTierTable.TierOrder(profile.CpuTier);
            int gpuOrder = PerformanceTierTable.TierOrder(profile.GpuTier);
            int ramOrder = PerformanceTierTable.TierOrder(profile.RamTier);
            int storageOrder = PerformanceTierTable.TierOrder(profile.StorageTier);

            // HDD is a global bottleneck when rest is strong
            if (profile.StorageKind == PerformanceTierTable.StorageHdd && (cpuOrder >= 2 || gpuOrder >= 2))
            {
                result.PrimaryLimitingFactor = FactorStorage;
                result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 1, Component = FactorStorage, Reason = "HDD limits boot and load times; SSD or NVMe will have highest impact." });
                if (ramOrder <= 1 && profile.RamGb < 16)
                    result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 2, Component = FactorRam, Reason = "16GB+ RAM improves multitasking and gaming." });
                else if (gpuOrder <= 2 && profile.GpuVramMb < 6144)
                    result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 2, Component = FactorGpu, Reason = "GPU with 6GB+ VRAM improves gaming and creation." });
                if (result.UpgradePriorityRank.Count < 3 && cpuOrder <= 1)
                    result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 3, Component = FactorCpu, Reason = "More cores/threads help multitasking and streaming." });
                return result;
            }

            // Strong GPU + 8GB RAM → RAM bottleneck
            if (gpuOrder >= 3 && profile.RamGb > 0 && profile.RamGb < 16)
            {
                result.PrimaryLimitingFactor = FactorRam;
                result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 1, Component = FactorRam, Reason = "GPU is underutilized; 16GB+ RAM recommended for gaming and multitasking." });
                if (profile.StorageKind == PerformanceTierTable.StorageHdd)
                    result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 2, Component = FactorStorage, Reason = "SSD/NVMe improves load times and responsiveness." });
                if (cpuOrder <= 2)
                    result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 3, Component = FactorCpu, Reason = "Faster CPU helps in CPU-bound games and streaming." });
                return result;
            }

            // NVMe + strong CPU + weak GPU → GPU bottleneck
            if (storageOrder >= 3 && cpuOrder >= 2 && gpuOrder <= 2)
            {
                result.PrimaryLimitingFactor = FactorGpu;
                result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 1, Component = FactorGpu, Reason = "GPU is the limiting factor for gaming and GPU-accelerated workloads." });
                if (profile.RamGb < 16)
                    result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 2, Component = FactorRam, Reason = "16GB+ RAM improves stability in games and creation." });
                if (cpuOrder <= 2 && result.UpgradePriorityRank.Count < 3)
                    result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 3, Component = FactorCpu, Reason = "More cores help streaming and multitasking." });
                return result;
            }

            // Weak GPU with decent CPU/RAM
            if (gpuOrder <= 1 && (cpuOrder >= 2 || profile.RamGb >= 16))
            {
                result.PrimaryLimitingFactor = FactorGpu;
                result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 1, Component = FactorGpu, Reason = "Dedicated or stronger GPU needed for gaming and GPU workloads." });
                if (profile.RamGb < 16)
                    result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 2, Component = FactorRam, Reason = "16GB+ RAM recommended." });
                if (profile.StorageKind == PerformanceTierTable.StorageHdd)
                    result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 3, Component = FactorStorage, Reason = "SSD improves overall responsiveness." });
                return result;
            }

            // Weak CPU
            if (cpuOrder <= 1 && (gpuOrder >= 2 || profile.RamGb >= 16))
            {
                result.PrimaryLimitingFactor = FactorCpu;
                result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 1, Component = FactorCpu, Reason = "CPU limits heavy multitasking, streaming, and CPU-bound tasks." });
                if (profile.RamGb < 16)
                    result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 2, Component = FactorRam, Reason = "16GB+ RAM helps multitasking." });
                if (gpuOrder <= 2)
                    result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 3, Component = FactorGpu, Reason = "Stronger GPU improves gaming and creation." });
                return result;
            }

            // Weak RAM (other components not clearly stronger)
            if (ramOrder <= 1 && profile.RamGb > 0 && profile.RamGb < 16)
            {
                result.PrimaryLimitingFactor = FactorRam;
                result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 1, Component = FactorRam, Reason = "16GB+ RAM improves multitasking and modern applications." });
                if (profile.StorageKind == PerformanceTierTable.StorageHdd)
                    result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 2, Component = FactorStorage, Reason = "SSD reduces swap and load times." });
                if (gpuOrder <= 2)
                    result.UpgradePriorityRank.Add(new UpgradePriority { Rank = 3, Component = FactorGpu, Reason = "Dedicated GPU helps gaming and GPU workloads." });
                return result;
            }

            result.PrimaryLimitingFactor = FactorNone;
            var suggestions = new List<UpgradePriority>();
            if (cpuOrder <= 2)
                suggestions.Add(new UpgradePriority { Rank = 0, Component = FactorCpu, Reason = "Faster CPU improves demanding tasks." });
            if (gpuOrder <= 2)
                suggestions.Add(new UpgradePriority { Rank = 0, Component = FactorGpu, Reason = "Stronger GPU improves gaming and creation." });
            if (profile.RamGb < 32)
                suggestions.Add(new UpgradePriority { Rank = 0, Component = FactorRam, Reason = "32GB RAM benefits heavy multitasking and VMs." });
            int r = 1;
            foreach (var s in suggestions)
            {
                if (r > 3) break;
                result.UpgradePriorityRank.Add(new UpgradePriority { Rank = r, Component = s.Component, Reason = s.Reason });
                r++;
            }
            return result;
        }
    }
}
