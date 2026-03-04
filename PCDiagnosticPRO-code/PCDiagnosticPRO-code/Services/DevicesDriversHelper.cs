using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// BLOC 5: Helper pour structurer et grouper les DevicesDrivers
    /// </summary>
    public static class DevicesDriversHelper
    {
        /// <summary>
        /// Types de périphériques pour le groupement
        /// </summary>
        public enum DeviceCategory
        {
            Display,
            Storage,
            Network,
            USB,
            HID,
            Audio,
            Virtual,
            System,
            Unknown
        }

        /// <summary>
        /// Périphérique problématique structuré
        /// </summary>
        public class ProblemDevice
        {
            public string DeviceName { get; set; } = "";
            public string PNPDeviceId { get; set; } = "";
            public int ErrorCode { get; set; }
            public string ErrorDescription { get; set; } = "";
            public string? DriverProvider { get; set; }
            public string? DriverVersion { get; set; }
            public DeviceCategory Category { get; set; } = DeviceCategory.Unknown;
            public string Status { get; set; } = "";
            
            /// <summary>Sévérité: Critical (GPU/Storage/System), Important (Network/Audio), Minor (USB/HID/Virtual)</summary>
            public string Severity => Category switch
            {
                DeviceCategory.Display => "Critical",
                DeviceCategory.Storage => "Critical",
                DeviceCategory.System => "Critical",
                DeviceCategory.Network => "Important",
                DeviceCategory.Audio => "Important",
                _ => "Minor"
            };
        }

        /// <summary>
        /// Résultat du parsing des DevicesDrivers
        /// </summary>
        public class DevicesDriversSummary
        {
            public int TotalDevices { get; set; }
            public int ProblemDeviceCount { get; set; }
            public Dictionary<DeviceCategory, List<ProblemDevice>> GroupedDevices { get; set; } = new();
            public List<ProblemDevice> Top10Problems { get; set; } = new();
            
            /// <summary>Nombre d'erreurs critiques</summary>
            public int CriticalCount => GroupedDevices
                .SelectMany(g => g.Value)
                .Count(d => d.Severity == "Critical");
                
            /// <summary>Nombre d'erreurs importantes</summary>
            public int ImportantCount => GroupedDevices
                .SelectMany(g => g.Value)
                .Count(d => d.Severity == "Important");
                
            /// <summary>Nombre d'erreurs mineures</summary>
            public int MinorCount => GroupedDevices
                .SelectMany(g => g.Value)
                .Count(d => d.Severity == "Minor");
        }

        /// <summary>
        /// Parse et structure les DevicesDrivers depuis le JSON PS
        /// </summary>
        public static DevicesDriversSummary ParseDevicesDrivers(JsonElement? psData)
        {
            var summary = new DevicesDriversSummary();
            
            if (!psData.HasValue) return summary;
            
            try
            {
                // Chercher DevicesDrivers dans sections ou racine
                JsonElement devicesElement = default;
                bool found = false;
                
                if (psData.Value.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("DevicesDrivers", out var dd))
                {
                    devicesElement = dd;
                    found = true;
                }
                else if (psData.Value.TryGetProperty("DevicesDrivers", out var ddDirect))
                {
                    devicesElement = ddDirect;
                    found = true;
                }
                
                if (!found) return summary;
                
                // Extraire data
                JsonElement data = devicesElement;
                if (devicesElement.TryGetProperty("data", out var dataElem))
                    data = dataElem;
                
                // Parser problemDevices
                if (data.TryGetProperty("problemDevices", out var problemDevices) && 
                    problemDevices.ValueKind == JsonValueKind.Array)
                {
                    var allProblems = new List<ProblemDevice>();
                    
                    foreach (var device in problemDevices.EnumerateArray())
                    {
                        var pd = new ProblemDevice();
                        
                        if (device.TryGetProperty("name", out var name))
                            pd.DeviceName = name.GetString() ?? "";
                        if (device.TryGetProperty("deviceName", out var dn))
                            pd.DeviceName = dn.GetString() ?? "";
                            
                        if (device.TryGetProperty("pnpDeviceId", out var pnp))
                            pd.PNPDeviceId = pnp.GetString() ?? "";
                        if (device.TryGetProperty("PNPDeviceID", out var pnp2))
                            pd.PNPDeviceId = pnp2.GetString() ?? "";
                            
                        if (device.TryGetProperty("configManagerErrorCode", out var err))
                            pd.ErrorCode = err.GetInt32();
                        if (device.TryGetProperty("errorCode", out var err2))
                            pd.ErrorCode = err2.GetInt32();
                            
                        if (device.TryGetProperty("status", out var status))
                            pd.Status = status.GetString() ?? "";
                            
                        if (device.TryGetProperty("driverProvider", out var dp))
                            pd.DriverProvider = dp.GetString();
                        if (device.TryGetProperty("driverVersion", out var dv))
                            pd.DriverVersion = dv.GetString();
                        
                        // Catégoriser
                        pd.Category = CategorizeDevice(pd.DeviceName, pd.PNPDeviceId);
                        pd.ErrorDescription = GetErrorDescription(pd.ErrorCode);
                        
                        allProblems.Add(pd);
                    }
                    
                    summary.ProblemDeviceCount = allProblems.Count;
                    
                    // Grouper par catégorie
                    summary.GroupedDevices = allProblems
                        .GroupBy(d => d.Category)
                        .ToDictionary(g => g.Key, g => g.ToList());
                    
                    // Top 10 (priorité: Critical > Important > Minor)
                    summary.Top10Problems = allProblems
                        .OrderBy(d => d.Severity == "Critical" ? 0 : d.Severity == "Important" ? 1 : 2)
                        .ThenBy(d => d.ErrorCode)
                        .Take(10)
                        .ToList();
                }
                
                // Compter total devices si disponible
                if (data.TryGetProperty("totalDevices", out var total))
                    summary.TotalDevices = total.GetInt32();
                else if (data.TryGetProperty("allDevices", out var all) && all.ValueKind == JsonValueKind.Array)
                    summary.TotalDevices = all.GetArrayLength();
            }
            catch (Exception ex)
            {
                App.LogMessage($"[DevicesDriversHelper] Erreur parsing: {ex.Message}");
            }
            
            return summary;
        }

        /// <summary>
        /// Catégorise un périphérique selon son nom et PNPDeviceID
        /// </summary>
        private static DeviceCategory CategorizeDevice(string name, string pnpId)
        {
            var combined = $"{name} {pnpId}".ToUpperInvariant();
            
            if (combined.Contains("DISPLAY") || combined.Contains("GPU") || combined.Contains("NVIDIA") || 
                combined.Contains("AMD RADEON") || combined.Contains("INTEL UHD") || combined.Contains("VIDEO"))
                return DeviceCategory.Display;
                
            if (combined.Contains("DISK") || combined.Contains("STORAGE") || combined.Contains("NVME") || 
                combined.Contains("SSD") || combined.Contains("HDD") || combined.Contains("SCSI"))
                return DeviceCategory.Storage;
                
            if (combined.Contains("ETHERNET") || combined.Contains("WIFI") || combined.Contains("NETWORK") || 
                combined.Contains("LAN") || combined.Contains("WIRELESS"))
                return DeviceCategory.Network;
                
            if (combined.Contains("USB") || combined.Contains("WD SES") || combined.Contains("MASS STORAGE"))
                return DeviceCategory.USB;
                
            if (combined.Contains("HID") || combined.Contains("KEYBOARD") || combined.Contains("MOUSE") || 
                combined.Contains("INPUT"))
                return DeviceCategory.HID;
                
            if (combined.Contains("AUDIO") || combined.Contains("SOUND") || combined.Contains("REALTEK") || 
                combined.Contains("SPEAKER"))
                return DeviceCategory.Audio;
                
            if (combined.Contains("VIRTUAL") || combined.Contains("ROOT\\") || combined.Contains("VMWARE") || 
                combined.Contains("HYPER-V"))
                return DeviceCategory.Virtual;
                
            if (combined.Contains("SYSTEM") || combined.Contains("ACPI") || combined.Contains("PCI\\VEN"))
                return DeviceCategory.System;
                
            return DeviceCategory.Unknown;
        }

        /// <summary>
        /// Description des codes d'erreur Windows Device Manager
        /// </summary>
        private static string GetErrorDescription(int errorCode)
        {
            return errorCode switch
            {
                0 => "OK",
                1 => "Device not configured correctly",
                3 => "Driver corrupted",
                10 => "Device cannot start",
                12 => "Not enough free resources",
                14 => "Restart required",
                18 => "Reinstall drivers",
                19 => "Registry problem",
                21 => "Windows is removing device",
                22 => "Device disabled",
                24 => "Device not present",
                28 => "Drivers not installed",
                29 => "Disabled in BIOS",
                31 => "Device not working properly",
                32 => "Driver disabled",
                33 => "Cannot determine required resources",
                34 => "Cannot determine required resources",
                35 => "BIOS does not supply enough resources",
                36 => "IRQ conflict",
                37 => "Driver does not support this device",
                38 => "Previous driver instance still in memory",
                39 => "Registry error",
                40 => "Registry entry error",
                41 => "Device successfully loaded but not found",
                42 => "Duplicate device",
                43 => "Driver stopped due to failure",
                44 => "Hardware stopped responding",
                45 => "Device not connected",
                46 => "Cannot access device, shutting down",
                47 => "Safe removal ready",
                48 => "Driver blocked by policy",
                49 => "Registry size limit exceeded",
                50 => "Device waiting for dependency",
                51 => "Windows verifying digital signature",
                52 => "Driver not signed",
                _ => $"Unknown error ({errorCode})"
            };
        }

        /// <summary>
        /// Génère une section TXT formatée pour les DevicesDrivers
        /// </summary>
        public static void WriteDevicesDriversSection(System.Text.StringBuilder sb, DevicesDriversSummary summary)
        {
            sb.AppendLine("  ┌─ RÉSUMÉ PÉRIPHÉRIQUES ───────────────────────────────────────────────────┐");
            sb.AppendLine($"  │  Total périphériques     : {summary.TotalDevices}");
            sb.AppendLine($"  │  Périphériques en erreur : {summary.ProblemDeviceCount}");
            sb.AppendLine($"  │    ├─ Critiques          : {summary.CriticalCount}");
            sb.AppendLine($"  │    ├─ Importants         : {summary.ImportantCount}");
            sb.AppendLine($"  │    └─ Mineurs            : {summary.MinorCount}");
            sb.AppendLine("  └─────────────────────────────────────────────────────────────────────────────┘");
            sb.AppendLine();
            
            if (summary.Top10Problems.Count > 0)
            {
                sb.AppendLine("  ┌─ TOP 10 PROBLÈMES ────────────────────────────────────────────────────────┐");
                foreach (var dev in summary.Top10Problems)
                {
                    var icon = dev.Severity == "Critical" ? "🔴" : dev.Severity == "Important" ? "🟠" : "🟡";
                    var shortName = dev.DeviceName.Length > 40 ? dev.DeviceName[..37] + "..." : dev.DeviceName;
                    sb.AppendLine($"  │  {icon} [{dev.Category}] {shortName}");
                    sb.AppendLine($"  │     Code {dev.ErrorCode}: {dev.ErrorDescription}");
                    if (!string.IsNullOrEmpty(dev.DriverProvider))
                        sb.AppendLine($"  │     Driver: {dev.DriverProvider} v{dev.DriverVersion}");
                }
                sb.AppendLine("  └─────────────────────────────────────────────────────────────────────────────┘");
                sb.AppendLine();
            }
            
            // Groupes par catégorie (si plusieurs)
            if (summary.GroupedDevices.Count > 1)
            {
                sb.AppendLine("  ┌─ PAR CATÉGORIE ───────────────────────────────────────────────────────────┐");
                foreach (var group in summary.GroupedDevices.OrderBy(g => g.Key.ToString()))
                {
                    sb.AppendLine($"  │  {group.Key}: {group.Value.Count} périphérique(s)");
                }
                sb.AppendLine("  └─────────────────────────────────────────────────────────────────────────────┘");
            }
        }
    }
}
