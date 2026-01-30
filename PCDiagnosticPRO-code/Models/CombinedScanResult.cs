using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCDiagnosticPro.DiagnosticsSignals;

namespace PCDiagnosticPro.Models
{
    public class CombinedScanResult
    {
        [JsonPropertyName("scan_powershell")]
        public JsonElement ScanPowershell { get; set; }

        [JsonPropertyName("sensors_csharp")]
        public HardwareSensorsResult SensorsCsharp { get; set; } = new HardwareSensorsResult();
        
        /// <summary>
        /// GOD TIER: 10 diagnostic signals (WHEA, TDR, CPU throttle, etc.)
        /// </summary>
        [JsonPropertyName("diagnostic_signals")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, SignalResult>? DiagnosticSignals { get; set; }
    }
}
