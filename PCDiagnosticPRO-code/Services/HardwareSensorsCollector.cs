using System;
using System.Collections.Generic;
using System.Linq;
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
            var result = CreateDefaultResult();
            result.CollectionExceptions = new List<string>();
            Computer computer = null;

            try
            {
                computer = new Computer();
                computer.IsCpuEnabled = true;
                computer.IsGpuEnabled = true;
                computer.IsStorageEnabled = true;
                computer.Open();

                TryCollectGpuMetrics(computer, result);
                TryCollectCpuMetrics(computer, result);
                TryCollectDiskMetrics(computer, result);

                result.CollectedAt = DateTimeOffset.Now;
            }
            catch (Exception ex)
            {
                var exMsg = ex.Message;
                result.CollectionExceptions.Add($"Global: {exMsg}");
                MarkAllUnavailable(result, string.Format("Erreur globale: {0}", exMsg));
                
                // Detect Defender/WinRing0/security blocking
                DetectSecurityBlocking(result, ex);
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
            
            // Post-process: check if blocking detected from individual collector errors
            if (!result.BlockedBySecurity && result.CollectionExceptions?.Count > 0)
            {
                foreach (var ex in result.CollectionExceptions)
                {
                    if (IsSecurityBlockingError(ex))
                    {
                        result.BlockedBySecurity = true;
                        result.BlockingMessage = "Capteurs bloqués par la sécurité. Exécuter en tant qu'administrateur ou ajouter une exclusion sur le dossier de l'application.";
                        break;
                    }
                }
            }
            
            // Log to temp for debugging
            LogSensorCollectionStatus(result);

            return result;
        }
        
        /// <summary>
        /// Detect if exception indicates Defender/WinRing0/security blocking
        /// </summary>
        private static void DetectSecurityBlocking(HardwareSensorsResult result, Exception ex)
        {
            var exLower = ex.Message.ToLowerInvariant();
            var innerEx = ex.InnerException?.Message?.ToLowerInvariant() ?? "";
            
            if (IsSecurityBlockingError(exLower) || IsSecurityBlockingError(innerEx))
            {
                result.BlockedBySecurity = true;
                result.BlockingMessage = "Capteurs bloqués par la sécurité. Exécuter en tant qu'administrateur ou ajouter une exclusion sur le dossier de l'application.";
                App.LogMessage($"[HardwareSensors] SECURITY BLOCKING DETECTED: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Check if error message indicates security blocking
        /// </summary>
        private static bool IsSecurityBlockingError(string errorMsg)
        {
            if (string.IsNullOrEmpty(errorMsg)) return false;
            
            var lower = errorMsg.ToLowerInvariant();
            return lower.Contains("access denied") || 
                   lower.Contains("access is denied") ||
                   lower.Contains("defender") || 
                   lower.Contains("antivirus") || 
                   lower.Contains("blocked") ||
                   lower.Contains("winring0") ||
                   lower.Contains("ring0") ||
                   lower.Contains("driver") && (lower.Contains("load") || lower.Contains("failed")) ||
                   lower.Contains("security") ||
                   lower.Contains("unauthorized") ||
                   lower.Contains("permission") ||
                   lower.Contains("privilege");
        }
        
        /// <summary>
        /// Log all GPU memory sensors for debugging VRAM accuracy
        /// </summary>
        private static void LogGpuMemorySensors(List<ISensor> sensors, double? selectedTotal, double? selectedUsed)
        {
            try
            {
                var memorySensors = sensors.Where(s => 
                    s.Name.IndexOf("Memory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.Name.IndexOf("VRAM", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.Name.IndexOf("D3D", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                
                if (memorySensors.Count > 0)
                {
                    var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PCDiagnosticPro_VRAM_Debug.log");
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"=== GPU Memory Sensors - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                    sb.AppendLine($"Total sensors found: {memorySensors.Count}");
                    sb.AppendLine();
                    
                    foreach (var sensor in memorySensors)
                    {
                        sb.AppendLine($"  Sensor: {sensor.Name}");
                        sb.AppendLine($"    Type: {sensor.SensorType}");
                        sb.AppendLine($"    Value: {sensor.Value?.ToString() ?? "null"} (Hardware: {sensor.Hardware?.Name ?? "unknown"})");
                    }
                    
                    sb.AppendLine();
                    sb.AppendLine($"SELECTED Values:");
                    sb.AppendLine($"  VRAM Total: {selectedTotal?.ToString("F0") ?? "null"} MB");
                    sb.AppendLine($"  VRAM Used:  {selectedUsed?.ToString("F0") ?? "null"} MB");
                    sb.AppendLine();
                    
                    System.IO.File.AppendAllText(logPath, sb.ToString());
                    App.LogMessage($"[GPU Memory] Found {memorySensors.Count} memory sensors. Selected: Total={selectedTotal:F0}MB, Used={selectedUsed:F0}MB");
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[GPU Memory] Logging error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Log sensor collection status to %TEMP% for debugging
        /// </summary>
        private static void LogSensorCollectionStatus(HardwareSensorsResult result)
        {
            try
            {
                var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PCDiagnosticPro_SensorCollection.log");
                var (available, total) = result.GetAvailabilitySummary();
                var logContent = $"=== Sensor Collection Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n" +
                                 $"Available: {available}/{total}\n" +
                                 $"BlockedBySecurity: {result.BlockedBySecurity}\n" +
                                 $"BlockingMessage: {result.BlockingMessage ?? "N/A"}\n" +
                                 $"Exceptions: {string.Join("; ", result.CollectionExceptions ?? new List<string>())}\n" +
                                 $"CPU Temp: {(result.Cpu.CpuTempC.Available ? result.Cpu.CpuTempC.Value.ToString("F1") + "°C" : result.Cpu.CpuTempC.Reason)}\n" +
                                 $"GPU Temp: {(result.Gpu.GpuTempC.Available ? result.Gpu.GpuTempC.Value.ToString("F1") + "°C" : result.Gpu.GpuTempC.Reason)}\n";
                System.IO.File.AppendAllText(logPath, logContent + "\n");
            }
            catch { /* Ignore logging errors */ }
        }

        private static HardwareSensorsResult CreateDefaultResult()
        {
            var res = new HardwareSensorsResult();
            res.CollectedAt = DateTimeOffset.Now;
            
            res.Gpu = new GpuMetrics();
            res.Gpu.Name = Unavailable("GPU non collecte");
            res.Gpu.VramTotalMB = UnavailableDouble("VRAM totale non collectee");
            res.Gpu.VramUsedMB = UnavailableDouble("VRAM utilisee non collectee");
            res.Gpu.GpuLoadPercent = UnavailableDouble("Charge GPU non collectee");
            res.Gpu.GpuTempC = UnavailableDouble("Temperature GPU non collectee");
            
            res.Cpu = new CpuMetrics();
            res.Cpu.CpuTempC = UnavailableDouble("Temperature CPU non collectee");
            res.Cpu.CpuTempSource = "N/A";
            res.Cpu.CpuLoadPercent = UnavailableDouble("Charge CPU: utiliser donnees PowerShell");
            
            res.Disks = new List<DiskMetrics>();
            
            return res;
        }

        private static void MarkAllUnavailable(HardwareSensorsResult result, string reason)
        {
            result.Gpu.Name = Unavailable(reason);
            result.Gpu.VramTotalMB = UnavailableDouble(reason);
            result.Gpu.VramUsedMB = UnavailableDouble(reason);
            result.Gpu.GpuLoadPercent = UnavailableDouble(reason);
            result.Gpu.GpuTempC = UnavailableDouble(reason);
            result.Cpu.CpuTempC = UnavailableDouble(reason);
            result.Cpu.CpuTempSource = "Erreur";
            result.Cpu.CpuLoadPercent = UnavailableDouble(reason);
            result.Disks.Clear();
            
            var diskMetric = new DiskMetrics();
            diskMetric.Name = Unavailable(reason);
            diskMetric.TempC = UnavailableDouble(reason);
            result.Disks.Add(diskMetric);
        }

        private static void TryCollectGpuMetrics(Computer computer, HardwareSensorsResult result)
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

                result.Gpu.Name = Available(gpu.Name);

                // VRAM Total: prioritize accurate sensors
                var vramTotal = FindSensorValue(sensors, "GPU Memory Total", "Memory Total", "VRAM Total");
                if (vramTotal.HasValue)
                    result.Gpu.VramTotalMB = Available(vramTotal.Value);
                else
                    result.Gpu.VramTotalMB = UnavailableDouble("VRAM totale indisponible");

                // VRAM Used: CRITICAL FIX - Prioritize "D3D Dedicated Memory Used" (matches Task Manager)
                // over "GPU Memory Used" (which shows committed/allocated memory, not actual usage)
                // Order matters: first match wins
                var vramUsed = FindSensorValue(sensors, 
                    "D3D Dedicated Memory Used",  // Task Manager value - actual dedicated VRAM in use
                    "Dedicated Memory Used",       // Alternative naming
                    "Memory Used",                 // Fallback - may be allocated/committed
                    "VRAM Used", 
                    "GPU Memory Used");
                if (vramUsed.HasValue)
                    result.Gpu.VramUsedMB = Available(vramUsed.Value);
                else
                    result.Gpu.VramUsedMB = UnavailableDouble("VRAM utilisee indisponible");
                
                // Debug: Log all GPU memory sensors found for verification
                LogGpuMemorySensors(sensors, vramTotal, vramUsed);

                var gpuLoad = FindSensorValue(sensors, "GPU Core", "GPU", "Core");
                if (gpuLoad.HasValue)
                    result.Gpu.GpuLoadPercent = Available(gpuLoad.Value);
                else
                    result.Gpu.GpuLoadPercent = UnavailableDouble("Charge GPU indisponible");

                var gpuTemp = FindSensorValueByType(sensors, SensorType.Temperature, "GPU", "Core");
                if (gpuTemp.HasValue)
                    result.Gpu.GpuTempC = Available(gpuTemp.Value);
                else
                    result.Gpu.GpuTempC = UnavailableDouble("Temperature GPU indisponible");
            }
            catch (Exception ex)
            {
                result.CollectionExceptions?.Add($"GPU: {ex.Message}");
                SetGpuUnavailable(result, string.Format("Erreur GPU: {0}", ex.Message));
            }
        }

        private static void TryCollectCpuMetrics(Computer computer, HardwareSensorsResult result)
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
                    result.Cpu.CpuTempC = UnavailableDouble("CPU introuvable");
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
                
                // Validation anti-sentinelle (0°C ou hors plage 5-115°C = invalide)
                bool lhmValid = cpuTemp.HasValue && cpuTemp.Value > 5.0 && cpuTemp.Value < 115.0;
                
                if (lhmValid)
                {
                    result.Cpu.CpuTempC = Available(cpuTemp.Value);
                    result.Cpu.CpuTempSource = tempSource;
                    App.LogMessage($"[Sensors→CPU] Température LHM valide: {cpuTemp.Value:F1}°C (source: {tempSource})");
                }
                else
                {
                    // LHM a retourné sentinelle ou aucune valeur → tenter fallback WMI ThermalZone
                    if (cpuTemp.HasValue)
                    {
                        App.LogMessage($"[Sensors→CPU] LHM sentinelle détectée: {cpuTemp.Value:F1}°C (source: {tempSource}) → fallback WMI");
                    }
                    else
                    {
                        var tempSensors = sensors.Where(s => s.SensorType == SensorType.Temperature).ToList();
                        var sensorNames = string.Join(", ", tempSensors.Select(s => $"{s.Name}={s.Value}"));
                        App.LogMessage($"[Sensors→CPU] AUCUNE température LHM. Capteurs dispo: [{sensorNames}] → fallback WMI");
                    }
                    
                    // === FALLBACK WMI ThermalZone ===
                    var wmiFallback = WmiThermalZoneFallback.TryGetCpuTemp(minValidC: 5.0, maxValidC: 115.0);
                    
                    if (wmiFallback.TempC.HasValue)
                    {
                        result.Cpu.CpuTempC = Available(wmiFallback.TempC.Value);
                        result.Cpu.CpuTempSource = wmiFallback.Source;
                        App.LogMessage($"[Sensors→CPU] Fallback WMI réussi: {wmiFallback.TempC.Value:F1}°C (source: {wmiFallback.Source})");
                    }
                    else
                    {
                        // Aucune source valide
                        string reason = cpuTemp.HasValue 
                            ? $"capteur invalide: valeur sentinelle {cpuTemp.Value:F0}" 
                            : $"aucun capteur compatible";
                        reason += $"; fallback WMI: {wmiFallback.Reason ?? "unavailable"}";
                        
                        result.Cpu.CpuTempC = UnavailableDouble(reason);
                        result.Cpu.CpuTempSource = tempSource ?? "N/A";
                        App.LogMessage($"[Sensors→CPU] ÉCHEC total: {reason}");
                    }
                }
            }
            catch (Exception ex)
            {
                result.CollectionExceptions?.Add($"CPU: {ex.Message}");
                result.Cpu.CpuTempC = UnavailableDouble(string.Format("Erreur CPU: {0}", ex.Message));
                result.Cpu.CpuTempSource = "Erreur";
                App.LogMessage($"[Sensors→CPU] ERREUR: {ex.Message}");
            }
        }

        private static void TryCollectDiskMetrics(Computer computer, HardwareSensorsResult result)
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
                    diskMetric.Name = Unavailable("Aucun disque detecte");
                    diskMetric.TempC = UnavailableDouble("Temperature disque indisponible");
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
                    diskMetric.Name = Available(disk.Name);
                    
                    if (temp.HasValue)
                        diskMetric.TempC = Available(temp.Value);
                    else
                        diskMetric.TempC = UnavailableDouble("Temperature disque indisponible");
                    
                    result.Disks.Add(diskMetric);
                }
            }
            catch (Exception ex)
            {
                result.CollectionExceptions?.Add($"Disks: {ex.Message}");
                result.Disks.Clear();
                var diskMetric = new DiskMetrics();
                diskMetric.Name = Unavailable(string.Format("Erreur disques: {0}", ex.Message));
                diskMetric.TempC = UnavailableDouble(string.Format("Erreur disques: {0}", ex.Message));
                result.Disks.Add(diskMetric);
            }
        }

        private static void SetGpuUnavailable(HardwareSensorsResult result, string reason)
        {
            result.Gpu.Name = Unavailable(reason);
            result.Gpu.VramTotalMB = UnavailableDouble(reason);
            result.Gpu.VramUsedMB = UnavailableDouble(reason);
            result.Gpu.GpuLoadPercent = UnavailableDouble(reason);
            result.Gpu.GpuTempC = UnavailableDouble(reason);
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

        private static MetricValue<string> Available(string value)
        {
            var m = new MetricValue<string>();
            m.Available = true;
            m.Value = value;
            return m;
        }

        private static MetricValue<double> Available(double value)
        {
            var m = new MetricValue<double>();
            m.Available = true;
            m.Value = value;
            return m;
        }

        private static MetricValue<string> Unavailable(string reason)
        {
            var m = new MetricValue<string>();
            m.Available = false;
            m.Reason = reason;
            return m;
        }

        private static MetricValue<double> UnavailableDouble(string reason)
        {
            var m = new MetricValue<double>();
            m.Available = false;
            m.Reason = reason;
            return m;
        }
    }
}
