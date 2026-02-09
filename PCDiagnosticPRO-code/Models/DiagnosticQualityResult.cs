using System;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Métriques de qualité de collecte (section diagnostics_quality dans JSON combiné + log).
    /// Objectif: Coverage ≥ 90%, Reliability ≥ 90%, Actionability ≥ 80%.
    /// </summary>
    public class DiagnosticQualityResult
    {
        [JsonPropertyName("coverage")]
        public int CoverageScore { get; set; }

        [JsonPropertyName("reliability")]
        public int ReliabilityScore { get; set; }

        [JsonPropertyName("actionability")]
        public int ActionabilityScore { get; set; }

        [JsonPropertyName("overall")]
        public int OverallScore { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("coverageDetails")]
        public string CoverageDetails { get; set; } = "";

        [JsonPropertyName("reliabilityDetails")]
        public string ReliabilityDetails { get; set; } = "";

        [JsonPropertyName("actionabilityDetails")]
        public string ActionabilityDetails { get; set; } = "";

        [JsonPropertyName("timestampUtc")]
        public DateTime TimestampUtc { get; set; }
    }
}
