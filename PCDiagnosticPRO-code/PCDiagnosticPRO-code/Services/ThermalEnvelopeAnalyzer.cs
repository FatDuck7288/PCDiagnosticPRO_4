using System;
using System.Collections.Generic;
using System.Linq;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Thermal Envelope Stability — évalue la stabilité thermique globale du système.
    /// </summary>
    public static class ThermalEnvelopeAnalyzer
    {
        public class ThermalEnvelopeResult
        {
            public int ThermalScore { get; set; } = 100;
            public string ThermalStatus { get; set; } = "OK";
            public double? CpuTempC { get; set; }
            public double? GpuTempC { get; set; }
            public double? MaxDiskTempC { get; set; }
            public bool IsThrottling { get; set; }
            public bool IsCritical { get; set; }
            public List<string> Warnings { get; set; } = new();
            public string Recommendation { get; set; } = "";
        }

        /// <summary>
        /// Analyse l'enveloppe thermique depuis les capteurs hardware.
        /// </summary>
        public static ThermalEnvelopeResult Analyze(HardwareSensorsResult? sensors)
        {
            var result = new ThermalEnvelopeResult();
            if (sensors == null)
            {
                result.ThermalStatus = "Non mesuré";
                result.ThermalScore = 70;
                result.Recommendation = "Capteurs thermiques non disponibles.";
                return result;
            }

            int penalties = 0;

            // CPU Temperature
            if (sensors.Cpu.CpuTempC.Available)
            {
                result.CpuTempC = sensors.Cpu.CpuTempC.Value;
                var cpuTemp = sensors.Cpu.CpuTempC.Value;
                if (cpuTemp > 95)
                {
                    penalties += 40;
                    result.IsCritical = true;
                    result.IsThrottling = true;
                    result.Warnings.Add($"CPU critique: {cpuTemp:F0}°C > 95°C");
                }
                else if (cpuTemp > 85)
                {
                    penalties += 20;
                    result.IsThrottling = true;
                    result.Warnings.Add($"CPU throttling possible: {cpuTemp:F0}°C");
                }
                else if (cpuTemp > 75)
                {
                    penalties += 10;
                    result.Warnings.Add($"CPU chaud: {cpuTemp:F0}°C");
                }
            }

            // GPU Temperature
            if (sensors.Gpu.GpuTempC.Available)
            {
                result.GpuTempC = sensors.Gpu.GpuTempC.Value;
                var gpuTemp = sensors.Gpu.GpuTempC.Value;
                if (gpuTemp > 95)
                {
                    penalties += 35;
                    result.IsCritical = true;
                    result.Warnings.Add($"GPU critique: {gpuTemp:F0}°C");
                }
                else if (gpuTemp > 85)
                {
                    penalties += 15;
                    result.Warnings.Add($"GPU chaud: {gpuTemp:F0}°C");
                }
            }

            // Disk Temperatures
            if (sensors.Disks.Count > 0)
            {
                var validTemps = sensors.Disks.Where(d => d.TempC.Available && d.TempC.Value > 0).Select(d => d.TempC.Value).ToList();
                if (validTemps.Count > 0)
                {
                    result.MaxDiskTempC = validTemps.Max();
                    if (result.MaxDiskTempC > 60)
                    {
                        penalties += 20;
                        result.Warnings.Add($"Disque chaud: {result.MaxDiskTempC:F0}°C");
                    }
                    else if (result.MaxDiskTempC > 50)
                    {
                        penalties += 5;
                    }
                }
            }

            result.ThermalScore = Math.Max(0, 100 - penalties);

            if (result.IsCritical)
            {
                result.ThermalStatus = "Critique";
                result.Recommendation = "Arrêtez les tâches lourdes immédiatement. Vérifiez le refroidissement.";
            }
            else if (result.IsThrottling)
            {
                result.ThermalStatus = "Throttling";
                result.Recommendation = "Réduisez la charge ou améliorez la ventilation.";
            }
            else if (penalties > 10)
            {
                result.ThermalStatus = "À surveiller";
                result.Recommendation = "Températures élevées mais acceptables.";
            }
            else
            {
                result.ThermalStatus = "OK";
                result.Recommendation = "Enveloppe thermique stable.";
            }

            return result;
        }
    }
}
