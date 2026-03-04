using System;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    public enum UnavailableReason
    {
        NotSupported,
        NotPresent,
        BlockedBySecurity,
        PermissionDenied,
        Timeout,
        ProviderMissing,
        ParseError,
        Unknown
    }

    public enum MetricConfidence
    {
        Low,
        Medium,
        High
    }

    /// <summary>
    /// Shared availability contract for all producer/consumer layers.
    /// Additive model: legacy scalar fields can coexist during migration.
    /// </summary>
    public class AvailabilityEnvelope<T>
    {
        [JsonPropertyName("available")]
        public bool Available { get; set; }

        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public T? Value { get; set; }

        [JsonPropertyName("reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; set; }

        [JsonPropertyName("source")]
        public string Source { get; set; } = "unknown";

        [JsonPropertyName("confidence")]
        public MetricConfidence Confidence { get; set; } = MetricConfidence.Low;

        [JsonPropertyName("timestamp")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Timestamp { get; set; }

        [JsonPropertyName("details")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Details { get; set; }
    }
}
