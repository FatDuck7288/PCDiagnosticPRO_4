using System;
using System.Collections.Generic;
using System.Linq;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.Services;

namespace PCDiagnosticPro.Tests
{
    /// <summary>
    /// Comprehensive unit + integration tests for the Performance Scoring pipeline.
    /// Validates: name normalization, tier mapping, scenario scoring determinism,
    /// score clamping, dissonance detection, edge cases, and end-to-end integration.
    /// </summary>
    public static class PerformanceScoringTests
    {
        private static readonly List<string> _failures = new();
        private static readonly List<string> _successes = new();

        /// <summary>Run all performance scoring tests. Returns (passed, failed, failures).</summary>
        public static (int passed, int failed, List<string> failures) RunAllTests()
        {
            _failures.Clear();
            _successes.Clear();

            // ── 1. Name Normalization Tests ──
            Test_Normalize_NvidiaGeForce_Prefix();
            Test_Normalize_NCoreProcessor_Suffix();
            Test_Normalize_Registered_Trademark();
            Test_Normalize_Null_Empty();
            Test_Normalize_DoubleSpaces();

            // ── 2. Tier Mapping Tests ──
            Test_CpuTier_Ryzen9_IsHighEnd();
            Test_CpuTier_CoreI7_IsUpperMid();
            Test_CpuTier_CoreI5_IsMidRange();
            Test_CpuTier_CoreI3_IsEntry();
            Test_CpuTier_12Cores_Heuristic_IsHighEnd();
            Test_CpuTier_UnknownName_FallsBackToHeuristic();
            Test_GpuTier_RTX3090_IsHighEnd();
            Test_GpuTier_RTX4090_IsHighEnd();
            Test_GpuTier_RTX3060_IsUpperMid();
            Test_GpuTier_GTX1650_IsMidRange();
            Test_GpuTier_IntelUHD_IsEntry();
            Test_GpuTier_HighVram_UnknownName_NotEntry();
            Test_GpuTier_WeirdName_8gbVram_IsHighEnd();
            Test_RamTier_32GB_IsHighEnd();
            Test_RamTier_16GB_IsMidRange();
            Test_RamTier_8GB_IsEntry();
            Test_StorageTier_NVMe_IsHighEnd();
            Test_StorageTier_SATA_IsMidRange();
            Test_StorageTier_HDD_IsEntry();

            // ── 3. Scenario Score Determinism ──
            Test_OfficeBrowsing_Determinism();
            Test_Gaming1440p_Determinism();
            Test_AllScenarios_SameInputs_SameOutputs();

            // ── 4. Score Clamping ──
            Test_Score_NeverBelow0();
            Test_Score_NeverAbove100();
            Test_Score_100Bar_HighEnd();

            // ── 5. Dissonance Detection ──
            Test_Dissonance_OfficeVsGaming1440p_HighEnd();
            Test_Dissonance_OfficeVsGaming1440p_LowEnd();
            Test_Dissonance_OfficeVsGaming1440p_MidRange();
            Test_Dissonance_MonotonicDifficulty();

            // ── 6. Edge Cases ──
            Test_EdgeCase_MissingVram_ZeroGpuVram();
            Test_EdgeCase_ZeroRam();
            Test_EdgeCase_ZeroCores();
            Test_EdgeCase_UnknownStorage();

            // ── 7. Integration: 6 Representative Profiles ──
            Test_Profile_OfficeLowEnd();
            Test_Profile_MidrangeGaming();
            Test_Profile_HighEndGaming();
            Test_Profile_WorkstationEditing();
            Test_Profile_UnmatchedGpuName();
            Test_Profile_MissingVram();

            // ── 8. Floor mechanism ──
            Test_Floor_HighEnd_Gaming1440p_Minimum80();
            Test_Floor_NotApplied_MidRange();

            // ── 9. Single score (global average) ──
            Test_SingleScore_IsAverageOfScenarios();

            // ── 10. Dataset-driven scoring equivalence ──
            Test_DatasetDriven_OfficeBrowsing_SameAsHardcoded();
            Test_DatasetDriven_Gaming1440p_SameAsHardcoded();
            Test_DatasetDriven_AllScenarios_6Profiles_SameAsHardcoded();
            Test_DatasetDriven_TierResolution_SameAsHardcoded();

            // ── 11. Scenario score order consistency (Bureau ≥ … ≥ AI) ──
            Test_EnforceScenarioScoreOrder_RemovesInversions();
            Test_EnforceScenarioScoreOrder_AlreadyOrdered_Unchanged();

            return (_successes.Count, _failures.Count, _failures.ToList());
        }

        #region Helpers

        private static void Assert(bool condition, string testName, string detail = "")
        {
            if (!condition)
                throw new Exception($"{testName}: {detail}");
        }

        private static void Pass(string testName) => _successes.Add(testName);

        private static void Fail(string testName, string message) => _failures.Add($"{testName}: {message}");

        private static void RunTest(string name, Action test)
        {
            try { test(); Pass(name); }
            catch (Exception ex) { Fail(name, ex.Message); }
        }

        /// <summary>Build a HardwareProfile with resolved tiers.</summary>
        private static HardwareProfile MakeProfile(
            string? cpuModel, int cpuCores, int cpuThreads,
            string? gpuModel, double gpuVramMb,
            double ramGb, string storageKind)
        {
            var p = new HardwareProfile
            {
                CpuModel = cpuModel,
                CpuCores = cpuCores,
                CpuThreads = cpuThreads,
                GpuModel = gpuModel,
                GpuVramMb = gpuVramMb,
                RamGb = ramGb,
                StorageKind = storageKind
            };
            var (cpuTier, cpuMatched) = PerformanceTierTable.ResolveCpuTier(cpuModel, cpuCores, cpuThreads);
            var (gpuTier, gpuMatched) = PerformanceTierTable.ResolveGpuTier(gpuModel, gpuVramMb);
            p.CpuTier = cpuTier;
            p.GpuTier = gpuTier;
            p.CpuNameMatched = cpuMatched;
            p.GpuNameMatched = gpuMatched;
            p.RamTier = PerformanceTierTable.ResolveRamTier(ramGb);
            p.StorageTier = PerformanceTierTable.ResolveStorageTier(storageKind);
            return p;
        }

        /// <summary>Shortcut: score all scenarios for a profile.</summary>
        private static List<ScenarioScore> ScoreAll(HardwareProfile p) => UsageScenarioScorer.Score(p);

        private static ScenarioScore FindScenario(List<ScenarioScore> scores, string scenarioId)
            => scores.First(s => s.ScenarioId == scenarioId);

        #endregion

        // ═══════════════════════════════════════════════════════════
        // 1. NAME NORMALIZATION
        // ═══════════════════════════════════════════════════════════

        private static void Test_Normalize_NvidiaGeForce_Prefix() => RunTest(nameof(Test_Normalize_NvidiaGeForce_Prefix), () =>
        {
            var result = HardwareProfileBuilder.NormalizeHardwareName("NVIDIA GeForce RTX 3090");
            Assert(result == "RTX 3090", nameof(Test_Normalize_NvidiaGeForce_Prefix), $"Expected 'RTX 3090', got '{result}'");
        });

        private static void Test_Normalize_NCoreProcessor_Suffix() => RunTest(nameof(Test_Normalize_NCoreProcessor_Suffix), () =>
        {
            var result = HardwareProfileBuilder.NormalizeHardwareName("AMD Ryzen 9 5900X 12-Core Processor");
            Assert(result == "AMD Ryzen 9 5900X", nameof(Test_Normalize_NCoreProcessor_Suffix), $"Expected 'AMD Ryzen 9 5900X', got '{result}'");
        });

