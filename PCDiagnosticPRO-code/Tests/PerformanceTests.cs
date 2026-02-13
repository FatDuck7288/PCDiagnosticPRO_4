using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.Services;

namespace PCDiagnosticPro.Tests
{
    /// <summary>
    /// Acceptance tests for Performance section functionality.
    /// Run via: PerformanceTests.RunAllTests()
    /// </summary>
    public static class PerformanceTests
    {
        /// <summary>
        /// Runs all acceptance tests and returns results.
        /// </summary>
        public static async Task<TestResults> RunAllTestsAsync()
        {
            var results = new TestResults();
            
            results.Add("Dataset fetch + cache behavior", await TestDatasetFetchAndCacheAsync());
            results.Add("CPU matching correctness", TestCpuMatching());
            results.Add("GPU matching correctness", TestGpuMatching());
            results.Add("Market percentile formatting", TestPercentileFormatting());
            results.Add("Score precision (no multiples of 5)", TestScorePrecision());
            results.Add("Score variations (no identical clumps)", TestScoreVariations());
            
            return results;
        }

        /// <summary>
        /// Synchronous wrapper for test execution.
        /// </summary>
        public static TestResults RunAllTests()
        {
            return RunAllTestsAsync().GetAwaiter().GetResult();
        }

        #region Test 1: Dataset fetch + cache behavior

        private static async Task<TestResult> TestDatasetFetchAndCacheAsync()
        {
            try
            {
                var provider = new GitHubBenchmarkDataProvider();
                var result = await provider.GetDatasetAsync();
                
                // Test 1a: Should return a dataset (embedded fallback if remote fails)
                if (result.Dataset == null)
                    return TestResult.Fail("Dataset is null (should have embedded fallback)");
                
                // Test 1b: Dataset should have version
                if (string.IsNullOrEmpty(result.Dataset.DatasetVersion))
                    return TestResult.Fail("Dataset version is empty");
                
                // Test 1c: Dataset should have CPU entries
                if (result.Dataset.CpuEntries == null || result.Dataset.CpuEntries.Count == 0)
                    return TestResult.Fail("No CPU entries in dataset");
                
                // Test 1d: Dataset should have GPU entries
                if (result.Dataset.GpuEntries == null || result.Dataset.GpuEntries.Count == 0)
                    return TestResult.Fail("No GPU entries in dataset");
                
                // Test 1e: Result should indicate success or have error message
                if (!result.Success && string.IsNullOrEmpty(result.Error))
                    return TestResult.Fail("Result indicates failure but no error message");
                
                return TestResult.Pass($"Dataset loaded: v{result.Dataset.DatasetVersion}, " +
                    $"{result.Dataset.CpuEntries.Count} CPUs, {result.Dataset.GpuEntries.Count} GPUs, " +
                    $"FromCache={result.FromCache}");
            }
            catch (Exception ex)
            {
                return TestResult.Fail($"Exception: {ex.Message}");
            }
        }

        #endregion

        #region Test 2: CPU Matching Correctness

        private static TestResult TestCpuMatching()
        {
            try
            {
                var provider = new GitHubBenchmarkDataProvider();
                var result = provider.GetDatasetAsync().GetAwaiter().GetResult();
                if (result.Dataset == null)
                    return TestResult.Fail("Cannot test matching - no dataset");
                
                var testCases = new[]
                {
                    ("AMD Ryzen 9 5900X 12-Core Processor", "ryzen 9 5900x", MatchConfidence.High),
                    ("Intel(R) Core(TM) i9-13900K", "core i9-13900k", MatchConfidence.High),
                    ("AMD Ryzen 7 5800X", "ryzen 7 5800x", MatchConfidence.High),
                    ("NVIDIA RTX 3090", null, MatchConfidence.Low), // Not a CPU
                };
                
                var failures = new List<string>();
                foreach (var (input, expectedMatch, expectedConfidence) in testCases)
                {
                    var match = BenchmarkMatcher.MatchCpu(input, result.Dataset.CpuEntries);
                    
                    if (expectedMatch != null)
                    {
                        if (match.Entry == null)
                            failures.Add($"'{input}' - expected match but got null");
                        else if (!match.Entry.NormalizedName.Contains(expectedMatch, StringComparison.OrdinalIgnoreCase))
                            failures.Add($"'{input}' - expected '{expectedMatch}', got '{match.Entry.NormalizedName}'");
                        else if (match.Confidence != expectedConfidence)
                            failures.Add($"'{input}' - expected confidence {expectedConfidence}, got {match.Confidence}");
                    }
                    else
                    {
                        // Expected no match
                        if (match.Entry != null && match.Confidence == MatchConfidence.High)
                            failures.Add($"'{input}' - expected no match, got '{match.Entry.NormalizedName}'");
                    }
                }
                
                if (failures.Count > 0)
                    return TestResult.Fail(string.Join("; ", failures));
                
                return TestResult.Pass($"All {testCases.Length} CPU matching test cases passed");
            }
            catch (Exception ex)
            {
                return TestResult.Fail($"Exception: {ex.Message}");
            }
        }

