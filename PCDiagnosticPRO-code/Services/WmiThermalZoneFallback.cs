using System;
using System.Management;
using System.Runtime.InteropServices;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Fallback pour température CPU via WMI (aucun pilote tiers, aucun signal sécurité).
    /// Méthodes utilisées: MSAcpi_ThermalZoneTemperature, Win32_TemperatureProbe,
    /// Win32_PerfFormattedData_Counters_ThermalZoneInformation, HWiNFO Shared Memory (lecture seule).
    /// Utilisé quand LibreHardwareMonitor retourne une sentinelle (0°C) ou en mode sécurisé.
    /// Voir docs/CPU_TEMPERATURE_AND_THROTTLING.md.
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
                // Méthode 0: HWiNFO shared memory EN PREMIER — aucune alerte Defender (lecture seule, pas de pilote chargé par notre app).
                // Fonctionne si l'utilisateur lance HWiNFO64 (Sensors Only) avec "Shared Memory Support" activé.
                var result = TryHwInfoSharedMemory(minValidC, maxValidC);
                if (result.TempC.HasValue)
                    return result;

                // Méthode 1: MSAcpi_ThermalZoneTemperature (standard ACPI)
                result = TryMsAcpiThermalZone(minValidC, maxValidC);
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

                return (null, "WMI_ThermalZone", "ACPI ThermalZone vide; TemperatureProbe et ThermalZoneInformation non disponibles (mode sécurisé). Astuce: lancer HWiNFO64 (Sensors) avec Shared Memory pour afficher la température sans alerte Defender.");
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
        /// Aucun pilote chargé par notre app — pas d'alerte Defender. HWiNFO utilise son propre pilote (signé).
        /// Layout SM2: HWiNFOHeader (magic SiWH, entry_section_offset 0x20, entry_element_size 0x24, entry_element_count 0x28),
        /// HWiNFOEntry (type 1=Temperature, name_original 0x0C, value 0x11C). On privilégie Package / Tctl / Tdie / Core.
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

                const uint HWiNFO_HEADER_MAGIC = 0x43695357; // 'SiWH'
                if (Marshal.ReadInt32(pView, 0) != (int)HWiNFO_HEADER_MAGIC)
                    return (null, "HWiNFO_SM2", "invalid_header_magic");

                int sensorSectionOffset = Marshal.ReadInt32(pView, 0x14);
                int sensorElementSize = Marshal.ReadInt32(pView, 0x18);
                int entrySectionOffset = Marshal.ReadInt32(pView, 0x20);
                int entryElementSize = Marshal.ReadInt32(pView, 0x24);
                int entryElementCount = Marshal.ReadInt32(pView, 0x28);
                if (entrySectionOffset <= 0 || entryElementSize < 0x124 || entryElementCount <= 0 || entryElementCount > 2000)
                    return (null, "HWiNFO_SM2", "invalid_header_layout");

                const int SensorTypeTemperature = 1;
                const int entrySensorIndexOffset = 0x04;
                const int entryNameOriginalOffset = 0x0C;
                const int entryValueOffset = 0x11C;
                const int sensorNameOriginalOffset = 0x08;

                double? cpuPackageTemp = null;
                string? cpuPackageName = null;
                double? cpuCoreMaxTemp = null;
                string? cpuCoreMaxName = null;
                double? firstValidTemp = null;
                string? firstValidName = null;

                for (int i = 0; i < entryElementCount; i++)
                {
                    int entryBase = entrySectionOffset + i * entryElementSize;
                    int type = Marshal.ReadInt32(pView, entryBase + 0);
                    if (type != SensorTypeTemperature)
                        continue;

                    try
                    {
                        int sensorIndex = Marshal.ReadInt32(pView, entryBase + entrySensorIndexOffset);
                        string sensorName = "";
                        if (sensorSectionOffset > 0 && sensorElementSize >= 0x88 && sensorIndex >= 0 && sensorIndex < 500)
                        {
                            int sensorBase = sensorSectionOffset + sensorIndex * sensorElementSize;
                            sensorName = Marshal.PtrToStringAnsi(IntPtr.Add(pView, sensorBase + sensorNameOriginalOffset), 128) ?? "";
                            sensorName = sensorName.TrimEnd('\0').Trim();
                        }
                        bool isCpuSensor = sensorName.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                          sensorName.IndexOf("Processor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                          (sensorName.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0 && sensorName.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                          (sensorName.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0 && sensorName.IndexOf("Ryzen", StringComparison.OrdinalIgnoreCase) >= 0);

                        string name = Marshal.PtrToStringAnsi(IntPtr.Add(pView, entryBase + entryNameOriginalOffset), 128) ?? "";
                        name = (name ?? "").TrimEnd('\0').Trim();
                        double value = Marshal.PtrToStructure<double>(IntPtr.Add(pView, entryBase + entryValueOffset));
                        if (double.IsNaN(value) || double.IsInfinity(value) || value < minValidC || value > maxValidC)
                            continue;

                        if (firstValidTemp == null && isCpuSensor)
                        {
                            firstValidTemp = value;
                            firstValidName = name;
                        }

                        if (!isCpuSensor)
                            continue;

                        if (name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (!cpuPackageTemp.HasValue || value > cpuPackageTemp.Value)
                            {
                                cpuPackageTemp = value;
                                cpuPackageName = name;
                            }
                        }
                        else if (name.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 name.IndexOf("Tdie", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 name.IndexOf("CCD", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (!cpuCoreMaxTemp.HasValue || value > cpuCoreMaxTemp.Value)
                            {
                                cpuCoreMaxTemp = value;
                                cpuCoreMaxName = name;
                            }
                        }
                    }
                    catch { /* skip corrupt entry */ }
                }

                double? chosen = cpuPackageTemp ?? cpuCoreMaxTemp ?? firstValidTemp;
                string? chosenName = cpuPackageName ?? cpuCoreMaxName ?? firstValidName;
                if (chosen.HasValue)
                {
                    string source = string.IsNullOrEmpty(chosenName) ? "HWiNFO_SM2" : $"HWiNFO_SM2 ({chosenName})";
                    App.LogMessage($"[WMI ThermalZone] HWiNFO shared memory: {chosen.Value:F1}°C ({source})");
                    return (chosen.Value, source, null);
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
