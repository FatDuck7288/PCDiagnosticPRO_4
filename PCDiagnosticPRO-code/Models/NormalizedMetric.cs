using System;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    public class NormalizedMetric
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("available")]
        public bool Available { get; set; }

        [JsonPropertyName("source")]
        public string Source { get; set; } = "Unknown";

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    }
}
