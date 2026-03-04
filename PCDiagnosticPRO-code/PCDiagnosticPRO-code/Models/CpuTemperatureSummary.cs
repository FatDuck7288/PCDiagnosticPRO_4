using System.Text.Json.Serialization;
using PCDiagnosticPro.Services;

namespace PCDiagnosticPro.Models
{
    public class CpuTemperatureSummary
    {
        [JsonPropertyName("temperatureC")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? TemperatureC { get; set; }

        [JsonPropertyName("temperatureSource")]
        public string TemperatureSource { get; set; } = CpuTemperatureMetadataService.SourceNone;

        [JsonPropertyName("temperatureConfidence")]
        public string TemperatureConfidence { get; set; } = CpuTemperatureMetadataService.ConfidenceNone;

        [JsonPropertyName("temperatureReason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TemperatureReason { get; set; }

        public static CpuTemperatureSummary FromSensors(HardwareSensorsResult? sensors)
        {
            var cpu = sensors?.Cpu;
            var available = cpu?.CpuTempC?.Available == true;
            return new CpuTemperatureSummary
            {
                TemperatureC = available ? cpu!.CpuTempC.Value : null,
                TemperatureSource = string.IsNullOrWhiteSpace(cpu?.CpuTempSource)
                    ? CpuTemperatureMetadataService.SourceNone
                    : cpu!.CpuTempSource,
                TemperatureConfidence = string.IsNullOrWhiteSpace(cpu?.CpuTempConfidence)
                    ? CpuTemperatureMetadataService.ConfidenceNone
                    : cpu!.CpuTempConfidence,
                TemperatureReason = available
                    ? null
                    : CpuTemperatureMetadataService.NormalizeReasonCode(cpu?.CpuTempReasonIfMissing ?? cpu?.CpuTempC?.Reason)
            };
        }
    }
}
