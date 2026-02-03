using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Safe hardware sensors collector that does NOT use WinRing0 or any kernel drivers.
    /// Uses only Windows-native APIs: WMI, Performance Counters, and vendor-specific usermode APIs.
    /// This avoids triggering Windows Defender alerts for vulnerable drivers.
    /// </summary>
    public class SafeHardwareSensorsCollector
    {
        public Task<HardwareSensorsResult> CollectAsync(CancellationToken ct)
        {
            return Task.Run(() => CollectInternal(ct), ct);
        }

        private HardwareSensorsResult CollectInternal(CancellationToken ct)
        {
            var result = CreateDefaultResult();
            result.CollectionExceptions = new List<string>();

            try
            {
                // Collect CPU temperature via WMI (safe, no driver needed)
                TryCollectCpuMetricsWmi(result);
                
                // Collect GPU metrics via WMI and Performance Counters
                TryCollectGpuMetricsSafe(result);
                
                // Collect disk temperatures via WMI S.M.A.R.T. (if available)
                TryCollectDiskMetricsWmi(result);

                result.CollectedAt = DateTimeOffset.Now;
                result.SafeModeUsed = true;
                
                App.LogMessage("[SafeSensors] Collection completed using safe mode (no kernel drivers)");
            }
            catch (Exception ex)
            {
                result.CollectionExceptions.Add($"SafeCollector: {ex.Message}");
                App.LogMessage($"[SafeSensors] Error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Collect CPU temperature via WMI ThermalZone (Windows built-in, no driver needed)
        /// </summary>
        private void TryCollectCpuMetricsWmi(HardwareSensorsResult result)
        {
            try
            {
                // Method 1: MSAcpi_ThermalZoneTemperature (most reliable)
                var wmiResult = WmiThermalZoneFallback.TryGetCpuTemp(minValidC: 5.0, maxValidC: 115.0);
                
                if (wmiResult.TempC.HasValue)
                {
                    result.Cpu.CpuTempC = Available(wmiResult.TempC.Value);
                    result.Cpu.CpuTempSource = $"WMI {wmiResult.Source}";
                    App.LogMessage($"[SafeSensors→CPU] Temperature: {wmiResult.TempC.Value:F1}°C via {wmiResult.Source}");
                }
                else
                {
                    // Method 2: Try Win32_TemperatureProbe (less common but sometimes available)
                    var tempProbe = TryGetWin32TemperatureProbe();
                    if (tempProbe.HasValue)
                    {
                        result.Cpu.CpuTempC = Available(tempProbe.Value);
                        result.Cpu.CpuTempSource = "WMI Win32_TemperatureProbe";
                    }
                    else
                    {
                        result.Cpu.CpuTempC = UnavailableDouble("Température CPU non accessible sans outils tiers");
                        result.Cpu.CpuTempSource = "Non disponible (mode sécurisé)";
                    }
                }
                
                // CPU Load via Performance Counter (always available)
                try
                {
                    using var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                    cpuCounter.NextValue(); // First call returns 0
                    Thread.Sleep(100);
                    var cpuLoad = cpuCounter.NextValue();
                    result.Cpu.CpuLoadPercent = Available(cpuLoad);
                }
                catch
                {
                    result.Cpu.CpuLoadPercent = UnavailableDouble("Charge CPU indisponible");
                }
            }
            catch (Exception ex)
            {
                result.CollectionExceptions?.Add($"CPU WMI: {ex.Message}");
                result.Cpu.CpuTempC = UnavailableDouble($"Erreur WMI: {ex.Message}");
                result.Cpu.CpuTempSource = "Erreur";
            }
        }

        /// <summary>
        /// Try to get CPU temperature from Win32_TemperatureProbe
        /// </summary>
        private double? TryGetWin32TemperatureProbe()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_TemperatureProbe");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var currentReading = obj["CurrentReading"];
                    if (currentReading != null)
                    {
                        var tempKelvin = Convert.ToDouble(currentReading) / 10.0;
                        var tempC = tempKelvin - 273.15;
                        if (tempC > 5 && tempC < 115)
                            return tempC;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Collect GPU metrics using safe methods (WMI, Performance Counters, NVML usermode)
        /// </summary>
        private void TryCollectGpuMetricsSafe(HardwareSensorsResult result)
        {
            try
            {
                // Get GPU name from WMI first
                string gpuName = "GPU inconnu";
                long vramTotalBytesWmi = 0;
                
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        gpuName = obj["Name"]?.ToString() ?? "GPU inconnu";
                        var adapterRam = obj["AdapterRAM"];
                        if (adapterRam != null)
                        {
                            vramTotalBytesWmi = Convert.ToInt64(adapterRam);
                        }
                        break; // Take first GPU
                    }
                }
                
                result.Gpu.Name = Available(gpuName);
                
                // Tentative NVML pour VRAM total (évite le dépassement UInt32 de WMI)
                bool nvmlVramSuccess = false;
                var nvmlMem = NvmlTemperatureReader.TryGetMemoryInfo();
                if (nvmlMem.HasValue && nvmlMem.Value.Total > 0)
                {
                    var totalMB = nvmlMem.Value.Total / (1024.0 * 1024.0);
                    result.Gpu.VramTotalMB = Available(totalMB);
                    result.Gpu.VramUsedMB = Available(nvmlMem.Value.Used / (1024.0 * 1024.0));
                    App.LogMessage($"[SafeSensors→GPU] VRAM via NVML: Total={totalMB:F0} Mo, Used={nvmlMem.Value.Used / (1024.0 * 1024.0):F0} Mo");
                    nvmlVramSuccess = true;
                }
                
                // Fallback WMI pour VRAM uniquement si NVML a échoué
                if (!nvmlVramSuccess)
                {
                    if (vramTotalBytesWmi > 0)
                    {
                        var vramTotalMB = vramTotalBytesWmi / (1024.0 * 1024.0);
                        
                        // Détection de dépassement UInt32: si vramTotalMB < 8192 et GPU haut de gamme connu
                        var gpuNameUpper = gpuName.ToUpperInvariant();
                        bool isHighEndGpu = gpuNameUpper.Contains("3090") || gpuNameUpper.Contains("4090") ||
                                           gpuNameUpper.Contains("3080") || gpuNameUpper.Contains("4080") ||
                                           gpuNameUpper.Contains("4070");
                        
                        if (isHighEndGpu && vramTotalMB < 8192)
                        {
                            // Dépassement UInt32 détecté - ne pas écrire la valeur fausse
                            App.LogMessage($"[SafeSensors→GPU] VRAM WMI overflow détecté: {vramTotalMB:F0} Mo pour {gpuName} (GPU haut de gamme > 8 Go attendu)");
                            result.Gpu.VramTotalMB = UnavailableDouble("VRAM overflow WMI (UInt32) - installer NVML pour valeur correcte");
                        }
                        else if (vramTotalMB > 0 && vramTotalMB < 100000)
                        {
                            result.Gpu.VramTotalMB = Available(vramTotalMB);
                            App.LogMessage($"[SafeSensors→GPU] VRAM via WMI fallback: {vramTotalMB:F0} Mo");
                        }
                        else
                        {
                            result.Gpu.VramTotalMB = UnavailableDouble("VRAM totale non fiable via WMI");
                        }
                    }
                    else
                    {
                        result.Gpu.VramTotalMB = UnavailableDouble("VRAM totale non disponible");
                    }
                }
                
                // VRAM Used: Try Performance Counters "GPU Engine" (Windows 10+) if not already set by NVML
                if (!nvmlVramSuccess)
                {
                    var vramUsed = TryGetGpuVramFromPerfCounters();
                    if (vramUsed.HasValue)
                    {
                        result.Gpu.VramUsedMB = Available(vramUsed.Value);
                        result.Gpu.VramUsedSource = "Performance Counters (GPU Memory)";
                    }
                    else
                    {
                        result.Gpu.VramUsedMB = UnavailableDouble("VRAM utilisée: voir Gestionnaire des tâches");
                        result.Gpu.VramUsedSource = "Non disponible (mode sécurisé)";
                    }
                }
                else
                {
                    result.Gpu.VramUsedSource = "NVIDIA NVML (usermode)";
                }
                
                // GPU Load via Performance Counters
                var gpuLoad = TryGetGpuLoadFromPerfCounters();
                if (gpuLoad.HasValue)
                {
                    result.Gpu.GpuLoadPercent = Available(gpuLoad.Value);
                }
                else
                {
                    result.Gpu.GpuLoadPercent = UnavailableDouble("Charge GPU: voir Gestionnaire des tâches");
                }
                
                // GPU Temperature: Try NVIDIA NVML (usermode DLL, no driver needed)
                if (gpuName.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var nvmlTemp = TryGetNvidiaTemperature();
                    if (nvmlTemp.HasValue)
                    {
                        result.Gpu.GpuTempC = Available(nvmlTemp.Value);
                        result.Gpu.GpuTempSource = "NVIDIA NVML (usermode)";
                        App.LogMessage($"[SafeSensors→GPU] NVIDIA temp: {nvmlTemp.Value:F1}°C via NVML");
                    }
                    else
                    {
                        result.Gpu.GpuTempC = UnavailableDouble("Température GPU: voir Gestionnaire des tâches");
                        result.Gpu.GpuTempSource = "Non disponible (mode sécurisé)";
                    }
                }
                else
                {
                    // AMD/Intel: No safe usermode API available
                    result.Gpu.GpuTempC = UnavailableDouble("Température GPU: voir Gestionnaire des tâches ou GPU-Z");
                    result.Gpu.GpuTempSource = "Non disponible (mode sécurisé)";
                }
            }
            catch (Exception ex)
            {
                result.CollectionExceptions?.Add($"GPU Safe: {ex.Message}");
                SetGpuUnavailable(result, $"Erreur: {ex.Message}");
            }
        }

        /// <summary>
        /// Try to get GPU VRAM usage from Windows Performance Counters (Windows 10+)
        /// </summary>
        private double? TryGetGpuVramFromPerfCounters()
        {
            try
            {
                // Windows 10 1709+ has GPU performance counters
                var category = new PerformanceCounterCategory("GPU Adapter Memory");
                var instances = category.GetInstanceNames();
                
                double totalDedicatedMB = 0;
                
                foreach (var instance in instances)
                {
                    using var counter = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", instance, true);
                    var value = counter.NextValue();
                    totalDedicatedMB += value / (1024.0 * 1024.0);
                }
                
                if (totalDedicatedMB > 0)
                    return totalDedicatedMB;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SafeSensors] GPU Memory PerfCounter error: {ex.Message}");
            }
            
            return null;
        }

        /// <summary>
        /// Try to get GPU load from Windows Performance Counters
        /// </summary>
        private double? TryGetGpuLoadFromPerfCounters()
        {
            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                var instances = category.GetInstanceNames();
                
                double maxUtilization = 0;
                
                foreach (var instance in instances)
                {
                    if (instance.Contains("engtype_3D"))
                    {
                        using var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
                        counter.NextValue();
                        Thread.Sleep(50);
                        var value = counter.NextValue();
                        if (value > maxUtilization)
                            maxUtilization = value;
                    }
                }
                
                if (maxUtilization > 0)
                    return maxUtilization;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SafeSensors] GPU Engine PerfCounter error: {ex.Message}");
            }
            
            return null;
        }

        /// <summary>
        /// Try to get NVIDIA GPU temperature using NVML (usermode DLL)
        /// NVML is installed with NVIDIA drivers and doesn't require kernel access
        /// </summary>
        private double? TryGetNvidiaTemperature()
        {
            try
            {
                // Check if nvml.dll exists
                var nvmlPath = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var nvmlDll = System.IO.Path.Combine(nvmlPath, "nvml.dll");
                
                if (!System.IO.File.Exists(nvmlDll))
                {
                    // Try in NVIDIA driver folder
                    nvmlDll = @"C:\Windows\System32\nvml.dll";
                    if (!System.IO.File.Exists(nvmlDll))
                    {
                        App.LogMessage("[SafeSensors] nvml.dll not found");
                        return null;
                    }
                }
                
                // Use NvmlWrapper if available
                return NvmlTemperatureReader.TryGetTemperature();
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SafeSensors] NVML error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Collect disk metrics using WMI (no driver needed)
        /// </summary>
        private void TryCollectDiskMetricsWmi(HardwareSensorsResult result)
        {
            try
            {
                result.Disks.Clear();
                
                // Get disk names from WMI
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
                var diskList = searcher.Get().Cast<ManagementObject>().ToList();
                
                if (diskList.Count == 0)
                {
                    var diskMetric = new DiskMetrics
                    {
                        Name = Unavailable("Aucun disque détecté"),
                        TempC = UnavailableDouble("Température non disponible")
                    };
                    result.Disks.Add(diskMetric);
                    return;
                }
                
                foreach (var disk in diskList)
                {
                    var diskName = disk["Model"]?.ToString() ?? disk["Caption"]?.ToString() ?? "Disque inconnu";
                    
                    var diskMetric = new DiskMetrics
                    {
                        Name = Available(diskName),
                        // S.M.A.R.T. temperature requires admin rights and special access
                        // In safe mode, we can't reliably get disk temperature
                        TempC = UnavailableDouble("Température disque: utiliser CrystalDiskInfo")
                    };
                    
                    result.Disks.Add(diskMetric);
                }
                
                // Try to get disk temperature via SMART WMI (may require admin)
                TryEnrichDiskTemperatureFromSmart(result);
            }
            catch (Exception ex)
            {
                result.CollectionExceptions?.Add($"Disks WMI: {ex.Message}");
                result.Disks.Clear();
                var diskMetric = new DiskMetrics
                {
                    Name = Unavailable($"Erreur: {ex.Message}"),
                    TempC = UnavailableDouble($"Erreur: {ex.Message}")
                };
                result.Disks.Add(diskMetric);
            }
        }

        /// <summary>
        /// Try to get disk temperature from WMI SMART data
        /// </summary>
        private void TryEnrichDiskTemperatureFromSmart(HardwareSensorsResult result)
        {
            try
            {
                // MSStorageDriver_ATAPISmartData requires admin rights
                using var searcher = new ManagementObjectSearcher(@"root\WMI", 
                    "SELECT * FROM MSStorageDriver_ATAPISmartData");
                
                var smartData = searcher.Get().Cast<ManagementObject>().ToList();
                
                // SMART attribute 194 (0xC2) is typically HDD temperature
                // SMART attribute 190 (0xBE) is sometimes used for SSD temperature
                
                int diskIndex = 0;
                foreach (var data in smartData)
                {
                    if (diskIndex >= result.Disks.Count) break;
                    
                    var vendorSpecific = data["VendorSpecific"] as byte[];
                    if (vendorSpecific != null && vendorSpecific.Length >= 362)
                    {
                        // Parse SMART attributes (each attribute is 12 bytes)
                        // Attribute 194 (0xC2) = Temperature
                        for (int i = 2; i < vendorSpecific.Length - 12; i += 12)
                        {
                            if (vendorSpecific[i] == 0xC2) // Attribute 194
                            {
                                var temp = vendorSpecific[i + 5];
                                if (temp > 0 && temp < 100)
                                {
                                    result.Disks[diskIndex].TempC = Available(temp);
                                    break;
                                }
                            }
                        }
                    }
                    diskIndex++;
                }
            }
            catch (Exception ex)
            {
                // SMART access often requires admin - silently ignore
                App.LogMessage($"[SafeSensors] SMART access failed (may need admin): {ex.Message}");
            }
        }

        private static HardwareSensorsResult CreateDefaultResult()
        {
            var res = new HardwareSensorsResult
            {
                CollectedAt = DateTimeOffset.Now,
                Gpu = new GpuMetrics
                {
                    Name = Unavailable("GPU non collecté"),
                    VramTotalMB = UnavailableDouble("VRAM totale non collectée"),
                    VramUsedMB = UnavailableDouble("VRAM utilisée non collectée"),
                    GpuLoadPercent = UnavailableDouble("Charge GPU non collectée"),
                    GpuTempC = UnavailableDouble("Température GPU non collectée")
                },
                Cpu = new CpuMetrics
                {
                    CpuTempC = UnavailableDouble("Température CPU non collectée"),
                    CpuTempSource = "N/A",
                    CpuLoadPercent = UnavailableDouble("Charge CPU non collectée")
                },
                Disks = new List<DiskMetrics>()
            };
            return res;
        }

        private static void SetGpuUnavailable(HardwareSensorsResult result, string reason)
        {
            result.Gpu.Name = Unavailable(reason);
            result.Gpu.VramTotalMB = UnavailableDouble(reason);
            result.Gpu.VramUsedMB = UnavailableDouble(reason);
            result.Gpu.GpuLoadPercent = UnavailableDouble(reason);
            result.Gpu.GpuTempC = UnavailableDouble(reason);
        }

        private static MetricValue<string> Available(string value) => new() { Available = true, Value = value };
        private static MetricValue<double> Available(double value) => new() { Available = true, Value = value };
        private static MetricValue<string> Unavailable(string reason) => new() { Available = false, Reason = reason };
        private static MetricValue<double> UnavailableDouble(string reason) => new() { Available = false, Reason = reason };
    }

    /// <summary>
    /// NVIDIA NVML temperature reader using P/Invoke
    /// NVML is a usermode library that doesn't require kernel drivers
    /// </summary>
    internal static class NvmlTemperatureReader
    {
        private const string NVML_DLL = "nvml.dll";

        [DllImport(NVML_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlInit_v2();

        [DllImport(NVML_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlShutdown();

        [DllImport(NVML_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetCount_v2(out uint deviceCount);

        [DllImport(NVML_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

        [DllImport(NVML_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temp);

        [StructLayout(LayoutKind.Sequential)]
        private struct nvmlMemory_t
        {
            public ulong Total;
            public ulong Free;
            public ulong Used;
        }

        [DllImport(NVML_DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out nvmlMemory_t mem);

        private const int NVML_SUCCESS = 0;
        private const int NVML_TEMPERATURE_GPU = 0; // Core temperature

        public static double? TryGetTemperature()
        {
            try
            {
                int result = nvmlInit_v2();
                if (result != NVML_SUCCESS)
                {
                    App.LogMessage($"[NVML] Init failed with code {result}");
                    return null;
                }

                try
                {
                    result = nvmlDeviceGetCount_v2(out uint deviceCount);
                    if (result != NVML_SUCCESS || deviceCount == 0)
                    {
                        App.LogMessage($"[NVML] No devices found (code {result}, count {deviceCount})");
                        return null;
                    }

                    // Get first GPU
                    result = nvmlDeviceGetHandleByIndex_v2(0, out IntPtr device);
                    if (result != NVML_SUCCESS)
                    {
                        App.LogMessage($"[NVML] GetHandle failed with code {result}");
                        return null;
                    }

                    result = nvmlDeviceGetTemperature(device, NVML_TEMPERATURE_GPU, out uint temperature);
                    if (result == NVML_SUCCESS && temperature > 0 && temperature < 150)
                    {
                        App.LogMessage($"[NVML] GPU temperature: {temperature}°C");
                        return temperature;
                    }

                    App.LogMessage($"[NVML] GetTemperature failed with code {result}");
                    return null;
                }
                finally
                {
                    nvmlShutdown();
                }
            }
            catch (DllNotFoundException)
            {
                App.LogMessage("[NVML] nvml.dll not found - NVIDIA drivers may not be installed");
                return null;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[NVML] Exception: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Try to get GPU memory info (Total/Used) via NVML.
        /// Returns null if NVML is not available or fails.
        /// This avoids the UInt32 overflow from WMI AdapterRAM for GPUs > 4GB.
        /// </summary>
        public static (ulong Total, ulong Used)? TryGetMemoryInfo()
        {
            try
            {
                int result = nvmlInit_v2();
                if (result != NVML_SUCCESS)
                {
                    App.LogMessage($"[NVML] Init failed with code {result} (memory info)");
                    return null;
                }

                try
                {
                    result = nvmlDeviceGetCount_v2(out uint deviceCount);
                    if (result != NVML_SUCCESS || deviceCount == 0)
                    {
                        App.LogMessage($"[NVML] No devices found for memory info (code {result}, count {deviceCount})");
                        return null;
                    }

                    // Get first GPU
                    result = nvmlDeviceGetHandleByIndex_v2(0, out IntPtr device);
                    if (result != NVML_SUCCESS)
                    {
                        App.LogMessage($"[NVML] GetHandle failed with code {result} (memory info)");
                        return null;
                    }

                    result = nvmlDeviceGetMemoryInfo(device, out nvmlMemory_t memInfo);
                    if (result == NVML_SUCCESS && memInfo.Total > 0)
                    {
                        App.LogMessage($"[NVML] GPU memory: Total={memInfo.Total / (1024 * 1024)} MB, Used={memInfo.Used / (1024 * 1024)} MB, Free={memInfo.Free / (1024 * 1024)} MB");
                        return (memInfo.Total, memInfo.Used);
                    }

                    App.LogMessage($"[NVML] GetMemoryInfo failed with code {result}");
                    return null;
                }
                finally
                {
                    nvmlShutdown();
                }
            }
            catch (DllNotFoundException)
            {
                App.LogMessage("[NVML] nvml.dll not found (memory info) - NVIDIA drivers may not be installed");
                return null;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[NVML] Exception (memory info): {ex.Message}");
                return null;
            }
        }
    }
}
