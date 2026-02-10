using System;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Single minidump file entry: name, date, optional BugCheck code and driver hint.
    /// </summary>
    public class MinidumpEntry
    {
        [JsonPropertyName("file_name")]
        public string FileName { get; set; } = "";

        [JsonPropertyName("last_write_time_utc")]
        public DateTime? LastWriteTimeUtc { get; set; }

        [JsonPropertyName("bug_check_code")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public uint? BugCheckCode { get; set; }

        [JsonPropertyName("driver_hint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DriverHint { get; set; }
    }
}
