using System;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Single critical/error event log entry for stability diagnostics.
    /// </summary>
    public class EventLogDetailedEntry
    {
        [JsonPropertyName("event_id")]
        public int EventId { get; set; }
        [JsonPropertyName("provider_name")]
        public string ProviderName { get; set; } = "";
        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
        [JsonPropertyName("time_created")]
        public DateTime? TimeCreated { get; set; }
        [JsonPropertyName("log_name")]
        public string LogName { get; set; } = "";
        [JsonPropertyName("level")]
        public int? Level { get; set; }
    }
}
