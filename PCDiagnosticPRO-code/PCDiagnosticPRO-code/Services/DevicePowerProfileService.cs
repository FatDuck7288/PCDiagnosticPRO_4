using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.Json;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Multi-signal heuristic used to decide if a battery is expected on this device.
    /// Signals:
    /// - Win32_Battery presence
    /// - Win32_SystemEnclosure.ChassisTypes (portable categories)
    /// - Win32_ComputerSystem.PCSystemType (mobile categories)
    /// Optional debug/test override:
    /// - PCDIAG_FORCE_LAPTOP_EXPECTED_BATTERY=auto|true|false
    /// </summary>
    public static class DevicePowerProfileService
    {
        private static readonly HashSet<int> LaptopChassisTypes = new()
        {
            8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32
        };

        private static readonly HashSet<int> MobilePcSystemTypes = new()
        {
            2, 8, 9
        };

        public sealed class DevicePowerProfile
        {
            public bool IsLaptopExpectedBattery { get; init; }
            public string Confidence { get; init; } = "Low";
            public bool HasBatteryHardware { get; init; }
            public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
        }

        public static DevicePowerProfile Detect(JsonElement root)
        {
            if (TryGetDebugOverride(out var overriddenExpected))
            {
                var forcedExpected = overriddenExpected ?? false;
                return new DevicePowerProfile
                {
                    IsLaptopExpectedBattery = forcedExpected,
                    Confidence = "High",
                    HasBatteryHardware = forcedExpected,
                    Reasons = new[] { "override_env:PCDIAG_FORCE_LAPTOP_EXPECTED_BATTERY" }
                };
            }

            var reasons = new List<string>();
            var score = 0;

            var batteryPresent = IsBatteryPresent(root);
            if (batteryPresent == true)
            {
                score++;
                reasons.Add("battery_device_present");
            }
            else
            {
                reasons.Add("battery_device_absent");
            }

            if (TryGetChassisIsLaptop(out var chassisLaptop))
            {
                if (chassisLaptop)
                {
                    score++;
                    reasons.Add("chassis_laptop");
                }
                else
                {
                    reasons.Add("chassis_not_laptop");
                }
            }
            else
            {
                reasons.Add("chassis_unknown");
            }

            if (TryGetPcSystemTypeIsMobile(out var mobileSystem))
            {
                if (mobileSystem)
                {
                    score++;
                    reasons.Add("pcsystemtype_mobile");
                }
                else
                {
                    reasons.Add("pcsystemtype_not_mobile");
                }
            }
            else
            {
                reasons.Add("pcsystemtype_unknown");
            }

            return new DevicePowerProfile
            {
                IsLaptopExpectedBattery = score >= 2 || batteryPresent == true,
                Confidence = score >= 2 ? "High" : "Low",
                HasBatteryHardware = batteryPresent == true,
                Reasons = reasons
            };
        }

        private static bool? IsBatteryPresent(JsonElement root)
        {
            try
            {
                var batteryData = GetSectionData(root, "Battery");
                if (batteryData.HasValue)
                {
                    var hasBattery = GetBool(batteryData, "hasBattery") ?? GetBool(batteryData, "present");
                    if (hasBattery.HasValue)
                        return hasBattery.Value;
                }
            }
            catch
            {
                // Ignore JSON parsing errors for this heuristic.
            }

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_Battery");
                using var results = searcher.Get();
                return results.Cast<ManagementObject>().Any();
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetChassisIsLaptop(out bool isLaptop)
        {
            isLaptop = false;
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
                foreach (ManagementObject enclosure in searcher.Get())
                {
                    var raw = enclosure["ChassisTypes"] as ushort[];
                    if (raw == null || raw.Length == 0)
                        continue;

                    if (raw.Any(v => LaptopChassisTypes.Contains(v)))
                    {
                        isLaptop = true;
                        return true;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetPcSystemTypeIsMobile(out bool isMobile)
        {
            isMobile = false;
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT PCSystemType FROM Win32_ComputerSystem");
                foreach (ManagementObject system in searcher.Get())
                {
                    var raw = system["PCSystemType"];
                    if (raw == null)
                        continue;

                    var type = Convert.ToInt32(raw);
                    if (MobilePcSystemTypes.Contains(type))
                    {
                        isMobile = true;
                        return true;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetDebugOverride(out bool? overrideValue)
        {
            overrideValue = null;
            var raw = Environment.GetEnvironmentVariable("PCDIAG_FORCE_LAPTOP_EXPECTED_BATTERY");
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            if (!IsDebugOrTestContext())
                return false;

            raw = raw.Trim();
            if (raw.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return false;
            if (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1")
            {
                overrideValue = true;
                return true;
            }

            if (raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw == "0")
            {
                overrideValue = false;
                return true;
            }

            return false;
        }

        private static bool IsDebugOrTestContext()
        {
#if DEBUG
            return true;
#else
            var testFlag = Environment.GetEnvironmentVariable("PCDIAG_TEST_MODE");
            return string.Equals(testFlag, "1", StringComparison.OrdinalIgnoreCase);
#endif
        }

        private static JsonElement? GetSectionData(JsonElement root, string sectionName)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (!root.TryGetProperty("scan_powershell", out var scan) || scan.ValueKind != JsonValueKind.Object)
                return null;

            if (!scan.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var section in sections.EnumerateObject())
            {
                if (!section.Name.Equals(sectionName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (section.Value.ValueKind != JsonValueKind.Object)
                    return null;

                if (section.Value.TryGetProperty("data", out var data))
                    return data;

                return section.Value;
            }

            return null;
        }

        private static bool? GetBool(JsonElement? element, string property)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var prop in element.Value.EnumerateObject())
            {
                if (!prop.Name.Equals(property, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (prop.Value.ValueKind == JsonValueKind.True)
                    return true;
                if (prop.Value.ValueKind == JsonValueKind.False)
                    return false;
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var intValue))
                    return intValue != 0;
                if (prop.Value.ValueKind == JsonValueKind.String &&
                    bool.TryParse(prop.Value.GetString(), out var parsed))
                {
                    return parsed;
                }
            }

            return null;
        }
    }
}
