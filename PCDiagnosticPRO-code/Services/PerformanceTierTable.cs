using System;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Deterministic tier mapping table for CPU, GPU, RAM, Storage.
    /// All thresholds are documented; no magic numbers. Used offline only.
    /// </summary>
    public static class PerformanceTierTable
    {
        #region Tier labels (public constants)

        public const string TierEntry = "Entry";
        public const string TierMidRange = "Mid-range";
        public const string TierUpperMid = "Upper Mid";
        public const string TierHighEnd = "High-end";
        public const string TierWorkstation = "Workstation";

        #endregion

        #region CPU tier

        // Primary: core/thread count. Entry &lt;4, Mid 6-8, High 12+
        private const int CpuCoresEntryMax = 4;
        private const int CpuCoresMidMin = 6;
        private const int CpuCoresMidMax = 8;
        private const int CpuCoresHighMin = 12;

        /// <summary>
        /// Resolve CPU tier from cores, threads, and optional model name. Uses normalized name for matching.
        /// Returns (tier, nameMatched). If name present but no pattern matches, use heuristic and nameMatched=false.
        /// </summary>
        public static (string Tier, bool NameMatched) ResolveCpuTier(string? name, int cores, int threads)
        {
            int effectiveThreads = threads > 0 ? threads : (cores > 0 ? cores * 2 : 0);
            int effectiveCores = cores > 0 ? cores : (threads > 0 ? (threads + 1) / 2 : 0);
            var n = HardwareProfileBuilder.NormalizeHardwareName(name);
            var nLower = n.ToLowerInvariant();

            if (!string.IsNullOrEmpty(n))
            {
                if (nLower.Contains("ryzen 9") || nLower.Contains("core i9") || nLower.Contains("xeon"))
                    return (TierHighEnd, true);
                if (nLower.Contains("ryzen 7") || nLower.Contains("core i7"))
                    return (TierUpperMid, true);
                if (nLower.Contains("ryzen 5") || nLower.Contains("core i5"))
                    return (TierMidRange, true);
                if (nLower.Contains("ryzen 3") || nLower.Contains("core i3") || nLower.Contains("pentium"))
                    return (TierEntry, true);
                // Name present but no dataset match → heuristic (do not default to Entry silently)
                if (effectiveCores >= CpuCoresHighMin || effectiveThreads >= 24) return (TierHighEnd, false);
                if (effectiveCores >= 10 || effectiveThreads >= 16) return (TierUpperMid, false);
                if (effectiveCores >= CpuCoresMidMin && effectiveCores <= CpuCoresMidMax) return (TierMidRange, false);
                if (effectiveCores >= 4 || effectiveThreads >= 4) return (TierEntry, false);
                return (TierEntry, false);
            }

            if (effectiveCores >= CpuCoresHighMin || effectiveThreads >= 24) return (TierHighEnd, true);
            if (effectiveCores >= 10 || effectiveThreads >= 16) return (TierUpperMid, true);
            if (effectiveCores >= CpuCoresMidMin && effectiveCores <= CpuCoresMidMax) return (TierMidRange, true);
            if (effectiveCores >= 4 || effectiveThreads >= 4) return (TierEntry, true);
            return (TierEntry, true);
        }

        #endregion

        #region GPU tier

        // VRAM thresholds (MB): &lt;2G Entry, 2-4 Mid, 4-8 Upper Mid, 8+ High
        private const double GpuVramEntryMaxMb = 2048;
        private const double GpuVramMidMinMb = 2048;
        private const double GpuVramMidMaxMb = 4096;
        private const double GpuVramUpperMidMinMb = 4096;
        private const double GpuVramUpperMidMaxMb = 8192;
        private const double GpuVramHighMinMb = 8192;

        /// <summary>
        /// Resolve GPU tier from VRAM (MB) and optional model name. Uses normalized name for matching.
        /// Returns (tier, nameMatched). Known high-end (e.g. 3090) cannot be Entry; defensive guard for anomaly.
        /// </summary>
        public static (string Tier, bool NameMatched) ResolveGpuTier(string? name, double vramMb)
        {
            var n = HardwareProfileBuilder.NormalizeHardwareName(name);
            var nLower = n.ToLowerInvariant();

            if (!string.IsNullOrEmpty(n))
            {
                // Explicit high-end: 3090, 4080, 4090, etc.
                if (nLower.Contains("3090") || nLower.Contains("4080") || nLower.Contains("4090") || nLower.Contains("rtx 40") || nLower.Contains("rx 7"))
                    return (TierHighEnd, true);
                if (nLower.Contains("rtx 30") || nLower.Contains("rx 6")) return (TierUpperMid, true);
                if (nLower.Contains("gtx 16") || nLower.Contains("rx 5")) return (TierMidRange, true);
                if (nLower.Contains("uhd") || nLower.Contains("iris") || nLower.Contains("vega")) return (TierEntry, true);
                // Name present but no dataset match → heuristic (do not default to Entry for 12GB+ VRAM)
                if (vramMb >= GpuVramHighMinMb) return (TierHighEnd, false);
                if (vramMb >= GpuVramUpperMidMinMb) return (TierUpperMid, false);
                if (vramMb >= GpuVramMidMinMb) return (TierMidRange, false);
                if (vramMb >= 1024) return (TierEntry, false);
                return (TierEntry, false);
            }

            if (vramMb >= GpuVramHighMinMb) return (TierHighEnd, true);
            if (vramMb >= GpuVramUpperMidMinMb) return (TierUpperMid, true);
            if (vramMb >= GpuVramMidMinMb) return (TierMidRange, true);
            if (vramMb >= 1024) return (TierEntry, true);
            return (TierEntry, true);
        }

        #endregion

        #region RAM tier

        // 8GB = Entry, 16GB = Mid, 32GB+ = High (comfortable 1080p gaming minimum often cited as 16GB)
        private const double RamEntryMaxGb = 8;
        private const double RamMidMinGb = 16;
        private const double RamHighMinGb = 32;

        /// <summary>
        /// Resolve RAM tier from total GB. 8GB → Entry, 16GB → Mid-range, 32GB+ → High-end.
        /// </summary>
        public static string ResolveRamTier(double ramGb)
        {
            if (ramGb >= RamHighMinGb) return TierHighEnd;
            if (ramGb >= RamMidMinGb) return TierMidRange;
            if (ramGb >= RamEntryMaxGb) return TierEntry;
            if (ramGb >= 4) return TierEntry;
            return TierEntry;
        }

        #endregion

        #region Storage tier

        /// <summary>
        /// Storage kind constants for scoring.
        /// </summary>
        public const string StorageHdd = "HDD";
        public const string StorageSataSsd = "SATA_SSD";
        public const string StorageNvme = "NVMe";

        /// <summary>
        /// Resolve storage tier label. HDD → Entry, SATA SSD → Mid-range, NVMe → High-end.
        /// </summary>
        public static string ResolveStorageTier(string storageKind)
        {
            return storageKind switch
            {
                StorageNvme => TierHighEnd,
                StorageSataSsd => TierMidRange,
                StorageHdd => TierEntry,
                _ => TierEntry
            };
        }

        #endregion

        #region Tier to numeric (for scoring)

        /// <summary>
        /// Map tier label to numeric score 0-100 for weighted averaging. Entry=20, Mid=40, Upper Mid=60, High=80, Workstation=100.
        /// </summary>
        public static int TierToScore(string tier)
        {
            return tier switch
            {
                TierWorkstation => 100,
                TierHighEnd => 80,
                TierUpperMid => 60,
                TierMidRange => 40,
                TierEntry => 20,
                _ => 20
            };
        }

        /// <summary>
        /// Compare tier order for scenario logic (higher = better). Entry=1, Mid=2, Upper Mid=3, High=4, Workstation=5.
        /// </summary>
        public static int TierOrder(string tier)
        {
            return tier switch
            {
                TierWorkstation => 5,
                TierHighEnd => 4,
                TierUpperMid => 3,
                TierMidRange => 2,
                TierEntry => 1,
                _ => 1
            };
        }

        #endregion
    }
}
