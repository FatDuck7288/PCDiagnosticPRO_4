using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    public class HardwareSensorsCollector
    {
        public Task<HardwareSensorsResult> CollectAsync(CancellationToken ct)
        {
            return Task.Run(() => CollectInternal(ct), ct);
        }

        private HardwareSensorsResult CollectInternal(CancellationToken ct)
        {
            var collectedAt = DateTimeOffset.Now;
            var result = CreateDefaultResult(collectedAt);
            Computer computer = null;

            try
            {
                computer = new Computer();
                computer.IsCpuEnabled = true;
                computer.IsGpuEnabled = true;
                computer.IsStorageEnabled = true;
                computer.Open();

                TryCollectGpuMetrics(computer, result, collectedAt);
                TryCollectCpuMetrics(computer, result, collectedAt);
                TryCollectDiskMetrics(computer, result, collectedAt);

                result.CollectedAt = collectedAt;
            }
            catch (Exception ex)
            {
                MarkAllUnavailable(result, string.Format("Erreur globale: {0}", ex.Message), collectedAt);
            }
            finally
            {
                if (computer != null)
                {
                    try
                    {
                        computer.Close();
                    }
                    catch
                    {
                        // Ignorer les erreurs de fermeture
                    }
                }
            }

            return result;
        }

        private static HardwareSensorsResult CreateDefaultResult(DateTimeOffset collectedAt)
        {
            var res = new HardwareSensorsResult();
            res.CollectedAt = collectedAt;
            
            res.Gpu = new GpuMetrics();
            res.Gpu.Name = Unavailable("GPU non collecte", "LibreHardwareMonitor", collectedAt);
            res.Gpu.VramTotalMB = UnavailableDouble("VRAM totale non collectee", "LibreHardwareMonitor", collectedAt);
            res.Gpu.VramUsedMB = UnavailableDouble("VRAM utilisee non collectee", "LibreHardwareMonitor", collectedAt);
            res.Gpu.GpuLoadPercent = UnavailableDouble("Charge GPU non collectee", "LibreHardwareMonitor", collectedAt);
            res.Gpu.GpuTempC = UnavailableDouble("Temperature GPU non collectee", "LibreHardwareMonitor", collectedAt);
            
            res.Cpu = new CpuMetrics();
            res.Cpu.CpuTempC = UnavailableDouble("Temperature CPU non collectee", "LibreHardwareMonitor", collectedAt);
            res.Cpu.CpuTempSource = "N/A";
            res.Cpu.CpuLoadPercent = UnavailableDouble("Charge CPU: utiliser donnees PowerShell", "PowerShell", collectedAt);
            
            res.Disks = new List<DiskMetrics>();
            
            return res;
        }

        private static void MarkAllUnavailable(HardwareSensorsResult result, string reason, DateTimeOffset collectedAt)
        {
            result.Gpu.Name = Unavailable(reason, "LibreHardwareMonitor", collectedAt);
            result.Gpu.VramTotalMB = UnavailableDouble(reason, "LibreHardwareMonitor", collectedAt);
            result.Gpu.VramUsedMB = UnavailableDouble(reason, "LibreHardwareMonitor", collectedAt);
            result.Gpu.GpuLoadPercent = UnavailableDouble(reason, "LibreHardwareMonitor", collectedAt);
            result.Gpu.GpuTempC = UnavailableDouble(reason, "LibreHardwareMonitor", collectedAt);
            result.Cpu.CpuTempC = UnavailableDouble(reason, "LibreHardwareMonitor", collectedAt);
            result.Cpu.CpuTempSource = "Erreur";
            result.Cpu.CpuLoadPercent = UnavailableDouble(reason, "PowerShell", collectedAt);
            result.Disks.Clear();
            
            var diskMetric = new DiskMetrics();
            diskMetric.Name = Unavailable(reason, "LibreHardwareMonitor", collectedAt);
            diskMetric.TempC = UnavailableDouble(reason, "LibreHardwareMonitor", collectedAt);
            result.Disks.Add(diskMetric);
        }

        private static void TryCollectGpuMetrics(Computer computer, HardwareSensorsResult result, DateTimeOffset collectedAt)
        {
            try
            {
                IHardware gpu = null;
                foreach (var hw in computer.Hardware)
                {
                    if (hw.HardwareType == HardwareType.GpuAmd ||
                        hw.HardwareType == HardwareType.GpuNvidia ||
                        hw.HardwareType == HardwareType.GpuIntel)
                    {
                        gpu = hw;
                        break;
                    }
                }

                if (gpu == null)
                {
                    SetGpuUnavailable(result, "GPU introuvable");
                    return;
                }

                gpu.Update();
                UpdateSubHardware(gpu);

                var sensors = GetAllSensors(gpu).ToList();

                result.Gpu.Name = Available(gpu.Name, "LibreHardwareMonitor", collectedAt);

                var vramTotal = FindSensorValue(sensors, "Memory Total", "VRAM Total", "GPU Memory Total");
                if (vramTotal.HasValue)
                    result.Gpu.VramTotalMB = Available(vramTotal.Value, "LibreHardwareMonitor", collectedAt);
                else
                    result.Gpu.VramTotalMB = UnavailableDouble("VRAM totale indisponible", "LibreHardwareMonitor", collectedAt);

                var vramUsed = FindSensorValue(sensors, "Memory Used", "VRAM Used", "GPU Memory Used");
                if (vramUsed.HasValue)
                    result.Gpu.VramUsedMB = Available(vramUsed.Value, "LibreHardwareMonitor", collectedAt);
                else
                    result.Gpu.VramUsedMB = UnavailableDouble("VRAM utilisee indisponible", "LibreHardwareMonitor", collectedAt);

                var gpuLoad = FindSensorValue(sensors, "GPU Core", "GPU", "Core");
                if (gpuLoad.HasValue)
                    result.Gpu.GpuLoadPercent = Available(gpuLoad.Value, "LibreHardwareMonitor", collectedAt);
                else
                    result.Gpu.GpuLoadPercent = UnavailableDouble("Charge GPU indisponible", "LibreHardwareMonitor", collectedAt);

                var gpuTemp = FindSensorValueByType(sensors, SensorType.Temperature, "GPU", "Core");
                if (gpuTemp.HasValue)
                    result.Gpu.GpuTempC = Available(gpuTemp.Value, "LibreHardwareMonitor", collectedAt);
                else
                    result.Gpu.GpuTempC = UnavailableDouble("Temperature GPU indisponible", "LibreHardwareMonitor", collectedAt);
            }
            catch (Exception ex)
            {
                SetGpuUnavailable(result, string.Format("Erreur GPU: {0}", ex.Message), collectedAt);
            }
        }

        private static void TryCollectCpuMetrics(Computer computer, HardwareSensorsResult result, DateTimeOffset collectedAt)
        {
            try
            {
                IHardware cpu = null;
                foreach (var hw in computer.Hardware)
                {
                    if (hw.HardwareType == HardwareType.Cpu)
                    {
                        cpu = hw;
                        break;
                    }
                }

                if (cpu == null)
                {
                    result.Cpu.CpuTempC = UnavailableDouble("CPU introuvable", "LibreHardwareMonitor", collectedAt);
                    result.Cpu.CpuTempSource = "N/A";
                    return;
                }

                cpu.Update();
                UpdateSubHardware(cpu);

                var sensors = GetAllSensors(cpu).ToList();
                
                // === STRATÉGIE ROBUSTE POUR CPU TEMP (Intel + AMD Ryzen) ===
                // Priorité 1: CPU Package (Intel standard)
                // Priorité 2: Tctl (AMD Ryzen - température de contrôle)
                // Priorité 3: Tdie (AMD Ryzen - température réelle du die)
                // Priorité 4: Core (CCD) Average ou Max
                // Priorité 5: Tout capteur température disponible
                
                double? cpuTemp = null;
                string tempSource = "N/A";
                
                // Priorité 1: CPU Package (Intel standard, aussi certains AMD)
                cpuTemp = FindSensorValueByType(sensors, SensorType.Temperature, "Package");
                if (cpuTemp.HasValue)
                {
                    tempSource = "CPU Package";
                }
                
                // Priorité 2: Tctl (AMD Ryzen - température de contrôle, peut être +10°C offset)
                if (!cpuTemp.HasValue)
                {
                    cpuTemp = FindSensorValueByType(sensors, SensorType.Temperature, "Tctl");
                    if (cpuTemp.HasValue)
                    {
                        tempSource = "Tctl (AMD)";
                        // Note: Tctl peut avoir un offset de +10°C sur certains Ryzen
                        // On garde la valeur brute et on documente la source
                    }
                }
                
                // Priorité 3: Tdie (AMD Ryzen - température réelle du die, préférable à Tctl)
                if (!cpuTemp.HasValue)
                {
                    cpuTemp = FindSensorValueByType(sensors, SensorType.Temperature, "Tdie");
                    if (cpuTemp.HasValue)
                    {
                        tempSource = "Tdie (AMD)";
                    }
                }
                
                // Priorité 4: Core (CCD) - moyenne ou max des cores
                if (!cpuTemp.HasValue)
                {
                    cpuTemp = FindSensorValueByType(sensors, SensorType.Temperature, "Core (Tctl/Tdie)");
                    if (cpuTemp.HasValue)
                    {
                        tempSource = "Core (Tctl/Tdie)";
                    }
                }
                
                // Priorité 5: CCD Average
                if (!cpuTemp.HasValue)
                {
                    cpuTemp = FindSensorValueByType(sensors, SensorType.Temperature, "CCD");
                    if (cpuTemp.HasValue)
                    {
                        tempSource = "CCD Average";
                    }
                }
                
                // Priorité 6: Fallback sur n'importe quel capteur température CPU
                if (!cpuTemp.HasValue)
                {
                    var anyTempSensor = sensors.FirstOrDefault(s => 
                        s.SensorType == SensorType.Temperature && s.Value.HasValue);
                    if (anyTempSensor != null)
                    {
                        cpuTemp = anyTempSensor.Value.Value;
                        tempSource = $"Fallback ({anyTempSensor.Name})";
                    }
                }
                
                if (cpuTemp.HasValue && IsPlausibleCpuTemp(cpuTemp.Value))
                {
                    result.Cpu.CpuTempC = Available(cpuTemp.Value, "LibreHardwareMonitor", collectedAt);
                    result.Cpu.CpuTempSource = tempSource;
                    App.LogMessage($"[Sensors→CPU] Température collectée: {cpuTemp.Value:F1}°C (source: {tempSource})");
                }
                else
                {
                    if (cpuTemp.HasValue && !IsPlausibleCpuTemp(cpuTemp.Value))
                    {
                        App.LogMessage($"[Sensors→CPU] Température LHM invalide ({cpuTemp.Value:F1}°C), fallback WMI.");
                    }

                    var wmiTemp = TryGetWmiCpuTemperature();
                    if (wmiTemp.HasValue)
                    {
                        result.Cpu.CpuTempC = Available(wmiTemp.Value, "WMI ThermalZone", collectedAt);
                        result.Cpu.CpuTempSource = "WMI ThermalZone";
                        App.LogMessage($"[Sensors→CPU] Température WMI collectée: {wmiTemp.Value:F1}°C");
                        return;
                    }

                    // Log tous les capteurs pour debug
                    var tempSensors = sensors.Where(s => s.SensorType == SensorType.Temperature).ToList();
                    var sensorNames = string.Join(", ", tempSensors.Select(s => $"{s.Name}={s.Value}"));
                    App.LogMessage($"[Sensors→CPU] AUCUNE température trouvée. Capteurs dispo: [{sensorNames}]");
                    
                    result.Cpu.CpuTempC = UnavailableDouble($"Aucun capteur température CPU compatible (trouvés: {tempSensors.Count})", "LibreHardwareMonitor", collectedAt);
                    result.Cpu.CpuTempSource = "N/A";
                }
            }
            catch (Exception ex)
            {
                result.Cpu.CpuTempC = UnavailableDouble(string.Format("Erreur CPU: {0}", ex.Message), "LibreHardwareMonitor", collectedAt);
                result.Cpu.CpuTempSource = "Erreur";
                App.LogMessage($"[Sensors→CPU] ERREUR: {ex.Message}");
            }
        }

        private static void TryCollectDiskMetrics(Computer computer, HardwareSensorsResult result, DateTimeOffset collectedAt)
        {
            try
            {
                result.Disks.Clear();

                var disks = new List<IHardware>();
                foreach (var hw in computer.Hardware)
                {
                    if (hw.HardwareType == HardwareType.Storage)
                    {
                        disks.Add(hw);
                    }
                }

                if (disks.Count == 0)
                {
                    var diskMetric = new DiskMetrics();
                    diskMetric.Name = Unavailable("Aucun disque detecte", "LibreHardwareMonitor", collectedAt);
                    diskMetric.TempC = UnavailableDouble("Temperature disque indisponible", "LibreHardwareMonitor", collectedAt);
                    result.Disks.Add(diskMetric);
                    return;
                }

                foreach (var disk in disks)
                {
                    disk.Update();
                    UpdateSubHardware(disk);

                    var sensors = GetAllSensors(disk).ToList();
                    var temp = FindSensorValueByType(sensors, SensorType.Temperature, "Temperature", "Temp");

                    var diskMetric = new DiskMetrics();
                    diskMetric.Name = Available(disk.Name, "LibreHardwareMonitor", collectedAt);
                    
                    if (temp.HasValue)
                        diskMetric.TempC = Available(temp.Value, "LibreHardwareMonitor", collectedAt);
                    else
                        diskMetric.TempC = UnavailableDouble("Temperature disque indisponible", "LibreHardwareMonitor", collectedAt);
                    
                    result.Disks.Add(diskMetric);
                }
            }
            catch (Exception ex)
            {
                result.Disks.Clear();
                var diskMetric = new DiskMetrics();
                diskMetric.Name = Unavailable(string.Format("Erreur disques: {0}", ex.Message), "LibreHardwareMonitor", collectedAt);
                diskMetric.TempC = UnavailableDouble(string.Format("Erreur disques: {0}", ex.Message), "LibreHardwareMonitor", collectedAt);
                result.Disks.Add(diskMetric);
            }
        }

        private static void SetGpuUnavailable(HardwareSensorsResult result, string reason, DateTimeOffset collectedAt)
        {
            result.Gpu.Name = Unavailable(reason, "LibreHardwareMonitor", collectedAt);
            result.Gpu.VramTotalMB = UnavailableDouble(reason, "LibreHardwareMonitor", collectedAt);
            result.Gpu.VramUsedMB = UnavailableDouble(reason, "LibreHardwareMonitor", collectedAt);
            result.Gpu.GpuLoadPercent = UnavailableDouble(reason, "LibreHardwareMonitor", collectedAt);
            result.Gpu.GpuTempC = UnavailableDouble(reason, "LibreHardwareMonitor", collectedAt);
        }

        private static List<ISensor> GetAllSensors(IHardware hardware)
        {
            var allSensors = new List<ISensor>();
            
            foreach (var sensor in hardware.Sensors)
            {
                allSensors.Add(sensor);
            }

            foreach (var subHardware in hardware.SubHardware)
            {
                foreach (var sensor in subHardware.Sensors)
                {
                    allSensors.Add(sensor);
                }
            }
            
            return allSensors;
        }

        private static void UpdateSubHardware(IHardware hardware)
        {
            foreach (var subHardware in hardware.SubHardware)
            {
                subHardware.Update();
            }
        }

        private static double? FindSensorValue(List<ISensor> sensors, params string[] nameContains)
        {
            foreach (var sensor in sensors)
            {
                if (sensor.Value.HasValue)
                {
                    foreach (var token in nameContains)
                    {
                        if (sensor.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return sensor.Value.Value;
                        }
                    }
                }
            }
            return null;
        }

        private static double? FindSensorValueByType(List<ISensor> sensors, SensorType sensorType, params string[] nameContains)
        {
            foreach (var sensor in sensors)
            {
                if (sensor.SensorType == sensorType && sensor.Value.HasValue)
                {
                    foreach (var token in nameContains)
                    {
                        if (sensor.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return sensor.Value.Value;
                        }
                    }
                }
            }
            return null;
        }

        private static MetricValue<string> Available(string value, string source, DateTimeOffset timestamp)
        {
            var m = new MetricValue<string>();
            m.Available = true;
            m.Value = value;
            m.Source = source;
            m.Timestamp = timestamp;
            return m;
        }

        private static MetricValue<double> Available(double value, string source, DateTimeOffset timestamp)
        {
            var m = new MetricValue<double>();
            m.Available = true;
            m.Value = value;
            m.Source = source;
            m.Timestamp = timestamp;
            return m;
        }

        private static MetricValue<string> Unavailable(string reason, string source, DateTimeOffset timestamp)
        {
            var m = new MetricValue<string>();
            m.Available = false;
            m.Reason = reason;
            m.Source = source;
            m.Timestamp = timestamp;
            return m;
        }

        private static MetricValue<double> UnavailableDouble(string reason, string source, DateTimeOffset timestamp)
        {
            var m = new MetricValue<double>();
            m.Available = false;
            m.Reason = reason;
            m.Source = source;
            m.Timestamp = timestamp;
            return m;
        }

        private static bool IsPlausibleCpuTemp(double value)
        {
            return value > 5 && value < 115;
        }

        private static double? TryGetWmiCpuTemperature()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature, HighPrecisionTemperature FROM MSAcpi_ThermalZoneTemperature");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var currentTempRaw = obj["CurrentTemperature"];
                    var highPrecisionRaw = obj["HighPrecisionTemperature"];

                    var tempCandidates = new List<double>();
                    if (currentTempRaw != null)
                    {
                        var current = Convert.ToDouble(currentTempRaw);
                        tempCandidates.Add(ConvertKelvinTenthsToCelsius(current));
                    }

                    if (highPrecisionRaw != null)
                    {
                        var high = Convert.ToDouble(highPrecisionRaw);
                        tempCandidates.Add(ConvertKelvinTenthsToCelsius(high));
                    }

                    foreach (var temp in tempCandidates)
                    {
                        if (IsPlausibleCpuTemp(temp))
                        {
                            return temp;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[Sensors→CPU] WMI fallback échoué: {ex.Message}");
            }

            return null;
        }

        private static double ConvertKelvinTenthsToCelsius(double value)
        {
            return (value / 10.0) - 273.15;
        }
    }
}
