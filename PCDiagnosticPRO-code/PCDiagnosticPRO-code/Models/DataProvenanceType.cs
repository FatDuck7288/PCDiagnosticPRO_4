using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Provenance contract for Rapport Integral rows.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DataProvenanceType
    {
        Collected,
        Calculated,
        Inferred,
        Unavailable
    }
}