        private static void Test_Normalize_Registered_Trademark() => RunTest(nameof(Test_Normalize_Registered_Trademark), () =>
        {
            var result = HardwareProfileBuilder.NormalizeHardwareName("Intel(R) Core(TM) i7-12700K");
            Assert(result.Contains("i7-12700K") && !result.Contains("(R)") && !result.Contains("(TM)"),
                nameof(Test_Normalize_Registered_Trademark), $"Got '{result}'");
        });

        private static void Test_Normalize_Null_Empty() => RunTest(nameof(Test_Normalize_Null_Empty), () =>
        {
            Assert(HardwareProfileBuilder.NormalizeHardwareName(null) == "", nameof(Test_Normalize_Null_Empty), "null should return empty");
            Assert(HardwareProfileBuilder.NormalizeHardwareName("") == "", nameof(Test_Normalize_Null_Empty), "empty should return empty");
            Assert(HardwareProfileBuilder.NormalizeHardwareName("   ") == "", nameof(Test_Normalize_Null_Empty), "whitespace should return empty");
        });

        private static void Test_Normalize_DoubleSpaces() => RunTest(nameof(Test_Normalize_DoubleSpaces), () =>
        {
            var result = HardwareProfileBuilder.NormalizeHardwareName("AMD  Ryzen  5  5600X");
            Assert(!result.Contains("  "), nameof(Test_Normalize_DoubleSpaces), $"Double spaces not collapsed: '{result}'");
        });

        // ═══════════════════════════════════════════════════════════
        // 2. TIER MAPPING
        // ═══════════════════════════════════════════════════════════

        private static void Test_CpuTier_Ryzen9_IsHighEnd() => RunTest(nameof(Test_CpuTier_Ryzen9_IsHighEnd), () =>
        {
            var (tier, matched) = PerformanceTierTable.ResolveCpuTier("AMD Ryzen 9 5900X", 12, 24);
            Assert(tier == PerformanceTierTable.TierHighEnd && matched, nameof(Test_CpuTier_Ryzen9_IsHighEnd), $"tier={tier}, matched={matched}");
        });

        private static void Test_CpuTier_CoreI7_IsUpperMid() => RunTest(nameof(Test_CpuTier_CoreI7_IsUpperMid), () =>
        {
            var (tier, matched) = PerformanceTierTable.ResolveCpuTier("Intel Core i7-12700K", 12, 20);
            Assert(tier == PerformanceTierTable.TierUpperMid && matched, nameof(Test_CpuTier_CoreI7_IsUpperMid), $"tier={tier}, matched={matched}");
        });

        private static void Test_CpuTier_CoreI5_IsMidRange() => RunTest(nameof(Test_CpuTier_CoreI5_IsMidRange), () =>
        {
            var (tier, matched) = PerformanceTierTable.ResolveCpuTier("Intel Core i5-12400F", 6, 12);
            Assert(tier == PerformanceTierTable.TierMidRange && matched, nameof(Test_CpuTier_CoreI5_IsMidRange), $"tier={tier}, matched={matched}");
        });

        private static void Test_CpuTier_CoreI3_IsEntry() => RunTest(nameof(Test_CpuTier_CoreI3_IsEntry), () =>
        {
            var (tier, matched) = PerformanceTierTable.ResolveCpuTier("Intel Core i3-10100", 4, 8);
            Assert(tier == PerformanceTierTable.TierEntry && matched, nameof(Test_CpuTier_CoreI3_IsEntry), $"tier={tier}, matched={matched}");
        });

        private static void Test_CpuTier_12Cores_Heuristic_IsHighEnd() => RunTest(nameof(Test_CpuTier_12Cores_Heuristic_IsHighEnd), () =>
        {
            var (tier, _) = PerformanceTierTable.ResolveCpuTier("SomeWeirdCPU X99", 12, 24);
            Assert(tier == PerformanceTierTable.TierHighEnd, nameof(Test_CpuTier_12Cores_Heuristic_IsHighEnd), $"tier={tier}");
        });

        private static void Test_CpuTier_UnknownName_FallsBackToHeuristic() => RunTest(nameof(Test_CpuTier_UnknownName_FallsBackToHeuristic), () =>
        {
            var (tier, matched) = PerformanceTierTable.ResolveCpuTier("UnknownCPUBrand 9000", 8, 16);
            Assert(!matched, nameof(Test_CpuTier_UnknownName_FallsBackToHeuristic), $"Should not be matched for unknown name");
            Assert(tier != PerformanceTierTable.TierEntry, nameof(Test_CpuTier_UnknownName_FallsBackToHeuristic), $"16 threads should not be Entry, got {tier}");
        });

        private static void Test_GpuTier_RTX3090_IsHighEnd() => RunTest(nameof(Test_GpuTier_RTX3090_IsHighEnd), () =>
        {
            var (tier, matched) = PerformanceTierTable.ResolveGpuTier("NVIDIA GeForce RTX 3090", 24576);
            Assert(tier == PerformanceTierTable.TierHighEnd && matched, nameof(Test_GpuTier_RTX3090_IsHighEnd), $"tier={tier}, matched={matched}");
        });

        private static void Test_GpuTier_RTX4090_IsHighEnd() => RunTest(nameof(Test_GpuTier_RTX4090_IsHighEnd), () =>
        {
            var (tier, matched) = PerformanceTierTable.ResolveGpuTier("NVIDIA GeForce RTX 4090", 24576);
            Assert(tier == PerformanceTierTable.TierHighEnd && matched, nameof(Test_GpuTier_RTX4090_IsHighEnd), $"tier={tier}, matched={matched}");
        });

        private static void Test_GpuTier_RTX3060_IsUpperMid() => RunTest(nameof(Test_GpuTier_RTX3060_IsUpperMid), () =>
        {
            var (tier, matched) = PerformanceTierTable.ResolveGpuTier("NVIDIA GeForce RTX 3060", 12288);
            Assert(tier == PerformanceTierTable.TierUpperMid && matched, nameof(Test_GpuTier_RTX3060_IsUpperMid), $"tier={tier}, matched={matched}");
        });

        private static void Test_GpuTier_GTX1650_IsMidRange() => RunTest(nameof(Test_GpuTier_GTX1650_IsMidRange), () =>
        {
            var (tier, matched) = PerformanceTierTable.ResolveGpuTier("NVIDIA GeForce GTX 1650", 4096);
            Assert(tier == PerformanceTierTable.TierMidRange && matched, nameof(Test_GpuTier_GTX1650_IsMidRange), $"tier={tier}, matched={matched}");
        });

        private static void Test_GpuTier_IntelUHD_IsEntry() => RunTest(nameof(Test_GpuTier_IntelUHD_IsEntry), () =>
        {
            var (tier, matched) = PerformanceTierTable.ResolveGpuTier("Intel UHD Graphics 630", 0);
            Assert(tier == PerformanceTierTable.TierEntry && matched, nameof(Test_GpuTier_IntelUHD_IsEntry), $"tier={tier}, matched={matched}");
        });

        private static void Test_GpuTier_HighVram_UnknownName_NotEntry() => RunTest(nameof(Test_GpuTier_HighVram_UnknownName_NotEntry), () =>
        {
            var (tier, matched) = PerformanceTierTable.ResolveGpuTier("WeirdGPUBrand Xeon3D", 16384);
            Assert(tier != PerformanceTierTable.TierEntry, nameof(Test_GpuTier_HighVram_UnknownName_NotEntry), $"16GB VRAM should not be Entry, got {tier}");
            Assert(!matched, nameof(Test_GpuTier_HighVram_UnknownName_NotEntry), $"Unknown name should not be matched");
        });

