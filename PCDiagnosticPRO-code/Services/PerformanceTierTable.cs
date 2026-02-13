using System;
using System.Collections.Generic;
using System.Linq;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Deterministic tier mapping table for CPU, GPU, RAM, Storage.
    /// When a PerformanceDataset is provided, patterns and thresholds are read from it.
    /// When dataset is null, the original hardcoded constants are used as embedded defaults.
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

        #region Tier order ↔ label helpers

        /// <summary>Convert tier order (1-5) to tier label.</summary>
        public static string TierOrderToLabel(int order)
        {
            return order switch
            {
                5 => TierWorkstation,
                4 => TierHighEnd,
                3 => TierUpperMid,
                2 => TierMidRange,
                _ => TierEntry
            };
        }

        #endregion

        #region CPU tier — hardcoded defaults

        private const int CpuCoresEntryMax = 4;
        private const int CpuCoresMidMin = 6;
        private const int CpuCoresMidMax = 8;
        private const int CpuCoresHighMin = 12;

        #endregion

        #region GPU tier — hardcoded defaults

        private const double GpuVramEntryMaxMb = 2048;
        private const double GpuVramMidMinMb = 2048;
        private const double GpuVramMidMaxMb = 4096;
        private const double GpuVramUpperMidMinMb = 4096;
        private const double GpuVramUpperMidMaxMb = 8192;
        private const double GpuVramHighMinMb = 8192;

        #endregion

        #region RAM tier — hardcoded defaults

        private const double RamEntryMaxGb = 8;
        private const double RamMidMinGb = 16;
        private const double RamHighMinGb = 32;

        #endregion

        #region Storage constants

        public const string StorageHdd = "HDD";
        public const string StorageSataSsd = "SATA_SSD";
        public const string StorageNvme = "NVMe";

        #endregion

        #region CPU tier resolution

        /// <summary>
        /// Resolve CPU tier from cores, threads, and optional model name.
        /// Uses dataset patterns/thresholds if provided; falls back to hardcoded defaults.
        /// </summary>
        public static (string Tier, bool NameMatched) ResolveCpuTier(string? name, int cores, int threads, PerformanceDataset? dataset = null)
        {
            if (dataset != null)
                return ResolveCpuTierFromDataset(name, cores, threads, dataset);
            return ResolveCpuTierHardcoded(name, cores, threads);
        }

        private static (string Tier, bool NameMatched) ResolveCpuTierFromDataset(string? name, int cores, int threads, PerformanceDataset ds)
        {
            int effectiveThreads = threads > 0 ? threads : (cores > 0 ? cores * 2 : 0);
            int effectiveCores = cores > 0 ? cores : (threads > 0 ? (threads + 1) / 2 : 0);
            var n = HardwareProfileBuilder.NormalizeHardwareName(name);
            var nLower = n.ToLowerInvariant();

            if (!string.IsNullOrEmpty(n))
            {
                // Pattern match from dataset
                foreach (var p in ds.CpuPatterns)
                {
                    if (nLower.Contains(p.Pattern.ToLowerInvariant()))
                        return (TierOrderToLabel(p.TierOrder), true);
                }

                // No pattern matched → heuristic from dataset rules
                var h = ds.CpuHeuristicRules;
                if (effectiveCores >= h.HighEndMinCores || effectiveThreads >= h.HighEndMinThreads) return (TierHighEnd, false);
                if (effectiveCores >= h.UpperMidMinCores || effectiveThreads >= h.UpperMidMinThreads) return (TierUpperMid, false);
                if (effectiveCores >= h.MidRangeMinCores && effectiveCores <= h.MidRangeMaxCores) return (TierMidRange, false);
                if (effectiveCores >= h.EntryMinCores || effectiveThreads >= h.EntryMinThreads) return (TierEntry, false);
                return (TierEntry, false);
            }

            // No name — pure heuristic from dataset
            var hr = ds.CpuHeuristicRules;
            if (effectiveCores >= hr.HighEndMinCores || effectiveThreads >= hr.HighEndMinThreads) return (TierHighEnd, true);
            if (effectiveCores >= hr.UpperMidMinCores || effectiveThreads >= hr.UpperMidMinThreads) return (TierUpperMid, true);
            if (effectiveCores >= hr.MidRangeMinCores && effectiveCores <= hr.MidRangeMaxCores) return (TierMidRange, true);
            if (effectiveCores >= hr.EntryMinCores || effectiveThreads >= hr.EntryMinThreads) return (TierEntry, true);
            return (TierEntry, true);
        }

        private static (string Tier, bool NameMatched) ResolveCpuTierHardcoded(string? name, int cores, int threads)
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

        #region GPU tier resolution

        /// <summary>
        /// Resolve GPU tier from VRAM (MB) and optional model name.
        /// Uses dataset patterns/thresholds if provided; falls back to hardcoded defaults.
        /// </summary>
        public static (string Tier, bool NameMatched) ResolveGpuTier(string? name, double vramMb, PerformanceDataset? dataset = null)
        {
            if (dataset != null)
                return ResolveGpuTierFromDataset(name, vramMb, dataset);
            return ResolveGpuTierHardcoded(name, vramMb);
        }

        private static (string Tier, bool NameMatched) ResolveGpuTierFromDataset(string? name, double vramMb, PerformanceDataset ds)
        {
            var n = HardwareProfileBuilder.NormalizeHardwareName(name);
            var nLower = n.ToLowerInvariant();

            if (!string.IsNullOrEmpty(n))
            {
                foreach (var p in ds.GpuPatterns)
                {
                    if (nLower.Contains(p.Pattern.ToLowerInvariant()))
                        return (TierOrderToLabel(p.TierOrder), true);
                }

                // No pattern matched → VRAM heuristic from dataset
                var t = ds.GpuVramThresholds;
                if (vramMb >= t.HighEndMinMb) return (TierHighEnd, false);
                if (vramMb >= t.UpperMidMinMb) return (TierUpperMid, false);
                if (vramMb >= t.MidRangeMinMb) return (TierMidRange, false);
                if (vramMb >= t.EntryMinMb) return (TierEntry, false);
                return (TierEntry, false);
            }

            var th = ds.GpuVramThresholds;
            if (vramMb >= th.HighEndMinMb) return (TierHighEnd, true);
            if (vramMb >= th.UpperMidMinMb) return (TierUpperMid, true);
            if (vramMb >= th.MidRangeMinMb) return (TierMidRange, true);
            if (vramMb >= th.EntryMinMb) return (TierEntry, true);
            return (TierEntry, true);
        }

        private static (string Tier, bool NameMatched) ResolveGpuTierHardcoded(string? name, double vramMb)
        {
            var n = HardwareProfileBuilder.NormalizeHardwareName(name);
            var nLower = n.ToLowerInvariant();

            if (!string.IsNullOrEmpty(n))
            {
                if (nLower.Contains("3090") || nLower.Contains("4080") || nLower.Contains("4090") || nLower.Contains("rtx 40") || nLower.Contains("rx 7"))
                    return (TierHighEnd, true);
                if (nLower.Contains("rtx 30") || nLower.Contains("rx 6")) return (TierUpperMid, true);
                if (nLower.Contains("gtx 16") || nLower.Contains("rx 5")) return (TierMidRange, true);
                if (nLower.Contains("uhd") || nLower.Contains("iris") || nLower.Contains("vega")) return (TierEntry, true);
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

        #region RAM tier resolution

        /// <summary>
        /// Resolve RAM tier from total GB.
        /// Uses dataset thresholds if provided; falls back to hardcoded defaults.
        /// </summary>
        public static string ResolveRamTier(double ramGb, PerformanceDataset? dataset = null)
        {
            if (dataset != null)
            {
                var r = dataset.RamTierRules;
                if (ramGb >= r.HighEndMinGb) return TierHighEnd;
                if (ramGb >= r.MidRangeMinGb) return TierMidRange;
                if (ramGb >= r.EntryMinGb) return TierEntry;
                if (ramGb >= r.EntryFloorGb) return TierEntry;
                return TierEntry;
            }

            if (ramGb >= RamHighMinGb) return TierHighEnd;
            if (ramGb >= RamMidMinGb) return TierMidRange;
            if (ramGb >= RamEntryMaxGb) return TierEntry;
            if (ramGb >= 4) return TierEntry;
            return TierEntry;
        }

        #endregion

        #region Storage tier resolution

        /// <summary>
        /// Resolve storage tier label. HDD → Entry, SATA SSD → Mid-range, NVMe → High-end.
        /// Dataset parameter accepted for API consistency but storage mapping is fixed.
        /// </summary>
        public static string ResolveStorageTier(string storageKind, PerformanceDataset? dataset = null)
        {
            // Storage mapping is simple and doesn't change with dataset;
            // the dataset StorageTierRules exist for documentation/validation completeness.
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
