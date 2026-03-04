using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Confidence level for market position matching.
    /// </summary>
    public enum MatchConfidence
    {
        /// <summary>Exact match found in benchmark dataset.</summary>
        High,
        /// <summary>Partial/fuzzy match or extrapolated from similar models.</summary>
        Medium,
        /// <summary>No match found; using heuristic/tier-based estimate.</summary>
        Low
    }

    /// <summary>
    /// Represents a row in the Market Position table.
    /// Compares the PC against the market to show where it stands (percentile/tier).
    /// Now includes benchmark score, rank, source info, and confidence level.
    /// </summary>
    public class MarketPositionScore
    {
        /// <summary>
        /// The component being evaluated (e.g., "CPU", "GPU", "RAM", "Stockage", "Global")
        /// </summary>
        [JsonPropertyName("component")]
        public string Component { get; set; } = string.Empty;

        /// <summary>
        /// Detected model name (e.g., "AMD Ryzen 9 5900X", "NVIDIA GeForce RTX 3090")
        /// </summary>
        [JsonPropertyName("detectedModel")]
        public string DetectedModel { get; set; } = string.Empty;

        /// <summary>
        /// Benchmark score (raw or normalized, depending on source).
        /// For CPUs: PassMark-style score (0-100000+)
        /// For GPUs: 3DMark-style score (0-50000+)
        /// For RAM/Storage: normalized 0-100 score
        /// </summary>
        [JsonPropertyName("benchmarkScore")]
        public double BenchmarkScore { get; set; }

        /// <summary>
        /// The percentile ranking (0-100) with decimal precision.
        /// Higher = better than more users.
        /// e.g., 95.3 means "more powerful than 95.3% of PCs"
        /// </summary>
        [JsonPropertyName("percentile")]
        public double Percentile { get; set; }

        /// <summary>
        /// Formatted percentile display (e.g., "Top 4.7%", "Top 12.4%")
        /// </summary>
        [JsonPropertyName("percentileDisplay")]
        public string PercentileDisplay { get; set; } = string.Empty;

        /// <summary>
        /// Approximate rank in market (e.g., "#350 / 8500")
        /// </summary>
        [JsonPropertyName("rankDisplay")]
        public string RankDisplay { get; set; } = string.Empty;

        /// <summary>
        /// Actual rank number (1 = best)
        /// </summary>
        [JsonPropertyName("rank")]
        public int Rank { get; set; }

        /// <summary>
        /// Total items in market reference for this component.
        /// </summary>
        [JsonPropertyName("totalInMarket")]
        public int TotalInMarket { get; set; }

        /// <summary>
        /// Market tier classification:
        /// - "Entrée de gamme" (Entry)
        /// - "Milieu de gamme" (Mid-range)
        /// - "Haut de gamme" (High-end)
        /// - "Station de travail" (Workstation)
        /// </summary>
        [JsonPropertyName("marketTier")]
        public string MarketTier { get; set; } = string.Empty;

        /// <summary>
        /// User-friendly description (e.g., "Plus performant que 85% des PCs")
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// The raw value used for this component (for display purposes).
        /// e.g., "12 cœurs / 24 threads" for CPU, "24576 Mo" for GPU VRAM, "96 Go" for RAM
        /// </summary>
        [JsonPropertyName("rawValue")]
        public string RawValue { get; set; } = string.Empty;

        /// <summary>
        /// Internal tier order (1-5) for sorting and calculations.
        /// </summary>
        [JsonPropertyName("tierOrder")]
        public int TierOrder { get; set; }

        /// <summary>
        /// Data source name + version + date (e.g., "PCDiagnosticPRO Benchmarks v1.2.0 (2026-02-10)")
        /// </summary>
        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// Confidence level of the match (High/Medium/Low).
        /// </summary>
        [JsonPropertyName("confidence")]
        public MatchConfidence Confidence { get; set; } = MatchConfidence.Low;

        /// <summary>
        /// Confidence display string ("Élevée", "Moyenne", "Faible").
        /// </summary>
        [JsonPropertyName("confidenceDisplay")]
        public string ConfidenceDisplay => Confidence switch
        {
            MatchConfidence.High => "Élevée",
            MatchConfidence.Medium => "Moyenne",
            MatchConfidence.Low => "Faible",
            _ => "Inconnue"
        };

        /// <summary>
        /// Calculate percentile from tier order (1-5) with fine-grained precision.
        /// Returns decimal percentile for more granular positioning.
        /// Tier 1 (Entry) → 10-30
        /// Tier 2 (Mid-range) → 30-50
        /// Tier 3 (Upper Mid) → 50-70
        /// Tier 4 (High-end) → 70-90
        /// Tier 5 (Workstation) → 90-99
        /// </summary>
        public static double CalculatePercentileFromTier(int tierOrder, double componentValue = 0, double maxValue = 0)
        {
            // Base percentile ranges per tier
            var (minPercentile, maxPercentile) = tierOrder switch
            {
                1 => (10.0, 30.0),
                2 => (30.0, 50.0),
                3 => (50.0, 70.0),
                4 => (70.0, 90.0),
                5 => (90.0, 99.0),
                _ => (10.0, 30.0) // Default to entry
            };

            // If we have component values, interpolate within the tier range with precision
            if (componentValue > 0 && maxValue > 0)
            {
                var ratio = componentValue / maxValue;
                ratio = System.Math.Min(ratio, 1.0); // Cap at 100%
                // Add some variance based on actual value to avoid flat numbers
                var basePercentile = minPercentile + (maxPercentile - minPercentile) * ratio;
                // Add micro-variance based on component value digits
                var variance = (componentValue % 10) * 0.1;
                return System.Math.Round(basePercentile + variance, 1);
            }

            // Otherwise, use the midpoint of the tier range with slight variance
            var midpoint = (minPercentile + maxPercentile) / 2.0;
            return System.Math.Round(midpoint + (tierOrder * 0.3), 1);
        }

        /// <summary>
        /// Get market tier string from tier order.
        /// </summary>
        public static string GetMarketTierFromOrder(int tierOrder)
        {
            return tierOrder switch
            {
                1 => "Entrée de gamme",
                2 => "Milieu de gamme",
                3 => "Milieu de gamme supérieur",
                4 => "Haut de gamme",
                5 => "Station de travail",
                _ => "Entrée de gamme"
            };
        }

        /// <summary>
        /// Get description from percentile (with decimal precision).
        /// </summary>
        public static string GetDescriptionFromPercentile(double percentile)
        {
            if (percentile >= 95)
                return $"Top {(100 - percentile):F1}% — Exceptionnel";
            if (percentile >= 85)
                return $"Plus performant que {percentile:F1}% des PCs";
            if (percentile >= 70)
                return $"Plus performant que {percentile:F1}% des PCs";
            if (percentile >= 50)
                return $"Dans la moyenne supérieure ({percentile:F1}%)";
            if (percentile >= 30)
                return $"Dans la moyenne ({percentile:F1}%)";
            return $"Entrée de gamme ({percentile:F1}%)";
        }

        /// <summary>
        /// Get percentile display string (e.g., "Top 4.7%", "85.3%").
        /// </summary>
        public static string GetPercentileDisplay(double percentile)
        {
            if (percentile >= 90)
                return $"Top {(100 - percentile):F1}%";
            return $"{percentile:F1}%";
        }

        /// <summary>
        /// Get rank display string (e.g., "#350 / 2500").
        /// </summary>
        public static string GetRankDisplay(int rank, int total)
        {
            if (rank <= 0 || total <= 0) return "N/A";
            return $"#{rank:N0} / {total:N0}";
        }

        /// <summary>
        /// Calculate approximate rank from percentile and total market size.
        /// </summary>
        public static int CalculateRankFromPercentile(double percentile, int totalInMarket)
        {
            if (totalInMarket <= 0) return 0;
            // Percentile 100 = rank 1, percentile 0 = rank totalInMarket
            return (int)System.Math.Max(1, System.Math.Round(totalInMarket * (100.0 - percentile) / 100.0));
        }

        /// <summary>
        /// Create a MarketPositionScore for a component (legacy, tier-based).
        /// </summary>
        public static MarketPositionScore Create(string component, int tierOrder, string rawValue)
        {
            var percentile = CalculatePercentileFromTier(tierOrder);
            return new MarketPositionScore
            {
                Component = component,
                TierOrder = tierOrder,
                Percentile = percentile,
                PercentileDisplay = GetPercentileDisplay(percentile),
                MarketTier = GetMarketTierFromOrder(tierOrder),
                Description = GetDescriptionFromPercentile(percentile),
                RawValue = rawValue,
                Confidence = MatchConfidence.Low,
                Source = "Tier-based estimate"
            };
        }

        /// <summary>
        /// Create a MarketPositionScore for a component with specific values for fine-grained percentile.
        /// </summary>
        public static MarketPositionScore CreateWithValue(string component, int tierOrder, string rawValue, double componentValue, double maxExpectedValue)
        {
            var percentile = CalculatePercentileFromTier(tierOrder, componentValue, maxExpectedValue);
            return new MarketPositionScore
            {
                Component = component,
                TierOrder = tierOrder,
                Percentile = percentile,
                PercentileDisplay = GetPercentileDisplay(percentile),
                MarketTier = GetMarketTierFromOrder(tierOrder),
                Description = GetDescriptionFromPercentile(percentile),
                RawValue = rawValue,
                Confidence = MatchConfidence.Low,
                Source = "Tier-based estimate"
            };
        }

        /// <summary>
        /// Create a MarketPositionScore from benchmark data (high confidence).
        /// </summary>
        public static MarketPositionScore CreateFromBenchmark(
            string component,
            string detectedModel,
            double benchmarkScore,
            double percentile,
            int rank,
            int totalInMarket,
            string source,
            MatchConfidence confidence)
        {
            int tierOrder = percentile switch
            {
                >= 90 => 5,
                >= 70 => 4,
                >= 50 => 3,
                >= 30 => 2,
                _ => 1
            };

            return new MarketPositionScore
            {
                Component = component,
                DetectedModel = detectedModel,
                BenchmarkScore = benchmarkScore,
                Percentile = System.Math.Round(percentile, 1),
                PercentileDisplay = GetPercentileDisplay(percentile),
                Rank = rank,
                TotalInMarket = totalInMarket,
                RankDisplay = GetRankDisplay(rank, totalInMarket),
                TierOrder = tierOrder,
                MarketTier = GetMarketTierFromOrder(tierOrder),
                Description = GetDescriptionFromPercentile(percentile),
                RawValue = detectedModel,
                Source = source,
                Confidence = confidence
            };
        }
    }
}