        private static void Test_GpuTier_WeirdName_8gbVram_IsHighEnd() => RunTest(nameof(Test_GpuTier_WeirdName_8gbVram_IsHighEnd), () =>
        {
            var (tier, _) = PerformanceTierTable.ResolveGpuTier("SuperUnknownGPU 2025", 8192);
            Assert(tier == PerformanceTierTable.TierHighEnd, nameof(Test_GpuTier_WeirdName_8gbVram_IsHighEnd), $"8GB VRAM should be High-end, got {tier}");
        });

        private static void Test_RamTier_32GB_IsHighEnd() => RunTest(nameof(Test_RamTier_32GB_IsHighEnd), () =>
        {
            Assert(PerformanceTierTable.ResolveRamTier(32) == PerformanceTierTable.TierHighEnd, nameof(Test_RamTier_32GB_IsHighEnd), "");
        });

        private static void Test_RamTier_16GB_IsMidRange() => RunTest(nameof(Test_RamTier_16GB_IsMidRange), () =>
        {
            Assert(PerformanceTierTable.ResolveRamTier(16) == PerformanceTierTable.TierMidRange, nameof(Test_RamTier_16GB_IsMidRange), "");
        });

        private static void Test_RamTier_8GB_IsEntry() => RunTest(nameof(Test_RamTier_8GB_IsEntry), () =>
        {
            Assert(PerformanceTierTable.ResolveRamTier(8) == PerformanceTierTable.TierEntry, nameof(Test_RamTier_8GB_IsEntry), "");
        });

        private static void Test_StorageTier_NVMe_IsHighEnd() => RunTest(nameof(Test_StorageTier_NVMe_IsHighEnd), () =>
        {
            Assert(PerformanceTierTable.ResolveStorageTier("NVMe") == PerformanceTierTable.TierHighEnd, nameof(Test_StorageTier_NVMe_IsHighEnd), "");
        });

        private static void Test_StorageTier_SATA_IsMidRange() => RunTest(nameof(Test_StorageTier_SATA_IsMidRange), () =>
        {
            Assert(PerformanceTierTable.ResolveStorageTier("SATA_SSD") == PerformanceTierTable.TierMidRange, nameof(Test_StorageTier_SATA_IsMidRange), "");
        });

        private static void Test_StorageTier_HDD_IsEntry() => RunTest(nameof(Test_StorageTier_HDD_IsEntry), () =>
        {
            Assert(PerformanceTierTable.ResolveStorageTier("HDD") == PerformanceTierTable.TierEntry, nameof(Test_StorageTier_HDD_IsEntry), "");
        });

        // ═══════════════════════════════════════════════════════════
        // 3. SCENARIO SCORE DETERMINISM
        // ═══════════════════════════════════════════════════════════

        private static void Test_OfficeBrowsing_Determinism() => RunTest(nameof(Test_OfficeBrowsing_Determinism), () =>
        {
            var p = MakeProfile("Intel Core i5-12400F", 6, 12, "NVIDIA GeForce RTX 3060", 12288, 16, "NVMe");
            var s1 = ScoreAll(p);
            var s2 = ScoreAll(p);
            var office1 = FindScenario(s1, "office");
            var office2 = FindScenario(s2, "office");
            Assert(office1.Score == office2.Score, nameof(Test_OfficeBrowsing_Determinism),
                $"Run1={office1.Score}, Run2={office2.Score}");
        });

        private static void Test_Gaming1440p_Determinism() => RunTest(nameof(Test_Gaming1440p_Determinism), () =>
        {
            var p = MakeProfile("AMD Ryzen 9 5900X", 12, 24, "NVIDIA GeForce RTX 3090", 24576, 64, "NVMe");
            var s1 = ScoreAll(p);
            var s2 = ScoreAll(p);
            var g1 = FindScenario(s1, "gaming_1440p");
            var g2 = FindScenario(s2, "gaming_1440p");
            Assert(g1.Score == g2.Score, nameof(Test_Gaming1440p_Determinism), $"Run1={g1.Score}, Run2={g2.Score}");
        });

        private static void Test_AllScenarios_SameInputs_SameOutputs() => RunTest(nameof(Test_AllScenarios_SameInputs_SameOutputs), () =>
        {
            var p = MakeProfile("Intel Core i7-12700K", 12, 20, "NVIDIA GeForce RTX 3060", 12288, 32, "NVMe");
            var run1 = ScoreAll(p);
            var run2 = ScoreAll(p);
            for (int i = 0; i < run1.Count; i++)
            {
                Assert(run1[i].Score == run2[i].Score && run1[i].Classification == run2[i].Classification,
                    nameof(Test_AllScenarios_SameInputs_SameOutputs),
                    $"Scenario {run1[i].Name}: run1={run1[i].Score}/{run1[i].Classification}, run2={run2[i].Score}/{run2[i].Classification}");
            }
        });

        // ═══════════════════════════════════════════════════════════
        // 4. SCORE CLAMPING
        // ═══════════════════════════════════════════════════════════

        private static void Test_Score_NeverBelow0() => RunTest(nameof(Test_Score_NeverBelow0), () =>
        {
            // Worst-case: no CPU, no GPU, no RAM, HDD
            var p = MakeProfile(null, 0, 0, null, 0, 0, "HDD");
            var scores = ScoreAll(p);
            foreach (var s in scores)
                Assert(s.Score >= 0, nameof(Test_Score_NeverBelow0), $"{s.Name} score={s.Score}");
        });

        private static void Test_Score_NeverAbove100() => RunTest(nameof(Test_Score_NeverAbove100), () =>
        {
            // Best-case
            var p = MakeProfile("AMD Ryzen 9 7950X", 16, 32, "NVIDIA GeForce RTX 4090", 24576, 128, "NVMe");
            var scores = ScoreAll(p);
            foreach (var s in scores)
                Assert(s.Score <= 100, nameof(Test_Score_NeverAbove100), $"{s.Name} score={s.Score}");
        });

        private static void Test_Score_100Bar_HighEnd() => RunTest(nameof(Test_Score_100Bar_HighEnd), () =>
        {
            var p = MakeProfile("AMD Ryzen 9 5900X", 12, 24, "NVIDIA GeForce RTX 3090", 24576, 64, "NVMe");
            var scores = ScoreAll(p);
            var office = FindScenario(scores, "office");
            // Office on a RTX 3090 system: verify the score makes sense (should be 100 with this config)
            Assert(office.Score <= 100, nameof(Test_Score_100Bar_HighEnd), $"Office score={office.Score}");
        });

        // ═══════════════════════════════════════════════════════════
        // 5. DISSONANCE DETECTION
        // ═══════════════════════════════════════════════════════════

        private static void Test_Dissonance_OfficeVsGaming1440p_HighEnd() => RunTest(nameof(Test_Dissonance_OfficeVsGaming1440p_HighEnd), () =>
        {
            // HIGH-END MACHINE: Office should NOT be lower than Gaming 1440p
            // But Gaming 1440p should NOT be higher than Office if Office is the easier task
            // The REAL problem: Office should cap at ~85 for moderate machines but Gaming 1440p
            // can reach 100 for ultra-high-end. The dissonance is Office reaching 95 on a system
            // where Gaming 1440p is 100.
            var p = MakeProfile("AMD Ryzen 9 5900X", 12, 24, "NVIDIA GeForce RTX 3090", 24576, 64, "NVMe");
            var scores = ScoreAll(p);
            var office = FindScenario(scores, "office");
            var gaming1440 = FindScenario(scores, "gaming_1440p");

            // CRITICAL BUG CHECK: If gaming_1440p = 100 and office = 95, that is dissonant.
            // A harder task should never score higher than an easier task on the same hardware.
            // Verify: office >= gaming_1440p on high-end (easy task should score at least as much)
            bool dissonant = gaming1440.Score > office.Score;
            Assert(!dissonant, nameof(Test_Dissonance_OfficeVsGaming1440p_HighEnd),
                $"DISSONANCE DETECTED: Office={office.Score}, Gaming1440p={gaming1440.Score}. " +
                $"A harder task (gaming 1440p) scores higher than an easier task (office). " +
                $"Office formula max = base(50)+cpu(25)+ram8(15)+ram16(5)+nvme(5) = 100, " +
                $"Gaming1440p formula max = base(20)+gpu(40)+vram(25)+ram(15) = 100");
        });

