using System;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// PHASE 1.2: Standard normalized metric format for DiagnosticSnapshot.
    /// Every metric MUST have all required fields.
    /// RULE: available=false => value=null, confidence=0, reason MANDATORY
    /// </summary>
    public class NormalizedMetric
    {
        /// <summary>Metric value (null if unavailable)</summary>
        [JsonPropertyName("value")]
        public object? Value { get; set; }

        /// <summary>Unit of measurement (e.g., "°C", "ms", "%", "count")</summary>
        [JsonPropertyName("unit")]
        public string Unit { get; set; } = "";

        /// <summary>Whether the metric was successfully collected</summary>
        [JsonPropertyName("available")]
        public bool Available { get; set; }

        /// <summary>Source of the data (e.g., "LHM", "WMI", "EventLog", "PerfCounter")</summary>
        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        /// <summary>
        /// Reason if unavailable. MANDATORY when available=false.
        /// Values: sentinel_zero, sentinel_minus_one, nan_or_infinite, out_of_range, 
        /// eventlog_access_denied_or_unavailable, perf_counter_not_supported, etc.
        /// </summary>
        [JsonPropertyName("reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; set; }

        /// <summary>Collection timestamp (ISO 8601 UTC)</summary>
        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");

        /// <summary>Confidence level 0-100. MUST be 0 when available=false.</summary>
        [JsonPropertyName("confidence")]
        public int Confidence { get; set; }

        /// <summary>Additional notes or context</summary>
        [JsonPropertyName("notes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Notes { get; set; }
    }

    /// <summary>
    /// Factory for creating NormalizedMetric instances with proper validation.
    /// </summary>
    public static class MetricFactory
    {
        /// <summary>
        /// Create an available metric with valid value.
        /// </summary>
        public static NormalizedMetric CreateAvailable(object value, string unit, string source, int confidence = 100, string? notes = null)
        {
            return new NormalizedMetric
            {
                Value = value,
                Unit = unit,
                Available = true,
                Source = source,
                Reason = null,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Confidence = Math.Clamp(confidence, 0, 100),
                Notes = notes
            };
        }

        /// <summary>
        /// Create an unavailable metric with mandatory reason.
        /// Value is set to null, confidence to 0.
        /// </summary>
        public static NormalizedMetric CreateUnavailable(string unit, string source, string reason, string? notes = null)
        {
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("Reason is MANDATORY for unavailable metrics", nameof(reason));

            return new NormalizedMetric
            {
                Value = null,
                Unit = unit,
                Available = false,
                Source = source,
                Reason = reason,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Confidence = 0,
                Notes = notes
            };
        }

        /// <summary>
        /// Create a metric from a nullable double, applying sentinel validation.
        /// </summary>
        public static NormalizedMetric FromDouble(double? value, string unit, string source, 
            double minValid, double maxValid, bool zeroIsSentinel = true)
        {
            if (!value.HasValue)
                return CreateUnavailable(unit, source, "value_not_collected");

            var v = value.Value;

            if (double.IsNaN(v) || double.IsInfinity(v))
                return CreateUnavailable(unit, source, "nan_or_infinite");

            if (v == -1)
                return CreateUnavailable(unit, source, "sentinel_minus_one");

            if (zeroIsSentinel && Math.Abs(v) < 0.001)
                return CreateUnavailable(unit, source, "sentinel_zero");

            if (v < minValid || v > maxValid)
                return CreateUnavailable(unit, source, $"out_of_range ({v} not in [{minValid},{maxValid}])");

            return CreateAvailable(Math.Round(v, 2), unit, source, 100);
        }

        /// <summary>
        /// Create a metric from an integer count.
        /// </summary>
        public static NormalizedMetric FromCount(int count, string source, string? notes = null)
        {
            return CreateAvailable(count, "count", source, 100, notes);
        }

        /// <summary>
        /// Create a metric from a timestamp.
        /// </summary>
        public static NormalizedMetric FromTimestamp(DateTime? timestamp, string source)
        {
            if (!timestamp.HasValue)
                return CreateUnavailable("timestamp", source, "no_timestamp_available");

            return CreateAvailable(timestamp.Value.ToString("o"), "timestamp", source, 100);
        }
    }
}
