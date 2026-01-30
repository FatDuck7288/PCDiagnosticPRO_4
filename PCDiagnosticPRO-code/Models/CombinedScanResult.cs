using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace PCDiagnosticPro.Models
{
    public class CombinedScanResult
    {
        [JsonPropertyName("scan_powershell")]
        public JsonElement ScanPowershell { get; set; }

        [JsonPropertyName("sensors_csharp")]
        public HardwareSensorsResult SensorsCsharp { get; set; } = new HardwareSensorsResult();

        [JsonPropertyName("udis_report")]
        public UdisReport? UdisReport { get; set; }

        [JsonPropertyName("findings")]
        public List<DiagnosticFinding> Findings { get; set; } = new();

        [JsonPropertyName("findings_note")]
        public string? FindingsNote { get; set; }

        [JsonPropertyName("normalized_metrics")]
        public List<NormalizedMetric> NormalizedMetrics { get; set; } = new();
    }
}