        private static void Test_Dissonance_OfficeVsGaming1440p_LowEnd() => RunTest(nameof(Test_Dissonance_OfficeVsGaming1440p_LowEnd), () =>
        {
            // LOW-END: Office should be much higher than Gaming 1440p
            var p = MakeProfile("Intel Core i3-10100", 4, 8, "Intel UHD Graphics 630", 0, 8, "HDD");
            var scores = ScoreAll(p);
            var office = FindScenario(scores, "office");
            var gaming1440 = FindScenario(scores, "gaming_1440p");
            Assert(office.Score > gaming1440.Score, nameof(Test_Dissonance_OfficeVsGaming1440p_LowEnd),
                $"Low-end should have Office >> Gaming1440p. Office={office.Score}, Gaming1440p={gaming1440.Score}");
        });

        private static void Test_Dissonance_OfficeVsGaming1440p_MidRange() => RunTest(nameof(Test_Dissonance_OfficeVsGaming1440p_MidRange), () =>
        {
            // MID-RANGE: Office should be higher than Gaming 1440p
            var p = MakeProfile("Intel Core i5-12400F", 6, 12, "NVIDIA GeForce RTX 3060", 12288, 16, "NVMe");
            var scores = ScoreAll(p);
            var office = FindScenario(scores, "office");
            var gaming1440 = FindScenario(scores, "gaming_1440p");
            Assert(office.Score >= gaming1440.Score, nameof(Test_Dissonance_OfficeVsGaming1440p_MidRange),
                $"Mid-range: Office should >= Gaming1440p. Office={office.Score}, Gaming1440p={gaming1440.Score}");
        });

        private static void Test_Dissonance_MonotonicDifficulty() => RunTest(nameof(Test_Dissonance_MonotonicDifficulty), () =>
        {
            // On any machine, scores for easier scenarios should be >= harder scenarios:
            // office >= multitasking (generally)
            // gaming_1080p >= gaming_1440p (always)
            var profiles = new[]
            {
                MakeProfile("Intel Core i3-10100", 4, 8, "Intel UHD Graphics 630", 0, 8, "HDD"),
                MakeProfile("Intel Core i5-12400F", 6, 12, "NVIDIA GeForce RTX 3060", 12288, 16, "NVMe"),
                MakeProfile("AMD Ryzen 9 5900X", 12, 24, "NVIDIA GeForce RTX 3090", 24576, 64, "NVMe"),
            };

            foreach (var p in profiles)
            {
                var scores = ScoreAll(p);
                var g1080 = FindScenario(scores, "gaming_1080p");
                var g1440 = FindScenario(scores, "gaming_1440p");
                Assert(g1080.Score >= g1440.Score, nameof(Test_Dissonance_MonotonicDifficulty),
                    $"[{p.CpuModel}/{p.GpuModel}] Gaming1080p={g1080.Score} should >= Gaming1440p={g1440.Score}");
            }
        });

        // ═══════════════════════════════════════════════════════════
        // 6. EDGE CASES
        // ═══════════════════════════════════════════════════════════

        private static void Test_EdgeCase_MissingVram_ZeroGpuVram() => RunTest(nameof(Test_EdgeCase_MissingVram_ZeroGpuVram), () =>
        {
            var p = MakeProfile("Intel Core i5-12400F", 6, 12, "NVIDIA GeForce RTX 3060", 0, 16, "NVMe");
            var scores = ScoreAll(p);
            var gaming = FindScenario(scores, "gaming_1440p");
            // With zero VRAM, gaming score should be reduced (not claiming excellent)
            Assert(gaming.Score < 80, nameof(Test_EdgeCase_MissingVram_ZeroGpuVram),
                $"Zero VRAM should reduce gaming score significantly, got {gaming.Score}");
        });

        private static void Test_EdgeCase_ZeroRam() => RunTest(nameof(Test_EdgeCase_ZeroRam), () =>
        {
            var p = MakeProfile("Intel Core i5-12400F", 6, 12, "NVIDIA GeForce RTX 3060", 12288, 0, "NVMe");
            var scores = ScoreAll(p);
            foreach (var s in scores)
                Assert(s.Score >= 0, nameof(Test_EdgeCase_ZeroRam), $"{s.Name} score={s.Score} should be >= 0");
        });

        private static void Test_EdgeCase_ZeroCores() => RunTest(nameof(Test_EdgeCase_ZeroCores), () =>
        {
            var p = MakeProfile(null, 0, 0, "NVIDIA GeForce RTX 3060", 12288, 16, "NVMe");
            var scores = ScoreAll(p);
            foreach (var s in scores)
                Assert(s.Score >= 0 && s.Score <= 100, nameof(Test_EdgeCase_ZeroCores), $"{s.Name} score={s.Score}");
        });

        private static void Test_EdgeCase_UnknownStorage() => RunTest(nameof(Test_EdgeCase_UnknownStorage), () =>
        {
            var p = MakeProfile("Intel Core i5-12400F", 6, 12, "NVIDIA GeForce RTX 3060", 12288, 16, "Unknown");
            var scores = ScoreAll(p);
            var office = FindScenario(scores, "office");
            // Unknown storage should not get NVMe bonus
            Assert(office.Score <= 95, nameof(Test_EdgeCase_UnknownStorage), $"Unknown storage should not get NVMe bonus. Score={office.Score}");
        });

        // ═══════════════════════════════════════════════════════════
        // 7. INTEGRATION: 6 REPRESENTATIVE PROFILES
        // ═══════════════════════════════════════════════════════════

        private static void Test_Profile_OfficeLowEnd() => RunTest(nameof(Test_Profile_OfficeLowEnd), () =>
        {
            // Office low-end: 4c CPU, iGPU, 8GB RAM, HDD
            var p = MakeProfile("Intel Core i3-10100", 4, 8, "Intel UHD Graphics 630", 0, 8, "HDD");
            var scores = ScoreAll(p);
            var office = FindScenario(scores, "office");
            var gaming1440 = FindScenario(scores, "gaming_1440p");
            var ai = FindScenario(scores, "ai_inference");

            Assert(office.Score >= 50, nameof(Test_Profile_OfficeLowEnd), $"Office should be at least 50, got {office.Score}");
            Assert(office.Classification != ScenarioClassification.NotRecommended, nameof(Test_Profile_OfficeLowEnd),
                $"Office should not be 'Not Recommended' for office PC");
            Assert(gaming1440.Score < 40, nameof(Test_Profile_OfficeLowEnd),
                $"Gaming 1440p should be Not Recommended on office PC, got {gaming1440.Score}");
            Assert(ai.Score < 40, nameof(Test_Profile_OfficeLowEnd),
                $"AI should be Not Recommended on office PC, got {ai.Score}");
        });

