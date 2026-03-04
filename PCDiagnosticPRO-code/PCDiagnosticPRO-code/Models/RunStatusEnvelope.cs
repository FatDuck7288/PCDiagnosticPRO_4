using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    public static class RunState
    {
        public const string Ok = "OK";
        public const string Partial = "PARTIAL";
        public const string Incomplete = "INCOMPLETE";
        public const string Failed = "FAILED";
    }

    /// <summary>
    /// Runtime quality/contract gate output. Additive: does not replace existing health status fields.
    /// </summary>
    public class RunStatusEnvelope
    {
        [JsonPropertyName("state")]
        public string State { get; set; } = RunState.Ok;

        [JsonPropertyName("reasonCodes")]
        public List<string> ReasonCodes { get; set; } = new();

        [JsonPropertyName("failedGates")]
        public List<string> FailedGates { get; set; } = new();

        [JsonPropertyName("uiCoveragePercent")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? UiCoveragePercent { get; set; }

        [JsonPropertyName("threshold")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Threshold { get; set; }

        [JsonPropertyName("messages")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Messages { get; set; }

        [JsonIgnore]
        public bool HasGateFailures => ReasonCodes.Count > 0 || FailedGates.Count > 0;
    }
}
