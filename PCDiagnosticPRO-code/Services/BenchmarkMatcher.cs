using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Matches hardware names to benchmark entries using robust normalization.
    /// Returns match confidence and handles fuzzy matching.
    /// </summary>
    public static class BenchmarkMatcher
    {
        /// <summary>
        /// Result of a benchmark match operation.
        /// </summary>
        public class MatchResult<T> where T : class
        {
            public T? Entry { get; set; }
            public MatchConfidence Confidence { get; set; } = MatchConfidence.Low;
            public string MatchedName { get; set; } = "";
            public double MatchScore { get; set; }
        }

        /// <summary>
        /// Find the best matching CPU entry for the given hardware name.
        /// </summary>
        public static MatchResult<CpuBenchmarkEntry> MatchCpu(string hardwareName, IEnumerable<CpuBenchmarkEntry> entries)
        {
            if (string.IsNullOrEmpty(hardwareName) || entries == null)
                return new MatchResult<CpuBenchmarkEntry>();

            var normalized = NormalizeHardwareName(hardwareName);
            
            // Try exact match first
            foreach (var entry in entries)
            {
                if (string.Equals(normalized, entry.NormalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    return new MatchResult<CpuBenchmarkEntry>
                    {
                        Entry = entry,
                        Confidence = MatchConfidence.High,
                        MatchedName = entry.NormalizedName,
                        MatchScore = 1.0
                    };
                }

                // Check alternative names
                foreach (var alt in entry.AlternativeNames ?? new List<string>())
                {
                    if (normalized.Contains(alt, StringComparison.OrdinalIgnoreCase) ||
                        alt.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return new MatchResult<CpuBenchmarkEntry>
                        {
                            Entry = entry,
                            Confidence = MatchConfidence.High,
                            MatchedName = entry.NormalizedName,
                            MatchScore = 0.95
                        };
                    }
                }
            }

            // Try fuzzy matching (substring contains)
            var bestMatch = FindBestSubstringMatch(normalized, entries, e => e.NormalizedName, e => e.AlternativeNames);
            if (bestMatch.entry != null)
            {
                return new MatchResult<CpuBenchmarkEntry>
                {
                    Entry = bestMatch.entry,
                    Confidence = bestMatch.score > 0.7 ? MatchConfidence.Medium : MatchConfidence.Low,
                    MatchedName = bestMatch.entry.NormalizedName,
                    MatchScore = bestMatch.score
                };
            }

            return new MatchResult<CpuBenchmarkEntry>();
        }

        /// <summary>
        /// Find the best matching GPU entry for the given hardware name.
        /// </summary>
        public static MatchResult<GpuBenchmarkEntry> MatchGpu(string hardwareName, IEnumerable<GpuBenchmarkEntry> entries)
        {
            if (string.IsNullOrEmpty(hardwareName) || entries == null)
                return new MatchResult<GpuBenchmarkEntry>();

            var normalized = NormalizeHardwareName(hardwareName);

            // Try exact match first
            foreach (var entry in entries)
            {
                if (string.Equals(normalized, entry.NormalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    return new MatchResult<GpuBenchmarkEntry>
                    {
                        Entry = entry,
                        Confidence = MatchConfidence.High,
                        MatchedName = entry.NormalizedName,
                        MatchScore = 1.0
                    };
                }

                // Check alternative names
                foreach (var alt in entry.AlternativeNames ?? new List<string>())
                {
                    if (normalized.Contains(alt, StringComparison.OrdinalIgnoreCase) ||
                        alt.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return new MatchResult<GpuBenchmarkEntry>
                        {
                            Entry = entry,
                            Confidence = MatchConfidence.High,
                            MatchedName = entry.NormalizedName,
                            MatchScore = 0.95
                        };
                    }
                }
            }

            // Try fuzzy matching
            var bestMatch = FindBestSubstringMatch(normalized, entries, e => e.NormalizedName, e => e.AlternativeNames);
            if (bestMatch.entry != null)
            {
                return new MatchResult<GpuBenchmarkEntry>
                {
                    Entry = bestMatch.entry,
                    Confidence = bestMatch.score > 0.7 ? MatchConfidence.Medium : MatchConfidence.Low,
                    MatchedName = bestMatch.entry.NormalizedName,
                    MatchScore = bestMatch.score
                };
            }

            return new MatchResult<GpuBenchmarkEntry>();
        }

        /// <summary>
        /// Calculate RAM percentile from GB amount using baseline mappings.
        /// </summary>
        public static (double percentile, MatchConfidence confidence) CalculateRamPercentile(double ramGb, RamBenchmarkBaseline? baseline)
        {
            if (ramGb <= 0)
                return (10.0, MatchConfidence.Low);

            if (baseline?.Mappings == null || baseline.Mappings.Count == 0)
            {
                // Default mapping
                if (ramGb >= 128) return (98.0, MatchConfidence.Medium);
                if (ramGb >= 64) return (90.0, MatchConfidence.Medium);
                if (ramGb >= 32) return (75.0, MatchConfidence.Medium);
                if (ramGb >= 16) return (55.0, MatchConfidence.Medium);
                if (ramGb >= 8) return (30.0, MatchConfidence.Medium);
                return (10.0, MatchConfidence.Medium);
            }

            // Find the appropriate mapping range and interpolate
            var sortedMappings = baseline.Mappings.OrderBy(m => m.MinGb).ToList();
            
            for (int i = 0; i < sortedMappings.Count; i++)
            {
                if (ramGb < sortedMappings[i].MinGb)
                {
                    if (i == 0)
                        return (sortedMappings[0].Percentile * ramGb / sortedMappings[0].MinGb, MatchConfidence.High);
                    
                    // Interpolate between this and previous
                    var prev = sortedMappings[i - 1];
                    var curr = sortedMappings[i];
                    var ratio = (ramGb - prev.MinGb) / (curr.MinGb - prev.MinGb);
                    var percentile = prev.Percentile + (curr.Percentile - prev.Percentile) * ratio;
                    return (Math.Round(percentile, 1), MatchConfidence.High);
                }
            }

            // Above all mappings
            var last = sortedMappings.Last();
            var extraRatio = Math.Min(1.0, (ramGb - last.MinGb) / last.MinGb);
            return (Math.Min(99.5, last.Percentile + (100 - last.Percentile) * extraRatio), MatchConfidence.High);
        }

        /// <summary>
        /// Calculate storage percentile based on type.
        /// </summary>
        public static (double percentile, MatchConfidence confidence) CalculateStoragePercentile(string storageKind, StorageBenchmarkBaseline? baseline)
        {
            if (string.IsNullOrEmpty(storageKind))
                return (15.0, MatchConfidence.Low);

            var baselineToUse = baseline ?? new StorageBenchmarkBaseline();
            var normalizedKind = storageKind.ToUpperInvariant();

            if (normalizedKind.Contains("NVME") || normalizedKind.Contains("PCIE"))
            {
                // Differentiate Gen4/Gen5 if we have that info
                if (normalizedKind.Contains("GEN4") || normalizedKind.Contains("GEN5"))
                    return (baselineToUse.NvmeGen4Percentile, MatchConfidence.High);
                return (baselineToUse.NvmePercentile, MatchConfidence.High);
            }

            if (normalizedKind.Contains("SATA") && normalizedKind.Contains("SSD"))
                return (baselineToUse.SataSsdPercentile, MatchConfidence.High);

            if (normalizedKind.Contains("SSD"))
                return (baselineToUse.SataSsdPercentile, MatchConfidence.Medium);

            if (normalizedKind.Contains("HDD"))
                return (baselineToUse.HddPercentile, MatchConfidence.High);

            // Unknown
            return (baselineToUse.SataSsdPercentile, MatchConfidence.Low);
        }

        #region Normalization

        /// <summary>
        /// Normalize hardware name for matching.
        /// - Lowercase
        /// - Remove (R), (TM), ®, ™
        /// - Remove common prefixes (NVIDIA GeForce, AMD Radeon, Intel Core, etc.)
        /// - Collapse multiple spaces
        /// - Trim
        /// </summary>
        public static string NormalizeHardwareName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "";

            var result = name.ToLowerInvariant();

            // Remove trademark symbols
            result = Regex.Replace(result, @"\(r\)|\(tm\)|®|™", "", RegexOptions.IgnoreCase);

            // Remove common vendor prefixes
            var prefixes = new[]
            {
                "nvidia geforce", "nvidia", "geforce",
                "amd radeon", "amd", "radeon",
                "intel core", "intel",
                "microsoft basic display adapter",
                "qualcomm",
                "-core processor", "processor"
            };

            foreach (var prefix in prefixes)
            {
                result = Regex.Replace(result, $@"^\s*{Regex.Escape(prefix)}\s*", "", RegexOptions.IgnoreCase);
                result = Regex.Replace(result, $@"\s*{Regex.Escape(prefix)}\s*$", "", RegexOptions.IgnoreCase);
            }

            // Collapse multiple spaces
            result = Regex.Replace(result, @"\s+", " ");

            // Trim
            result = result.Trim();

            return result;
        }

        #endregion

        #region Fuzzy Matching

        private static (T? entry, double score) FindBestSubstringMatch<T>(
            string normalized,
            IEnumerable<T> entries,
            Func<T, string> getName,
            Func<T, List<string>?> getAlternatives) where T : class
        {
            T? bestEntry = null;
            double bestScore = 0;

            foreach (var entry in entries)
            {
                var entryName = getName(entry);
                
                // Check main name
                var score = CalculateMatchScore(normalized, entryName);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestEntry = entry;
                }

                // Check alternatives
                var alts = getAlternatives(entry);
                if (alts != null)
                {
                    foreach (var alt in alts)
                    {
                        score = CalculateMatchScore(normalized, alt);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestEntry = entry;
                        }
                    }
                }
            }

            return (bestEntry, bestScore);
        }

        private static double CalculateMatchScore(string input, string target)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(target))
                return 0;

            var inputNorm = input.ToLowerInvariant();
            var targetNorm = target.ToLowerInvariant();

            // Exact match
            if (inputNorm == targetNorm)
                return 1.0;

            // One contains the other
            if (inputNorm.Contains(targetNorm))
                return 0.9 * targetNorm.Length / inputNorm.Length;
            if (targetNorm.Contains(inputNorm))
                return 0.9 * inputNorm.Length / targetNorm.Length;

            // Extract model numbers and compare
            var inputNumbers = Regex.Matches(inputNorm, @"\d+");
            var targetNumbers = Regex.Matches(targetNorm, @"\d+");

            if (inputNumbers.Count > 0 && targetNumbers.Count > 0)
            {
                // Check if key model numbers match
                var inputMainNumber = inputNumbers.Cast<Match>().OrderByDescending(m => m.Length).FirstOrDefault()?.Value;
                var targetMainNumber = targetNumbers.Cast<Match>().OrderByDescending(m => m.Length).FirstOrDefault()?.Value;

                if (inputMainNumber == targetMainNumber && !string.IsNullOrEmpty(inputMainNumber))
                {
                    return 0.75;
                }
            }

            // Levenshtein-based similarity (simplified)
            var maxLen = Math.Max(inputNorm.Length, targetNorm.Length);
            var commonChars = inputNorm.Intersect(targetNorm).Count();
            return 0.3 * commonChars / maxLen;
        }

        #endregion
    }
}