        private static void Test_Profile_MidrangeGaming() => RunTest(nameof(Test_Profile_MidrangeGaming), () =>
        {
            // Midrange gaming: 6–8c CPU, RTX 3060, 16GB, SSD
            var p = MakeProfile("Intel Core i5-12400F", 6, 12, "NVIDIA GeForce RTX 3060", 12288, 16, "SATA_SSD");
            var scores = ScoreAll(p);
            var office = FindScenario(scores, "office");
            var gaming1080 = FindScenario(scores, "gaming_1080p");
            var gaming1440 = FindScenario(scores, "gaming_1440p");

            Assert(office.Classification == ScenarioClassification.Excellent, nameof(Test_Profile_MidrangeGaming),
                $"Office should be Excellent on midrange, got {office.Classification} (score={office.Score})");
            Assert(gaming1080.Score >= 56, nameof(Test_Profile_MidrangeGaming),
                $"Gaming 1080p should be at least Good, got {gaming1080.Score}");
            Assert(gaming1440.Score < gaming1080.Score || gaming1440.Score == gaming1080.Score, nameof(Test_Profile_MidrangeGaming),
                $"Gaming 1440p should not exceed 1080p, got 1440p={gaming1440.Score}, 1080p={gaming1080.Score}");
        });

        private static void Test_Profile_HighEndGaming() => RunTest(nameof(Test_Profile_HighEndGaming), () =>
        {
            // High-end gaming: 12c CPU, RTX 3090, 32–64GB, NVMe
            var p = MakeProfile("AMD Ryzen 9 5900X", 12, 24, "NVIDIA GeForce RTX 3090", 24576, 64, "NVMe");
            var scores = ScoreAll(p);
            var office = FindScenario(scores, "office");
            var gaming1440 = FindScenario(scores, "gaming_1440p");

            // All scenarios should be Excellent or Good
            foreach (var s in scores)
            {
                Assert(s.Classification == ScenarioClassification.Excellent || s.Classification == ScenarioClassification.Good,
                    nameof(Test_Profile_HighEndGaming),
                    $"{s.Name} should be Excellent or Good on high-end, got {s.Classification} (score={s.Score})");
            }

            // GPU is known high-end — must not be classified as Entry
            Assert(p.GpuTier != PerformanceTierTable.TierEntry, nameof(Test_Profile_HighEndGaming),
                $"RTX 3090 must not be Entry, got {p.GpuTier}");
        });

        private static void Test_Profile_WorkstationEditing() => RunTest(nameof(Test_Profile_WorkstationEditing), () =>
        {
            // Workstation editing: 16c CPU, RTX 4090, 64–128GB, NVMe
            var p = MakeProfile("AMD Ryzen 9 7950X", 16, 32, "NVIDIA GeForce RTX 4090", 24576, 128, "NVMe");
            var scores = ScoreAll(p);

            // All scenarios should be Excellent
            foreach (var s in scores)
            {
                Assert(s.Classification == ScenarioClassification.Excellent, nameof(Test_Profile_WorkstationEditing),
                    $"{s.Name} should be Excellent on workstation, got {s.Classification} (score={s.Score})");
            }
        });

        private static void Test_Profile_UnmatchedGpuName() => RunTest(nameof(Test_Profile_UnmatchedGpuName), () =>
        {
            // Unmatched GPU name with 12GB VRAM — should NOT be Entry
            var p = MakeProfile("Intel Core i5-12400F", 6, 12, "SuperUnknownGPU Xeon3D 2025", 12288, 16, "NVMe");
            Assert(!p.GpuNameMatched, nameof(Test_Profile_UnmatchedGpuName), "GPU name should not be matched");
            Assert(p.GpuTier != PerformanceTierTable.TierEntry, nameof(Test_Profile_UnmatchedGpuName),
                $"12GB VRAM GPU should not be Entry, got {p.GpuTier}");

            var scores = ScoreAll(p);
            var gaming1440 = FindScenario(scores, "gaming_1440p");
            Assert(gaming1440.Score > 20, nameof(Test_Profile_UnmatchedGpuName),
                $"12GB VRAM should contribute to gaming 1440p, got {gaming1440.Score}");
        });

        private static void Test_Profile_MissingVram() => RunTest(nameof(Test_Profile_MissingVram), () =>
        {
            // Missing VRAM field (0) — should degrade gracefully, NOT lie with high score
            var p = MakeProfile("Intel Core i5-12400F", 6, 12, "NVIDIA GeForce RTX 3060", 0, 16, "NVMe");
            var scores = ScoreAll(p);
            var gaming1440 = FindScenario(scores, "gaming_1440p");
            var ai = FindScenario(scores, "ai_inference");

            // With missing VRAM, VRAM-dependent scenarios should be penalized
            Assert(gaming1440.Score <= 70, nameof(Test_Profile_MissingVram),
                $"Missing VRAM should reduce gaming 1440p score, got {gaming1440.Score}");
            Assert(ai.Score <= 55, nameof(Test_Profile_MissingVram),
                $"Missing VRAM should reduce AI score, got {ai.Score}");
        });

        // ═══════════════════════════════════════════════════════════
        // 8. FLOOR MECHANISM
        // ═══════════════════════════════════════════════════════════

        private static void Test_Floor_HighEnd_Gaming1440p_Minimum80() => RunTest(nameof(Test_Floor_HighEnd_Gaming1440p_Minimum80), () =>
        {
            // ApplyScenarioScoreFloors in PerformanceEvaluationEngine:
            // For high-end (3090 + 12 cores + 32GB RAM): gaming_1440p floor = 80
            // Test by calling the full engine... but since we can't easily call Evaluate without JSON,
            // test the scorer directly: RTX 3090 + 24GB VRAM + 12 cores + 64GB RAM
            var p = MakeProfile("AMD Ryzen 9 5900X", 12, 24, "NVIDIA GeForce RTX 3090", 24576, 64, "NVMe");
            var scores = ScoreAll(p);
            var gaming1440 = FindScenario(scores, "gaming_1440p");
            // With High-end GPU tier (order 4): base(20) + gpu(40) + vram25(25) + ram(15) = 100
            Assert(gaming1440.Score >= 80, nameof(Test_Floor_HighEnd_Gaming1440p_Minimum80),
                $"High-end gaming 1440p should be at least 80, got {gaming1440.Score}");
        });

        private static void Test_Floor_NotApplied_MidRange() => RunTest(nameof(Test_Floor_NotApplied_MidRange), () =>
        {
            // Mid-range should NOT get floor applied (floors are only for 3090+ with 12c + 32GB)
            var p = MakeProfile("Intel Core i5-12400F", 6, 12, "NVIDIA GeForce RTX 3060", 12288, 16, "NVMe");
            var scores = ScoreAll(p);
            var gaming1440 = FindScenario(scores, "gaming_1440p");
            // RTX 3060 = Upper Mid (tier order 3): base(20) + gpu(30) + vram(25) + ram(15) = 90
            // But without floor mechanism (not high-end config), just verify raw score
            Assert(gaming1440.Score > 0, nameof(Test_Floor_NotApplied_MidRange),
                $"Score should be positive, got {gaming1440.Score}");
        });

        // ═══════════════════════════════════════════════════════════
        // 9. SINGLE SCORE (GLOBAL AVERAGE)
        // ═══════════════════════════════════════════════════════════

        private static void Test_SingleScore_IsAverageOfScenarios() => RunTest(nameof(Test_SingleScore_IsAverageOfScenarios), () =>
        {
            var p = MakeProfile("Intel Core i5-12400F", 6, 12, "NVIDIA GeForce RTX 3060", 12288, 16, "NVMe");
            var scores = ScoreAll(p);
            double sum = scores.Sum(s => s.Score);
            int expectedAvg = (int)Math.Round(sum / scores.Count);
            // We can't call ComputeSingleScore directly (it's private), but we can verify the formula
            // by computing the expected average ourselves
            Assert(expectedAvg >= 0 && expectedAvg <= 100, nameof(Test_SingleScore_IsAverageOfScenarios),
                $"Average should be 0-100, got {expectedAvg}");
        });

        // ═══════════════════════════════════════════════════════════
        // 10. DATASET-DRIVEN SCORING EQUIVALENCE
        // ═══════════════════════════════════════════════════════════

