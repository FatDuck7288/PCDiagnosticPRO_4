using System;
using System.Management;
using System.Runtime.InteropServices;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Fallback pour température CPU via WMI MSAcpi_ThermalZoneTemperature.
    /// Utilisé quand LibreHardwareMonitor retourne une sentinelle (0°C).
    /// </summary>
    public static class WmiThermalZoneFallback
    {
        /// <summary>
        /// Tente de récupérer la température CPU via WMI ThermalZone.
        /// Retourne null si indisponible ou hors plage valide.
        /// </summary>
        /// <param name="minValidC">Température minimum valide (défaut: 5°C)</param>
        /// <param name="maxValidC">Température maximum valide (défaut: 115°C)</param>
        public static (double? TempC, string Source, string? Reason) TryGetCpuTemp(
            double minValidC = 5.0, 
            double maxValidC = 115.0)
        {
            try
            {
                // Méthode 1: MSAcpi_ThermalZoneTemperature (standard ACPI)
                var result = TryMsAcpiThermalZone(minValidC, maxValidC);
                if (result.TempC.HasValue)
                    return result;

                // Méthode 2: Win32_TemperatureProbe (moins courant)
                result = TryWin32TemperatureProbe(minValidC, maxValidC);
                if (result.TempC.HasValue)
                    return result;

                // Méthode 3: Win32_PerfFormattedData_Counters_ThermalZoneInformation (Windows 10+)
                result = TryWin32PerfThermalZoneInformation(minValidC, maxValidC);
                if (result.TempC.HasValue)
                    return result;

                // Méthode 4: HWiNFO shared memory (optionnel, si HWiNFO est lancé avec Shared Memory)
                result = TryHwInfoSharedMemory(minValidC, maxValidC);
                if (result.TempC.HasValue)
                    return result;

                return (null, "WMI_ThermalZone", "ACPI ThermalZone vide; TemperatureProbe, ThermalZoneInformation et HWiNFO non disponibles (mode sécurisé)");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[WMI ThermalZone] Erreur: {ex.Message}");
                return (null, "WMI_ThermalZone", $"wmi_error: {ex.Message}");
            }
        }

        private static (double? TempC, string Source, string? Reason) TryMsAcpiThermalZone(
            double minValidC, double maxValidC)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\WMI",
                    "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

                searcher.Options.Timeout = TimeSpan.FromSeconds(5);

                double maxTemp = double.MinValue;
                int validCount = 0;

                foreach (ManagementObject obj in searcher.Get())
                {
                    try
                    {
                        var kelvinRaw = obj["CurrentTemperature"];
                        if (kelvinRaw == null) continue;

                        // WMI retourne en dixièmes de Kelvin
                        double kelvin = Convert.ToDouble(kelvinRaw);
                        double celsius = (kelvin - 2732.0) / 10.0;

                        // Validation plage
                        if (celsius >= minValidC && celsius <= maxValidC)
                        {
                            validCount++;
                            if (celsius > maxTemp)
                                maxTemp = celsius;
                        }
                        else
                        {
                            App.LogMessage($"[WMI ThermalZone] Valeur hors plage: {celsius:F1}°C (kelvin raw: {kelvin})");
                        }
                    }
                    catch { /* Skip invalid entry */ }
                }

                if (validCount > 0)
                {
                    App.LogMessage($"[WMI ThermalZone] Température: {maxTemp:F1}°C (max de {validCount} zones)");
                    return (maxTemp, "WMI_MSAcpi_ThermalZone", null);
                }

                return (null, "WMI_MSAcpi_ThermalZone", "no_valid_thermal_zone");
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace)
            {
                App.LogMessage("[WMI ThermalZone] Namespace root\\WMI non disponible");
                return (null, "WMI_MSAcpi_ThermalZone", "namespace_not_available");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[WMI ThermalZone] MSAcpi erreur: {ex.Message}");
                return (null, "WMI_MSAcpi_ThermalZone", $"error: {ex.Message}");
            }
        }

        private static (double? TempC, string Source, string? Reason) TryWin32TemperatureProbe(
            double minValidC, double maxValidC)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\CIMV2",
                    "SELECT CurrentReading FROM Win32_TemperatureProbe WHERE Status='OK'");

                searcher.Options.Timeout = TimeSpan.FromSeconds(3);

                foreach (ManagementObject obj in searcher.Get())
                {
                    try
                    {
                        var reading = obj["CurrentReading"];
                        if (reading == null) continue;

                        // Win32_TemperatureProbe retourne en dixièmes de Celsius
                        double celsius = Convert.ToDouble(reading) / 10.0;

                        if (celsius >= minValidC && celsius <= maxValidC)
                        {
                            App.LogMessage($"[WMI ThermalZone] Win32_TemperatureProbe: {celsius:F1}°C");
                            return (celsius, "WMI_Win32_TemperatureProbe", null);
                        }
                    }
                    catch { /* Skip invalid entry */ }
                }

                return (null, "WMI_Win32_TemperatureProbe", "no_valid_probe");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[WMI ThermalZone] Win32_TemperatureProbe erreur: {ex.Message}");
                return (null, "WMI_Win32_TemperatureProbe", $"error: {ex.Message}");
            }
        }

        /// <summary>
        /// Tente la température via Win32_PerfFormattedData_Counters_ThermalZoneInformation (Windows 10+).
        /// HighPrecisionTemperature est en dixièmes de Kelvin.
        /// </summary>
        private static (double? TempC, string Source, string? Reason) TryWin32PerfThermalZoneInformation(
            double minValidC, double maxValidC)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\CIMV2",
                    "SELECT HighPrecisionTemperature, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");

                searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                double maxTemp = double.MinValue;
                int validCount = 0;

                foreach (ManagementObject obj in searcher.Get())
                {
                    try
                    {
                        // HighPrecisionTemperature = tenths of Kelvin (e.g. 3032 = 30.0°C)
                        object? raw = obj["HighPrecisionTemperature"];
                        if (raw == null)
                            raw = obj["Temperature"];
                        if (raw == null) continue;

                        double tenthsKelvin = Convert.ToDouble(raw);
                        double celsius = (tenthsKelvin / 10.0) - 273.15;

                        if (celsius >= minValidC && celsius <= maxValidC)
                        {
                            validCount++;
                            if (celsius > maxTemp)
                                maxTemp = celsius;
                        }
                    }
                    catch { /* skip */ }
                }

                if (validCount > 0)
                {
                    App.LogMessage($"[WMI ThermalZone] ThermalZoneInformation: {maxTemp:F1}°C (zones: {validCount})");
                    return (maxTemp, "WMI_ThermalZoneInformation", null);
                }
                return (null, "WMI_ThermalZoneInformation", "no_valid_thermal_zone");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[WMI ThermalZone] ThermalZoneInformation error: {ex.Message}");
                return (null, "WMI_ThermalZoneInformation", $"error: {ex.Message}");
            }
        }

        /// <summary>
        /// Tente de lire la température CPU depuis la shared memory HWiNFO (Global\HWiNFO_SENS_SM2).
        /// Optionnel, best-effort : ne fonctionne que si HWiNFO est lancé avec "Shared Memory" activé.
        /// </summary>
        private static (double? TempC, string Source, string? Reason) TryHwInfoSharedMemory(
            double minValidC, double maxValidC)
        {
            IntPtr hMap = IntPtr.Zero;
            IntPtr pView = IntPtr.Zero;
            try
            {
                hMap = OpenFileMappingW(0x0002 /* FILE_MAP_READ */, false, "Global\\HWiNFO_SENS_SM2");
                if (hMap == IntPtr.Zero)
                    return (null, "HWiNFO_SM2", "not_present");

                pView = MapViewOfFile(hMap, 0x0004 /* FILE_MAP_READ */, 0, 0, 0);
                if (pView == IntPtr.Zero)
                    return (null, "HWiNFO_SM2", "map_failed");

                // HWiNFO SM2 layout (reverse-engineered): header has dwReadingOffset at offset 8
                if (Marshal.ReadInt32(pView, 8) is int dwReadingOffset && dwReadingOffset >= 16 && dwReadingOffset < 0x10000)
                {
                    // Each sensor reading element: value (double) at offset 0x11C within element; element size ~0x140
                    const int valueOffsetInElement = 0x11C;
                    const int elementSize = 0x140;
                    for (int i = 0; i < 20; i++)
                    {
                        int offset = dwReadingOffset + i * elementSize + valueOffsetInElement;
                        try
                        {
                            double value = Marshal.PtrToStructure<double>(IntPtr.Add(pView, offset));
                            if (!double.IsNaN(value) && !double.IsInfinity(value) && value >= minValidC && value <= maxValidC)
                            {
                                App.LogMessage($"[WMI ThermalZone] HWiNFO shared memory: {value:F1}°C");
                                return (value, "HWiNFO_SM2", null);
                            }
                        }
                        catch { /* skip */ }
                    }
                }
                return (null, "HWiNFO_SM2", "no_valid_temperature_in_sm2");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[WMI ThermalZone] HWiNFO SM2 error: {ex.Message}");
                return (null, "HWiNFO_SM2", $"error: {ex.Message}");
            }
            finally
            {
                if (pView != IntPtr.Zero) UnmapViewOfFile(pView);
                if (hMap != IntPtr.Zero) CloseHandle(hMap);
            }
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenFileMappingW(uint dwDesiredAccess, bool bInheritHandle, string lpName);

        [DllImport("kernel32", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