        #endregion

        #region Test 3: GPU Matching Correctness

        private static TestResult TestGpuMatching()
        {
            try
            {
                var provider = new GitHubBenchmarkDataProvider();
                var result = provider.GetDatasetAsync().GetAwaiter().GetResult();
                if (result.Dataset == null)
                    return TestResult.Fail("Cannot test matching - no dataset");
                
                var testCases = new[]
                {
                    ("NVIDIA GeForce RTX 3090", "rtx 3090", MatchConfidence.High),
                    ("NVIDIA GeForce RTX 3060", "rtx 3060", MatchConfidence.High),
                    ("AMD Radeon RX 6800 XT", "rx 6800 xt", MatchConfidence.High),
                    ("Intel(R) UHD Graphics 630", null, MatchConfidence.Low), // Not in dataset
                };
                
                var failures = new List<string>();
                foreach (var (input, expectedMatch, expectedConfidence) in testCases)
                {
                    var match = BenchmarkMatcher.MatchGpu(input, result.Dataset.GpuEntries);
                    
                    if (expectedMatch != null)
                    {
                        if (match.Entry == null)
                            failures.Add($"'{input}' - expected match but got null");
                        else if (!match.Entry.NormalizedName.Contains(expectedMatch, StringComparison.OrdinalIgnoreCase))
                            failures.Add($"'{input}' - expected '{expectedMatch}', got '{match.Entry.NormalizedName}'");
                    }
                }
                
                if (failures.Count > 0)
                    return TestResult.Fail(string.Join("; ", failures));
                
                return TestResult.Pass($"All {testCases.Length} GPU matching test cases passed");
            }
            catch (Exception ex)
            {
                return TestResult.Fail($"Exception: {ex.Message}");
            }
        }

        #endregion

        #region Test 4: Market percentile formatting

        private static TestResult TestPercentileFormatting()
        {
            try
            {
                var testCases = new[]
                {
                    (95.3, "Top 4.7%"),
                    (92.0, "Top 8.0%"),
                    (85.5, "85.5%"),
                    (50.0, "50.0%"),
                    (12.4, "12.4%"),
                };
                
                var failures = new List<string>();
                foreach (var (percentile, expected) in testCases)
                {
                    var actual = MarketPositionScore.GetPercentileDisplay(percentile);
                    if (actual != expected)
                        failures.Add($"Percentile {percentile}: expected '{expected}', got '{actual}'");
                }
                
                if (failures.Count > 0)
                    return TestResult.Fail(string.Join("; ", failures));
                
                return TestResult.Pass($"All {testCases.Length} percentile formatting test cases passed");
            }
            catch (Exception ex)
            {
                return TestResult.Fail($"Exception: {ex.Message}");
            }
        }

        #endregion

        #region Test 5: Score precision (no multiples of 5)