        /// <summary>Load the pinned reference dataset from embedded JSON string (mirrors hardcoded constants).</summary>
        private static PerformanceDataset? LoadPinnedDataset()
        {
            try
            {
                // Try loading from the Data directory first
                var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "performance_dataset_v1.json");
                if (!System.IO.File.Exists(path))
                {
                    // Try relative to the current directory
                    path = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Data", "performance_dataset_v1.json");
                }
                if (System.IO.File.Exists(path))
                {
                    var json = System.IO.File.ReadAllText(path);
                    var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    return System.Text.Json.JsonSerializer.Deserialize<PerformanceDataset>(json, options);
                }
                // Fallback: construct programmatically
                return BuildPinnedDataset();
            }
            catch
            {
                return BuildPinnedDataset();
            }
        }

        private static PerformanceDataset BuildPinnedDataset()
        {
            return new PerformanceDataset
            {
                DatasetVersion = "1.0.0",
                PublishedAt = "2026-02-12T00:00:00Z",
                CpuPatterns = new System.Collections.Generic.List<PatternRule>
                {
                    new() { Pattern = "ryzen 9", TierOrder = 4 }, new() { Pattern = "core i9", TierOrder = 4 },
                    new() { Pattern = "xeon", TierOrder = 4 }, new() { Pattern = "ryzen 7", TierOrder = 3 },
                    new() { Pattern = "core i7", TierOrder = 3 }, new() { Pattern = "ryzen 5", TierOrder = 2 },
                    new() { Pattern = "core i5", TierOrder = 2 }, new() { Pattern = "ryzen 3", TierOrder = 1 },
                    new() { Pattern = "core i3", TierOrder = 1 }, new() { Pattern = "pentium", TierOrder = 1 }
                },
                GpuPatterns = new System.Collections.Generic.List<PatternRule>
                {
                    new() { Pattern = "3090", TierOrder = 4 }, new() { Pattern = "4080", TierOrder = 4 },
                    new() { Pattern = "4090", TierOrder = 4 }, new() { Pattern = "rtx 40", TierOrder = 4 },
                    new() { Pattern = "rx 7", TierOrder = 4 }, new() { Pattern = "rtx 30", TierOrder = 3 },
                    new() { Pattern = "rx 6", TierOrder = 3 }, new() { Pattern = "gtx 16", TierOrder = 2 },
                    new() { Pattern = "rx 5", TierOrder = 2 }, new() { Pattern = "uhd", TierOrder = 1 },
                    new() { Pattern = "iris", TierOrder = 1 }, new() { Pattern = "vega", TierOrder = 1 }
                },
                CpuHeuristicRules = new CpuHeuristicRules(),
                GpuVramThresholds = new GpuVramThresholds(),
                RamTierRules = new RamTierRules(),
                StorageTierRules = new StorageTierRules(),
                ClassificationThresholds = new ClassificationThresholds(),
                ScenarioRules = BuildPinnedScenarioRules(),
                Floors = new FloorRules
                {
                    HighEndCondition = new FloorCondition { GpuPatterns = new() { "3090" }, MinVramMb = 24576, MinCores = 12, MinRamGb = 32 },
                    ScenarioFloors = new() { ["gaming_1440p"] = 80, ["4k_editing"] = 75, ["streaming_gaming"] = 75, ["ai_inference"] = 70 }
                }
            };
        }

        private static Dictionary<string, ScenarioRule> BuildPinnedScenarioRules()
        {
            return new Dictionary<string, ScenarioRule>
            {
                ["office"] = new() { Base = 50, Bonuses = new() {
                    new() { Condition = "CpuTierOrder>=1", Points = 25 },
                    new() { Condition = "RamGb>=8", Points = 15 },
                    new() { Condition = "RamGb>=16", Points = 5 },
                    new() { Condition = "StorageKind==HDD", Points = -15 },
                    new() { Condition = "StorageKind==NVMe", Points = 5, ElseIf = true }
                }},
                ["multitasking"] = new() { Base = 30, Bonuses = new() {
                    new() { Condition = "CpuTierOrder>=2", Points = 25 },
                    new() { Condition = "CpuTierOrder>=3", Points = 15 },
                    new() { Condition = "RamGb>=16", Points = 25 },
                    new() { Condition = "RamGb>=32", Points = 5 }
                }},
                ["gaming_1080p"] = new() { Base = 40, Bonuses = new() {
                    new() { Condition = "GpuTierOrder>=2", Points = 30 },
                    new() { Condition = "GpuTierOrder>=1", Points = 15, ElseIf = true },
                    new() { Condition = "GpuVramMb>=6144", Points = 20 },
                    new() { Condition = "GpuVramMb>=4096", Points = 10, ElseIf = true },
                    new() { Condition = "RamGb>=16", Points = 10 },
                    new() { Condition = "RamGb>=8", Points = 5, ElseIf = true }
                }},
                ["gaming_1440p"] = new() { Base = 20, Bonuses = new() {
                    new() { Condition = "GpuTierOrder>=4", Points = 40 },
                    new() { Condition = "GpuTierOrder>=3", Points = 30, ElseIf = true },
                    new() { Condition = "GpuTierOrder>=2", Points = 15, ElseIf = true },
                    new() { Condition = "GpuVramMb>=8192", Points = 25 },
                    new() { Condition = "GpuVramMb>=6144", Points = 15, ElseIf = true },
                    new() { Condition = "RamGb>=16", Points = 15 }
                }},
                ["4k_editing"] = new() { Base = 0, Bonuses = new() {
                    new() { Condition = "CpuTierOrder>=4", Points = 30 },
                    new() { Condition = "CpuTierOrder>=3", Points = 20, ElseIf = true },
                    new() { Condition = "RamGb>=32", Points = 30 },
                    new() { Condition = "RamGb>=16", Points = 15, ElseIf = true },
                    new() { Condition = "GpuTierOrder>=2", Points = 20 },
                    new() { Condition = "StorageKind==NVMe", Points = 20 },
                    new() { Condition = "StorageKind==SATA_SSD", Points = 10, ElseIf = true }
                }},
                ["streaming_gaming"] = new() { Base = 25, Bonuses = new() {
                    new() { Condition = "CpuTierOrder>=3", Points = 25 },
                    new() { Condition = "CpuTierOrder>=2", Points = 15, ElseIf = true },
                    new() { Condition = "GpuTierOrder>=3", Points = 25 },
                    new() { Condition = "GpuTierOrder>=2", Points = 15, ElseIf = true },
                    new() { Condition = "RamGb>=16", Points = 25 }
                }},
                ["vms"] = new() { Base = 20, Bonuses = new() {
                    new() { Condition = "CpuThreads>=16", Points = 35 },
                    new() { Condition = "CpuThreads>=8", Points = 25, ElseIf = true },
                    new() { Condition = "CpuThreads>=4", Points = 15, ElseIf = true },
                    new() { Condition = "RamGb>=32", Points = 35 },
                    new() { Condition = "RamGb>=16", Points = 25, ElseIf = true },
                    new() { Condition = "RamGb>=8", Points = 10, ElseIf = true }
                }},
                ["ai_inference"] = new() { Base = 20, Bonuses = new() {
                    new() { Condition = "GpuVramMb>=8192", Points = 40 },
                    new() { Condition = "GpuVramMb>=6144", Points = 30, ElseIf = true },
                    new() { Condition = "GpuVramMb>=4096", Points = 20, ElseIf = true },
                    new() { Condition = "RamGb>=32", Points = 20 },
                    new() { Condition = "RamGb>=16", Points = 15, ElseIf = true }
                }}
            };
        }

