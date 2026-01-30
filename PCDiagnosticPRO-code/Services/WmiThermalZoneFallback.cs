using System;
using System.Management;

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

                return (null, "WMI_ThermalZone", "thermal_zone_not_available");
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
    }
}