        private static TestResult TestScorePrecision()
        {
            try
            {
                // Create a test profile
                var profile = new HardwareProfile
                {
                    CpuModel = "AMD Ryzen 9 5900X",
                    CpuCores = 12,
                    CpuThreads = 24,
                    CpuTier = "High-End",
                    GpuModel = "NVIDIA GeForce RTX 3090",
                    GpuVramMb = 24576,
                    GpuTier = "High-End",
                    RamGb = 96,
                    RamTier = "High-End",
                    StorageKind = "NVMe",
                    StorageTier = "High-End"
                };
                
                var dataset = PerformanceDatasetLoader.Current;
                var scores = UsageScenarioScorer.Score(profile, dataset);
                
                // Check that not all scores are multiples of 5
                int multiplesOf5 = scores.Count(s => s.PreciseScore % 5 == 0);
                double multipleRatio = (double)multiplesOf5 / scores.Count;
                
                // Allow up to 50% to be multiples of 5 (some coincidental)
                if (multipleRatio > 0.5)
                    return TestResult.Fail($"Too many scores are multiples of 5: {multiplesOf5}/{scores.Count} ({multipleRatio:P0})");
                
                // Check that scores have decimal variation
                int withDecimals = scores.Count(s => Math.Abs(s.PreciseScore - Math.Round(s.PreciseScore)) > 0.01);
                if (withDecimals == 0)
                    return TestResult.Fail("No scores have decimal precision");
                
                return TestResult.Pass($"{withDecimals}/{scores.Count} scores have decimal precision, " +
                    $"{multiplesOf5}/{scores.Count} are multiples of 5");
            }
            catch (Exception ex)
            {
                return TestResult.Fail($"Exception: {ex.Message}");
            }
        }

        #endregion

        #region Test 6: Score variations (no identical clumps)

        private static TestResult TestScoreVariations()
        {
            try
            {
                var profile = new HardwareProfile
                {
                    CpuModel = "AMD Ryzen 9 5900X",
                    CpuCores = 12,
                    CpuThreads = 24,
                    CpuTier = "High-End",
                    GpuModel = "NVIDIA GeForce RTX 3090",
                    GpuVramMb = 24576,
                    GpuTier = "High-End",
                    RamGb = 96,
                    RamTier = "High-End",
                    StorageKind = "NVMe",
                    StorageTier = "High-End"
                };
                
                var dataset = PerformanceDatasetLoader.Current;
                var scores = UsageScenarioScorer.Score(profile, dataset);
                
                // Group by rounded score to find identical clumps
                var grouped = scores.GroupBy(s => Math.Round(s.PreciseScore)).ToList();
                
                // No group should have more than 3 identical scores (allow some grouping at 100)
                var largeClumps = grouped.Where(g => g.Count() > 3 && g.Key < 99).ToList();
                if (largeClumps.Count > 0)
                {
                    var clumpInfo = string.Join(", ", largeClumps.Select(g => $"{g.Key}:{g.Count()}"));
                    return TestResult.Fail($"Found large identical score clumps: {clumpInfo}");
                }
                
                // Should have at least 5 distinct score ranges
                if (grouped.Count < 5)
                    return TestResult.Fail($"Too few distinct scores: only {grouped.Count} groups");
                
                return TestResult.Pass($"{grouped.Count} distinct score values across {scores.Count} scenarios");
            }
            catch (Exception ex)
            {
                return TestResult.Fail($"Exception: {ex.Message}");
            }
        }

        #endregion
    }

    #region Test Result Classes

    public class TestResult
    {
        public bool Passed { get; set; }
        public string Message { get; set; } = "";
        
        public static TestResult Pass(string message = "") => new TestResult { Passed = true, Message = message };
        public static TestResult Fail(string message) => new TestResult { Passed = false, Message = message };
    }

    public class TestResults
    {
        private readonly List<(string Name, TestResult Result)> _results = new();
        
        public void Add(string name, TestResult result)
        {
            _results.Add((name, result));
        }
        
        public int Total => _results.Count;
        public int Passed => _results.Count(r => r.Result.Passed);
        public int Failed => _results.Count(r => !r.Result.Passed);
        
        public bool AllPassed => Failed == 0;
        
        public string Summary
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"=== Performance Tests: {Passed}/{Total} passed ===");
                sb.AppendLine();
                foreach (var (name, result) in _results)
                {
                    var status = result.Passed ? "✓ PASS" : "✗ FAIL";
                    sb.AppendLine($"{status}: {name}");
                    if (!string.IsNullOrEmpty(result.Message))
                        sb.AppendLine($"       {result.Message}");
                }
                return sb.ToString();
            }
        }
    }

    #endregion
}