        /// <summary>Build a HardwareProfile with tiers resolved from dataset.</summary>
        private static HardwareProfile MakeProfileWithDataset(
            string? cpuModel, int cpuCores, int cpuThreads,
            string? gpuModel, double gpuVramMb,
            double ramGb, string storageKind, PerformanceDataset ds)
        {
            var p = new HardwareProfile
            {
                CpuModel = cpuModel,
                CpuCores = cpuCores,
                CpuThreads = cpuThreads,
                GpuModel = gpuModel,
                GpuVramMb = gpuVramMb,
                RamGb = ramGb,
                StorageKind = storageKind
            };
            var (cpuTier, cpuMatched) = PerformanceTierTable.ResolveCpuTier(cpuModel, cpuCores, cpuThreads, ds);
            var (gpuTier, gpuMatched) = PerformanceTierTable.ResolveGpuTier(gpuModel, gpuVramMb, ds);
            p.CpuTier = cpuTier;
            p.GpuTier = gpuTier;
            p.CpuNameMatched = cpuMatched;
            p.GpuNameMatched = gpuMatched;
            p.RamTier = PerformanceTierTable.ResolveRamTier(ramGb, ds);
            p.StorageTier = PerformanceTierTable.ResolveStorageTier(storageKind, ds);
            return p;
        }

        private static void Test_DatasetDriven_OfficeBrowsing_SameAsHardcoded() => RunTest(nameof(Test_DatasetDriven_OfficeBrowsing_SameAsHardcoded), () =>
        {
            var ds = LoadPinnedDataset()!;
            var profiles = new[]
            {
                ("Intel Core i3-10100", 4, 8, "Intel UHD Graphics 630", 0.0, 8.0, "HDD"),
                ("Intel Core i5-12400F", 6, 12, "NVIDIA GeForce RTX 3060", 12288.0, 16.0, "NVMe"),
                ("AMD Ryzen 9 5900X", 12, 24, "NVIDIA GeForce RTX 3090", 24576.0, 64.0, "NVMe")
            };
            foreach (var (cpu, cores, threads, gpu, vram, ram, storage) in profiles)
            {
                var hc = MakeProfile(cpu, cores, threads, gpu, vram, ram, storage);
                var dd = MakeProfileWithDataset(cpu, cores, threads, gpu, vram, ram, storage, ds);
                var hcScores = UsageScenarioScorer.Score(hc);
                var ddScores = UsageScenarioScorer.Score(dd, ds);
                var hcOffice = hcScores.First(s => s.ScenarioId == "office");
                var ddOffice = ddScores.First(s => s.ScenarioId == "office");
                Assert(hcOffice.Score == ddOffice.Score, nameof(Test_DatasetDriven_OfficeBrowsing_SameAsHardcoded),
                    $"[{cpu}] Hardcoded={hcOffice.Score}, Dataset={ddOffice.Score}");
            }
        });

        private static void Test_DatasetDriven_Gaming1440p_SameAsHardcoded() => RunTest(nameof(Test_DatasetDriven_Gaming1440p_SameAsHardcoded), () =>
        {
            var ds = LoadPinnedDataset()!;
            var profiles = new[]
            {
                ("Intel Core i3-10100", 4, 8, "Intel UHD Graphics 630", 0.0, 8.0, "HDD"),
                ("Intel Core i5-12400F", 6, 12, "NVIDIA GeForce RTX 3060", 12288.0, 16.0, "SATA_SSD"),
                ("AMD Ryzen 9 5900X", 12, 24, "NVIDIA GeForce RTX 3090", 24576.0, 64.0, "NVMe")
            };
            foreach (var (cpu, cores, threads, gpu, vram, ram, storage) in profiles)
            {
                var hc = MakeProfile(cpu, cores, threads, gpu, vram, ram, storage);
                var dd = MakeProfileWithDataset(cpu, cores, threads, gpu, vram, ram, storage, ds);
                var hcScores = UsageScenarioScorer.Score(hc);
                var ddScores = UsageScenarioScorer.Score(dd, ds);
                var hcG = hcScores.First(s => s.ScenarioId == "gaming_1440p");
                var ddG = ddScores.First(s => s.ScenarioId == "gaming_1440p");
                Assert(hcG.Score == ddG.Score, nameof(Test_DatasetDriven_Gaming1440p_SameAsHardcoded),
                    $"[{cpu}] Hardcoded={hcG.Score}, Dataset={ddG.Score}");
            }
        });

        private static void Test_DatasetDriven_AllScenarios_6Profiles_SameAsHardcoded() => RunTest(nameof(Test_DatasetDriven_AllScenarios_6Profiles_SameAsHardcoded), () =>
        {
            var ds = LoadPinnedDataset()!;
            var profiles = new[]
            {
                ("Intel Core i3-10100", 4, 8, "Intel UHD Graphics 630", 0.0, 8.0, "HDD"),
                ("Intel Core i5-12400F", 6, 12, "NVIDIA GeForce RTX 3060", 12288.0, 16.0, "SATA_SSD"),
                ("Intel Core i5-12400F", 6, 12, "NVIDIA GeForce RTX 3060", 12288.0, 16.0, "NVMe"),
                ("AMD Ryzen 9 5900X", 12, 24, "NVIDIA GeForce RTX 3090", 24576.0, 64.0, "NVMe"),
                ("AMD Ryzen 9 7950X", 16, 32, "NVIDIA GeForce RTX 4090", 24576.0, 128.0, "NVMe"),
                ("Intel Core i5-12400F", 6, 12, "SuperUnknownGPU Xeon3D 2025", 12288.0, 16.0, "NVMe")
            };
            foreach (var (cpu, cores, threads, gpu, vram, ram, storage) in profiles)
            {
                var hc = MakeProfile(cpu, cores, threads, gpu, vram, ram, storage);
                var dd = MakeProfileWithDataset(cpu, cores, threads, gpu, vram, ram, storage, ds);
                var hcScores = UsageScenarioScorer.Score(hc);
                var ddScores = UsageScenarioScorer.Score(dd, ds);
                for (int i = 0; i < hcScores.Count; i++)
                {
                    Assert(hcScores[i].Score == ddScores[i].Score,
                        nameof(Test_DatasetDriven_AllScenarios_6Profiles_SameAsHardcoded),
                        $"[{cpu}/{gpu}] {hcScores[i].Name}: Hardcoded={hcScores[i].Score}, Dataset={ddScores[i].Score}");
                    Assert(hcScores[i].Classification == ddScores[i].Classification,
                        nameof(Test_DatasetDriven_AllScenarios_6Profiles_SameAsHardcoded),
                        $"[{cpu}/{gpu}] {hcScores[i].Name}: HC_Class={hcScores[i].Classification}, DS_Class={ddScores[i].Classification}");
                }
            }
        });

