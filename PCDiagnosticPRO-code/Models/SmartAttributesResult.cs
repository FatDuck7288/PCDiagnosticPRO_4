using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Single SMART attribute (ID, name, current, worst, raw, threshold).
    /// </summary>
    public class SmartAttributeEntry
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        [JsonPropertyName("current")]
        public int Current { get; set; }
        [JsonPropertyName("worst")]
        public int Worst { get; set; }
        [JsonPropertyName("raw")]
        public ulong Raw { get; set; }
        [JsonPropertyName("threshold")]
        public int Threshold { get; set; }
    }

    /// <summary>
    /// SMART data per disk: PredictFailure + list of critical attributes.
    /// </summary>
    public class SmartDiskEntry
    {
        [JsonPropertyName("instance_name")]
        public string InstanceName { get; set; } = "";
        [JsonPropertyName("predict_failure")]
        public bool PredictFailure { get; set; }
        [JsonPropertyName("attributes")]
        public List<SmartAttributeEntry> Attributes { get; set; } = new();
    }
}
