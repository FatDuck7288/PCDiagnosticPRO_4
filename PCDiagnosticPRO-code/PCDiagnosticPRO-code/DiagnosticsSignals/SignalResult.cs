using System;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.DiagnosticsSignals
{
    /// <summary>
    /// Standard result format for all diagnostic signal collectors.
    /// </summary>
    public class SignalResult
    {
        /// <summary>Signal name (matches collector name)</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        
        /// <summary>Collected value (number or object)</summary>
        [JsonPropertyName("value")]
        public object? Value { get; set; }
        
        /// <summary>Whether the signal was successfully collected</summary>
        [JsonPropertyName("available")]
        public bool Available { get; set; } = true;
        
        /// <summary>Source of the data (e.g., "EventLog", "PerfCounter", "ETW")</summary>
        [JsonPropertyName("source")]
        public string Source { get; set; } = "";
        
        /// <summary>Reason if unavailable</summary>
        [JsonPropertyName("reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; set; }
        
        /// <summary>Collection timestamp (ISO 8601)</summary>
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        
        /// <summary>Data quality: ok, partial, suspect</summary>
        [JsonPropertyName("quality")]
        public string Quality { get; set; } = "ok";
        
        /// <summary>Additional notes</summary>
        [JsonPropertyName("notes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Notes { get; set; }
        
        /// <summary>Collection duration in milliseconds</summary>
        [JsonPropertyName("durationMs")]
        public long DurationMs { get; set; }
        
        /// <summary>Create a successful result</summary>
        public static SignalResult Ok(string name, object value, string source, string? notes = null)
        {
            return new SignalResult
            {
                Name = name,
                Value = value,
                Available = true,
                Source = source,
                Quality = "ok",
                Notes = notes,
                Timestamp = DateTime.UtcNow
            };
        }
        
        /// <summary>Create a partial result (some data missing)</summary>
        public static SignalResult Partial(string name, object value, string source, string notes)
        {
            return new SignalResult
            {
                Name = name,
                Value = value,
                Available = true,
                Source = source,
                Quality = "partial",
                Notes = notes,
                Timestamp = DateTime.UtcNow
            };
        }
        
        /// <summary>Create an unavailable result</summary>
        public static SignalResult Unavailable(string name, string reason, string sourceAttempted)
        {
            return new SignalResult
            {
                Name = name,
                Value = null,
                Available = false,
                Source = sourceAttempted,
                Reason = reason,
                Quality = "suspect",
                Timestamp = DateTime.UtcNow
            };
        }
    }
}