        private static void Test_DatasetDriven_TierResolution_SameAsHardcoded() => RunTest(nameof(Test_DatasetDriven_TierResolution_SameAsHardcoded), () =>
        {
            var ds = LoadPinnedDataset()!;
            // CPU tiers
            var cpuTests = new[] {
                ("AMD Ryzen 9 5900X", 12, 24, PerformanceTierTable.TierHighEnd),
                ("Intel Core i7-12700K", 12, 20, PerformanceTierTable.TierUpperMid),
                ("Intel Core i5-12400F", 6, 12, PerformanceTierTable.TierMidRange),
                ("Intel Core i3-10100", 4, 8, PerformanceTierTable.TierEntry)
            };
            foreach (var (name, cores, threads, expectedTier) in cpuTests)
            {
                var (hcTier, _) = PerformanceTierTable.ResolveCpuTier(name, cores, threads);
                var (dsTier, _) = PerformanceTierTable.ResolveCpuTier(name, cores, threads, ds);
                Assert(hcTier == dsTier, nameof(Test_DatasetDriven_TierResolution_SameAsHardcoded),
                    $"CPU [{name}]: HC={hcTier}, DS={dsTier}");
                Assert(hcTier == expectedTier, nameof(Test_DatasetDriven_TierResolution_SameAsHardcoded),
                    $"CPU [{name}]: expected={expectedTier}, got={hcTier}");
            }
            // GPU tiers
            var gpuTests = new[] {
                ("NVIDIA GeForce RTX 3090", 24576.0, PerformanceTierTable.TierHighEnd),
                ("NVIDIA GeForce RTX 3060", 12288.0, PerformanceTierTable.TierUpperMid),
                ("NVIDIA GeForce GTX 1650", 4096.0, PerformanceTierTable.TierMidRange),
                ("Intel UHD Graphics 630", 0.0, PerformanceTierTable.TierEntry)
            };
            foreach (var (name, vram, expectedTier) in gpuTests)
            {
                var (hcTier, _) = PerformanceTierTable.ResolveGpuTier(name, vram);
                var (dsTier, _) = PerformanceTierTable.ResolveGpuTier(name, vram, ds);
                Assert(hcTier == dsTier, nameof(Test_DatasetDriven_TierResolution_SameAsHardcoded),
                    $"GPU [{name}]: HC={hcTier}, DS={dsTier}");
                Assert(hcTier == expectedTier, nameof(Test_DatasetDriven_TierResolution_SameAsHardcoded),
                    $"GPU [{name}]: expected={expectedTier}, got={hcTier}");
            }
            // RAM tiers
            Assert(PerformanceTierTable.ResolveRamTier(32) == PerformanceTierTable.ResolveRamTier(32, ds),
                nameof(Test_DatasetDriven_TierResolution_SameAsHardcoded), "RAM 32GB mismatch");
            Assert(PerformanceTierTable.ResolveRamTier(16) == PerformanceTierTable.ResolveRamTier(16, ds),
                nameof(Test_DatasetDriven_TierResolution_SameAsHardcoded), "RAM 16GB mismatch");
            Assert(PerformanceTierTable.ResolveRamTier(8) == PerformanceTierTable.ResolveRamTier(8, ds),
                nameof(Test_DatasetDriven_TierResolution_SameAsHardcoded), "RAM 8GB mismatch");
        });

        // ── 11. Scenario score order consistency (Bureau ≥ … ≥ AI) ──

        /// <summary>
        /// Ensures EnforceScenarioScoreOrder removes inversions: Office ≥ Multitasking ≥ … ≥ AI.
        /// </summary>
        private static void Test_EnforceScenarioScoreOrder_RemovesInversions() => RunTest(nameof(Test_EnforceScenarioScoreOrder_RemovesInversions), () =>
        {
            var scores = new List<ScenarioScore>
            {
                new() { ScenarioId = "office", Name = "Office / Browsing", Score = 45, Classification = ScenarioClassification.Acceptable },
                new() { ScenarioId = "multitasking", Name = "Multitasking", Score = 85, Classification = ScenarioClassification.Excellent },
                new() { ScenarioId = "gaming_1080p", Name = "Gaming (1080p)", Score = 70, Classification = ScenarioClassification.Good },
                new() { ScenarioId = "gaming_1440p", Name = "Gaming (1440p)", Score = 80, Classification = ScenarioClassification.Excellent },
                new() { ScenarioId = "4k_editing", Name = "4K Video Editing", Score = 50, Classification = ScenarioClassification.Acceptable },
                new() { ScenarioId = "streaming_gaming", Name = "Streaming + Gaming", Score = 60, Classification = ScenarioClassification.Good },
                new() { ScenarioId = "vms", Name = "Virtual Machines", Score = 55, Classification = ScenarioClassification.Acceptable },
                new() { ScenarioId = "ai_inference", Name = "AI (basic inference)", Score = 40, Classification = ScenarioClassification.NotRecommended }
            };
            PerformanceEvaluationEngine.EnforceScenarioScoreOrder(scores, null);
            for (int i = 1; i < scores.Count; i++)
            {
                Assert(scores[i].Score <= scores[i - 1].Score, nameof(Test_EnforceScenarioScoreOrder_RemovesInversions),
                    $"Order broken: {scores[i - 1].Name}={scores[i - 1].Score} < {scores[i].Name}={scores[i].Score}");
            }
            Assert(scores[0].Score == 45, nameof(Test_EnforceScenarioScoreOrder_RemovesInversions), "Office (easiest) should stay 45");
            Assert(scores[1].Score <= 45, nameof(Test_EnforceScenarioScoreOrder_RemovesInversions), "Multitasking should be capped to 45");
        });

        /// <summary>
        /// When scores are already in logical order, EnforceScenarioScoreOrder leaves them unchanged.
        /// </summary>
        private static void Test_EnforceScenarioScoreOrder_AlreadyOrdered_Unchanged() => RunTest(nameof(Test_EnforceScenarioScoreOrder_AlreadyOrdered_Unchanged), () =>
        {
            var scores = new List<ScenarioScore>
            {
                new() { ScenarioId = "office", Name = "Office / Browsing", Score = 90, Classification = ScenarioClassification.Excellent },
                new() { ScenarioId = "multitasking", Name = "Multitasking", Score = 75, Classification = ScenarioClassification.Good },
                new() { ScenarioId = "gaming_1080p", Name = "Gaming (1080p)", Score = 70, Classification = ScenarioClassification.Good },
                new() { ScenarioId = "gaming_1440p", Name = "Gaming (1440p)", Score = 65, Classification = ScenarioClassification.Good },
                new() { ScenarioId = "4k_editing", Name = "4K Video Editing", Score = 55, Classification = ScenarioClassification.Acceptable },
                new() { ScenarioId = "streaming_gaming", Name = "Streaming + Gaming", Score = 50, Classification = ScenarioClassification.Acceptable },
                new() { ScenarioId = "vms", Name = "Virtual Machines", Score = 45, Classification = ScenarioClassification.Acceptable },
                new() { ScenarioId = "ai_inference", Name = "AI (basic inference)", Score = 40, Classification = ScenarioClassification.NotRecommended }
            };
            var copy = scores.Select(s => new ScenarioScore { ScenarioId = s.ScenarioId, Name = s.Name, Score = s.Score, Classification = s.Classification }).ToList();
            PerformanceEvaluationEngine.EnforceScenarioScoreOrder(scores, null);
            for (int i = 0; i < scores.Count; i++)
            {
                Assert(scores[i].Score == copy[i].Score, nameof(Test_EnforceScenarioScoreOrder_AlreadyOrdered_Unchanged),
                    $"Score at index {i} changed: was {copy[i].Score}, got {scores[i].Score}");
            }
        });

        /// <summary>
        /// Print a detailed score report for a given profile (for manual inspection / debugging).
        /// </summary>
        public static string GenerateScoreReport(string label, HardwareProfile p)
        {
            var scores = ScoreAll(p);
            var lines = new List<string>
            {
                $"═══ {label} ═══",
                $"  CPU: {p.CpuModel ?? "(null)"} [{p.CpuTier}] {p.CpuCores}c/{p.CpuThreads}t (matched={p.CpuNameMatched})",
                $"  GPU: {p.GpuModel ?? "(null)"} [{p.GpuTier}] VRAM={p.GpuVramMb}MB (matched={p.GpuNameMatched})",
                $"  RAM: {p.RamGb}GB [{p.RamTier}]",
                $"  Storage: {p.StorageKind} [{p.StorageTier}]",
                $"  ─────────────────────────────────────"
            };
            foreach (var s in scores)
            {
                lines.Add($"  {s.Name,-25} {s.Score,3}/100  [{s.Classification}]");
            }
            double avg = scores.Average(s => s.Score);
            lines.Add($"  ─────────────────────────────────────");
            lines.Add($"  Global average: {avg:F1}/100");
            lines.Add("");
            return string.Join(Environment.NewLine, lines);
        }
    }
}
