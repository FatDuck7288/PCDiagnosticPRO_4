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
    /// CPU temp: WMI only (MSAcpi_ThermalZoneTemperature, TemperatureProbe, ThermalZoneInformation)
    /// — these methods do NOT trigger any security signal; often "Non disponible" on gaming desktops
    /// where ACPI Thermal Zone is empty. No PerfCounter for CPU temp (Windows does not expose it); no LHM in safe mode.
    /// See docs/CPU_TEMPERATURE_AND_THROTTLING.md for all CPU temperature methods and "no signal" options.
    /// GPU load: Performance Counter "GPU Engine" with engtype_3D only (aligned with Task Manager "GPU 3D").
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
                    // WmiThermalZoneFallback already tried MSAcpi, TemperatureProbe, ThermalZoneInformation
                    var reason = !string.IsNullOrEmpty(wmiResult.Reason)
                        ? wmiResult.Reason
                        : "ACPI ThermalZone vide; TemperatureProbe et ThermalZoneInformation non disponibles (mode sécurisé)";
                    result.Cpu.CpuTempC = UnavailableDouble(reason);
                    result.Cpu.CpuTempSource = "Non disponible (mode sécurisé)";
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
                
                // VRAM Total: NVML first (avoids WMI UInt32 overflow), then WMI fallback.
                var nvmlMem = NvmlTemperatureReader.TryGetMemoryInfo();
                if (nvmlMem.HasValue && nvmlMem.Value.Total > 0)
                {
                    var totalMB = nvmlMem.Value.Total / (1024.0 * 1024.0);
                    result.Gpu.VramTotalMB = Available(totalMB);
                    App.LogMessage($"[SafeSensors→GPU] VRAM Total via NVML: {totalMB:F0} Mo");
                }
                else if (vramTotalBytesWmi > 0)
                {
                    var vramTotalMBWmi = vramTotalBytesWmi / (1024.0 * 1024.0);
                    var gpuNameUpper = gpuName.ToUpperInvariant();
                    bool isHighEndGpu = gpuNameUpper.Contains("3090") || gpuNameUpper.Contains("4090") ||
                                       gpuNameUpper.Contains("3080") || gpuNameUpper.Contains("4080") ||
                                       gpuNameUpper.Contains("4070");
                    if (isHighEndGpu && vramTotalMBWmi < 8192)
                    {
                        App.LogMessage($"[SafeSensors→GPU] VRAM WMI overflow détecté: {vramTotalMBWmi:F0} Mo pour {gpuName}");
                        result.Gpu.VramTotalMB = UnavailableDouble("VRAM overflow WMI (UInt32) - installer NVML pour valeur correcte");
                    }
                    else if (vramTotalMBWmi > 0 && vramTotalMBWmi < 100000)
                    {
                        result.Gpu.VramTotalMB = Available(vramTotalMBWmi);
                        App.LogMessage($"[SafeSensors→GPU] VRAM Total via WMI: {vramTotalMBWmi:F0} Mo");
                    }
                    else
                        result.Gpu.VramTotalMB = UnavailableDouble("VRAM totale non fiable via WMI");
                }
                else
                    result.Gpu.VramTotalMB = UnavailableDouble("VRAM totale non disponible");
                
                // VRAM Used: Perf Counter "Dedicated Usage" + NVML Used as candidates; take minimum to avoid committed-style ~11 GB when Task Manager shows ~3 GB.
                var vramTotalMB = result.Gpu.VramTotalMB?.Available == true ? result.Gpu.VramTotalMB.Value : (double?)null;
                double? nvmlUsedMB = null;
                if (nvmlMem.HasValue && nvmlMem.Value.Used > 0)
                    nvmlUsedMB = nvmlMem.Value.Used / (1024.0 * 1024.0);
                var vramUsed = TryGetGpuVramUsed(vramTotalMB, nvmlUsedMB);
                if (vramUsed.HasValue)
                {
                    result.Gpu.VramUsedMB = Available(vramUsed.Value);
                    result.Gpu.VramUsedSource = "Performance Counters (Dedicated Usage — matches Task Manager)";
                    App.LogMessage($"[SafeSensors→GPU] VRAM Used: {vramUsed.Value:F0} Mo (Task Manager equivalent)");
                }
                else
                {
                    result.Gpu.VramUsedMB = UnavailableDouble("VRAM utilisée: voir Gestionnaire des tâches");
                    result.Gpu.VramUsedSource = "Non disponible (mode sécurisé)";
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
        /// Gets dedicated GPU memory usage: Perf Counter "Dedicated Usage" + optional NVML Used. Returns minimum of all candidates so we never display committed-style ~11 GB when Task Manager shows ~3 GB. Rejects single value &gt; 8 GB as suspicious.
        /// </summary>
        private double? TryGetGpuVramUsed(double? vramTotalMB, double? nvmlUsedMB)
        {
            var candidates = new List<double>();
            try
            {
                // 1) Add NVML Used if available and sensible (often matches Task Manager dedicated)
                if (nvmlUsedMB.HasValue && nvmlUsedMB.Value > 0)
                {
                    if (!vramTotalMB.HasValue || nvmlUsedMB.Value <= vramTotalMB.Value)
                    {
                        candidates.Add(nvmlUsedMB.Value);
                        App.LogMessage($"[SafeSensors→VRAM] Candidate NVML Used: {nvmlUsedMB.Value:F0} Mo");
                    }
                }

                // 2) Perf counter instances (some report committed ~11 GB)
                var category = new PerformanceCounterCategory("GPU Adapter Memory");
                var instances = category.GetInstanceNames();
                if (instances != null && instances.Length > 0)
                {
                    App.LogMessage($"[SafeSensors→VRAM] Perf instances: {instances.Length} ({string.Join(", ", instances)})");
                    foreach (var instance in instances)
                    {
                        try
                        {
                            using var counter = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", instance, true);
                            var value = counter.NextValue();
                            var dedicatedMB = value / (1024.0 * 1024.0);
                            if (dedicatedMB <= 0) continue;
                            if (vramTotalMB.HasValue && dedicatedMB > vramTotalMB.Value)
                            {
                                App.LogMessage($"[SafeSensors→VRAM] Skip instance '{instance}': {dedicatedMB:F0} Mo > total {vramTotalMB.Value:F0} Mo");
                                continue;
                            }
                            candidates.Add(dedicatedMB);
                            App.LogMessage($"[SafeSensors→VRAM] Candidate Perf '{instance}': {dedicatedMB:F0} Mo");
                        }
                        catch (Exception ex) { App.LogMessage($"[SafeSensors→VRAM] Perf instance '{instance}': {ex.Message}"); }
                    }
                }

                if (candidates.Count == 0) return null;
                var chosen = candidates.Min();
                // Never display committed-style value: Task Manager "Dedicated GPU memory" is typically < 8 GB; 11 GB is wrong (committed).
                const double MaxReasonableDedicatedMB = 8000; // 8 GB
                if (chosen > MaxReasonableDedicatedMB)
                {
                    App.LogMessage($"[SafeSensors→VRAM] Reject {chosen:F0} Mo (> {MaxReasonableDedicatedMB:F0} Mo, committed not dedicated — cf. Gestionnaire des tâches)");
                    return null;
                }
                App.LogMessage($"[SafeSensors→VRAM] Chosen: {chosen:F0} Mo (min of {candidates.Count} candidates)");
                return chosen;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SafeSensors] GPU VRAM error: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Try to get GPU load from Windows Performance Counters.
        /// Uses only "engtype_3D" instances so the value matches Task Manager "GPU 3D" (not Copy/Video Decode).
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
