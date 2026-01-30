using System;
using System.Text.Json;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Helper pour extraire la température SMART correctement.
    /// Les attributs SMART 194 (Temperature_Celsius) et 190 (Airflow_Temperature_Cel) 
    /// contiennent souvent 4 bytes où le low byte = température actuelle.
    /// Valeurs aberrantes comme 917541 = 0x000E0005 indiquent une lecture raw non parsée.
    /// </summary>
    public static class SmartTemperatureHelper
    {
        /// <summary>
        /// Tente d'extraire une température valide depuis une valeur SMART raw.
        /// </summary>
        /// <param name="rawValue">Valeur brute (peut être la température directe ou un raw 4-byte)</param>
        /// <returns>Température en °C si valide, null sinon</returns>
        public static double? ExtractTemperature(double? rawValue)
        {
            if (!rawValue.HasValue)
                return null;

            var value = rawValue.Value;

            // Cas 1: Valeur directe valide (0-100°C)
            if (value >= 0 && value <= 100)
            {
                return value;
            }

            // Cas 2: Valeur négative ou sentinelle
            if (value < 0 || double.IsNaN(value) || double.IsInfinity(value))
            {
                return null;
            }

            // Cas 3: Raw 4-byte value (ex: 917541 = 0x000E0005)
            // Le low byte contient généralement la température actuelle
            if (value > 100 && value < int.MaxValue)
            {
                int rawInt = (int)value;
                int lowByte = rawInt & 0xFF;

                // Validation: température plausible (5-85°C pour un disque)
                if (lowByte >= 5 && lowByte <= 85)
                {
                    App.LogMessage($"[SmartTemp] Raw {rawInt} (0x{rawInt:X8}) -> lowByte {lowByte}°C");
                    return lowByte;
                }

                // Essayer le second byte (certains firmwares inversent)
                int secondByte = (rawInt >> 8) & 0xFF;
                if (secondByte >= 5 && secondByte <= 85)
                {
                    App.LogMessage($"[SmartTemp] Raw {rawInt} (0x{rawInt:X8}) -> secondByte {secondByte}°C");
                    return secondByte;
                }

                App.LogMessage($"[SmartTemp] Raw {rawInt} aberrant: lowByte={lowByte}, secondByte={secondByte} - rejeté");
                return null;
            }

            return null;
        }

        /// <summary>
        /// Normalise les températures SMART dans un élément JSON PS.
        /// </summary>
        public static void NormalizeSmartTemperatures(JsonElement psRoot, out int normalizedCount, out int failedCount)
        {
            normalizedCount = 0;
            failedCount = 0;

            try
            {
                if (!psRoot.TryGetProperty("sections", out var sections))
                    return;

                // Section Temperatures
                if (sections.TryGetProperty("Temperatures", out var temps) && temps.TryGetProperty("data", out var tempData))
                {
                    if (tempData.TryGetProperty("disks", out var disks) && disks.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var disk in disks.EnumerateArray())
                        {
                            if (disk.TryGetProperty("tempC", out var tempC))
                            {
                                double? rawTemp = null;
                                if (tempC.ValueKind == JsonValueKind.Number)
                                    rawTemp = tempC.GetDouble();

                                var normalized = ExtractTemperature(rawTemp);
                                if (normalized.HasValue)
                                    normalizedCount++;
                                else if (rawTemp.HasValue)
                                    failedCount++;
                            }
                        }
                    }
                }

                // Section SmartDetails
                if (sections.TryGetProperty("SmartDetails", out var smart) && smart.TryGetProperty("data", out var smartData))
                {
                    if (smartData.TryGetProperty("disks", out var smartDisks))
                    {
                        if (smartDisks.ValueKind == JsonValueKind.Object)
                        {
                            if (smartDisks.TryGetProperty("temperature", out var temp))
                            {
                                double? rawTemp = null;
                                if (temp.ValueKind == JsonValueKind.Number)
                                    rawTemp = temp.GetDouble();

                                var normalized = ExtractTemperature(rawTemp);
                                if (normalized.HasValue)
                                    normalizedCount++;
                                else if (rawTemp.HasValue)
                                    failedCount++;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SmartTemp] Erreur normalisation: {ex.Message}");
            }
        }

        /// <summary>
        /// Retourne une description de la température pour le TXT.
        /// </summary>
        public static string GetTemperatureDisplay(double? rawValue, string diskModel)
        {
            var temp = ExtractTemperature(rawValue);
            if (temp.HasValue)
            {
                return $"{temp.Value:F0}°C";
            }

            if (rawValue.HasValue && rawValue.Value > 100)
            {
                return $"N/A (raw aberrant: {rawValue.Value:F0})";
            }

            return "N/A";
        }
    }
}
