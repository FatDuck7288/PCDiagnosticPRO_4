using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Explains how a score was calculated, providing transparency and reproducibility.
    /// </summary>
    public class ScoreExplanation
    {
        /// <summary>
        /// The final computed score (with decimal precision).
        /// </summary>
        [JsonPropertyName("finalScore")]
        public double FinalScore { get; set; }

        /// <summary>
        /// Scoring method used ("MarketBenchmark", "TierBased", "Hybrid").
        /// </summary>
        [JsonPropertyName("method")]
        public string Method { get; set; } = "MarketBenchmark";

        /// <summary>
        /// Individual component contributions to the score.
        /// </summary>
        [JsonPropertyName("components")]
        public List<ScoreComponent> Components { get; set; } = new();

        /// <summary>
        /// Data source used for scoring.
        /// </summary>
        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        /// <summary>
        /// Confidence level of the overall score.
        /// </summary>
        [JsonPropertyName("confidence")]
        public MatchConfidence Confidence { get; set; } = MatchConfidence.Medium;

        /// <summary>
        /// Generate a human-readable explanation string.
        /// Example: "Gaming 1440p = 92.4 because GPU percentile 95.1% (weight 0.55), VRAM 24GB (+4.2), CPU percentile 83.0% (weight 0.20), RAM 96GB (+1.1)"
        /// </summary>
        public string ToDisplayString(string scenarioName)
        {
            if (Components.Count == 0)
                return $"{scenarioName} = {FinalScore:F1} (méthode: {Method})";

            var sb = new StringBuilder();
            sb.Append($"{scenarioName} = {FinalScore:F1} car ");

            var parts = new List<string>();
            foreach (var comp in Components)
            {
                if (comp.Weight > 0)
                {
                    parts.Add($"{comp.Name} {comp.InputValue} (poids {comp.Weight:F2}, contrib. {comp.Contribution:F1})");
                }
                else if (comp.Contribution != 0)
                {
                    var sign = comp.Contribution > 0 ? "+" : "";
                    parts.Add($"{comp.Name} {comp.InputValue} ({sign}{comp.Contribution:F1})");
                }
            }

            sb.Append(string.Join(", ", parts));
            return sb.ToString();
        }

        /// <summary>
        /// Generate a short tooltip-friendly explanation.
        /// </summary>
        public string ToTooltip()
        {
            if (Components.Count == 0)
                return $"Score: {FinalScore:F1}\nMéthode: {Method}\nSource: {Source}";

            var sb = new StringBuilder();
            sb.AppendLine($"Score: {FinalScore:F1}");
            sb.AppendLine($"Méthode: {Method}");
            sb.AppendLine("Composantes:");
            
            foreach (var comp in Components)
            {
                if (comp.Weight > 0)
                    sb.AppendLine($"  • {comp.Name}: {comp.InputValue} → {comp.SubScore:F1} × {comp.Weight:F2} = {comp.Contribution:F1}");
                else if (comp.Contribution != 0)
                    sb.AppendLine($"  • {comp.Name}: {comp.InputValue} ({(comp.Contribution >= 0 ? "+" : "")}{comp.Contribution:F1})");
            }

            sb.AppendLine($"Source: {Source}");
            sb.Append($"Confiance: {ConfidenceToString(Confidence)}");
            return sb.ToString();
        }

        private static string ConfidenceToString(MatchConfidence c) => c switch
        {
            MatchConfidence.High => "Élevée",
            MatchConfidence.Medium => "Moyenne",
            MatchConfidence.Low => "Faible",
            _ => "Inconnue"
        };
    }

    /// <summary>
    /// Represents a single component's contribution to a score.
    /// </summary>
    public class ScoreComponent
    {
        /// <summary>
        /// Component name (e.g., "CPU", "GPU", "RAM", "Stockage", "VRAM").
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        /// <summary>
        /// The input value used (e.g., "12 cores", "24576 MB", "96 GB").
        /// </summary>
        [JsonPropertyName("inputValue")]
        public string InputValue { get; set; } = "";

        /// <summary>
        /// The raw numeric input (for calculations).
        /// </summary>
        [JsonPropertyName("rawInput")]
        public double RawInput { get; set; }

        /// <summary>
        /// The sub-score for this component (0-100).
        /// </summary>
        [JsonPropertyName("subScore")]
        public double SubScore { get; set; }

        /// <summary>
        /// Weight of this component in the final score (0-1).
        /// </summary>
        [JsonPropertyName("weight")]
        public double Weight { get; set; }

        /// <summary>
        /// Actual contribution to the final score (subScore × weight, or bonus points).
        /// </summary>
        [JsonPropertyName("contribution")]
        public double Contribution { get; set; }

        /// <summary>
        /// Percentile for this component (if applicable).
        /// </summary>
        [JsonPropertyName("percentile")]
        public double? Percentile { get; set; }

        /// <summary>
        /// Create a weighted component.
        /// </summary>
        public static ScoreComponent Weighted(string name, string inputValue, double rawInput, double subScore, double weight)
        {
            return new ScoreComponent
            {
                Name = name,
                InputValue = inputValue,
                RawInput = rawInput,
                SubScore = subScore,
                Weight = weight,
                Contribution = subScore * weight
            };
        }

        /// <summary>
        /// Create a bonus component (fixed points).
        /// </summary>
        public static ScoreComponent Bonus(string name, string inputValue, double contribution)
        {
            return new ScoreComponent
            {
                Name = name,
                InputValue = inputValue,
                Contribution = contribution
            };
        }

        /// <summary>
        /// Create a percentile-based component.
        /// </summary>
        public static ScoreComponent FromPercentile(string name, string inputValue, double percentile, double weight)
        {
            return new ScoreComponent
            {
                Name = name,
                InputValue = inputValue,
                Percentile = percentile,
                SubScore = percentile,
                Weight = weight,
                Contribution = percentile * weight
            };
        }
    }
}
