using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    public class CombinedScanResult
    {
        [JsonPropertyName("scan_powershell")]
        public JsonElement ScanPowershell { get; set; }

        [JsonPropertyName("sensors_csharp")]
        public HardwareSensorsResult SensorsCsharp { get; set; } = new HardwareSensorsResult();
    }
}
