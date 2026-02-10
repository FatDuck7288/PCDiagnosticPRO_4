using System;
using System.Collections.Generic;
using System.Management;
using System.Threading.Tasks;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Collects SMART attributes via WMI (MSStorageDriver_FailurePredictStatus + FailurePredictData).
    /// No kernel driver; user-mode only.
    /// </summary>
    public static class SmartAttributesCollector
    {
        private static readonly Dictionary<int, string> KnownAttributeNames = new()
        {
            { 5, "Reallocated Sectors Count" },
            { 9, "Power-On Hours" },
            { 12, "Power Cycle Count" },
            { 184, "End-to-End Error" },
            { 187, "Reported Uncorrectable" },
            { 188, "Command Timeout" },
            { 189, "High Fly Writes" },
            { 190, "Temperature Difference" },
            { 194, "Temperature" },
            { 196, "Reallocated Event Count" },
            { 197, "Current Pending Sector" },
            { 198, "Uncorrectable Sector Count" },
            { 231, "SSD Wear Leveling / Life Left" },
            { 233, "Media Wear Indicator" }
        };

        public static async Task<List<SmartDiskEntry>?> CollectAsync(System.Threading.CancellationToken ct = default)
        {
            var results = new List<SmartDiskEntry>();
            await Task.Run(() =>
            {
                try
                {
                    var statusByInstance = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus"))
                    {
                        foreach (ManagementObject mo in searcher.Get())
                        {
                            try
                            {
                                var name = mo["InstanceName"]?.ToString() ?? "";
                                var predict = mo["PredictFailure"] is bool b && b;
                                if (!string.IsNullOrEmpty(name))
                                    statusByInstance[name] = predict;
                            }
                            catch { /* skip */ }
                        }
                    }

                    using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData"))
                    {
                        foreach (ManagementObject mo in searcher.Get())
                        {
                            if (ct.IsCancellationRequested) break;
                            try
                            {
                                var instanceName = mo["InstanceName"]?.ToString() ?? "";
                                var vendorSpecific = mo["VendorSpecific"] as byte[];
                                if (vendorSpecific == null || vendorSpecific.Length < 362) continue;

                                var entry = new SmartDiskEntry
                                {
                                    InstanceName = instanceName,
                                    PredictFailure = statusByInstance.TryGetValue(instanceName, out var pred) && pred
                                };

                                for (int i = 2; i + 12 <= vendorSpecific.Length; i += 12)
                                {
                                    int id = vendorSpecific[i];
                                    if (id == 0) continue;
                                    int current = vendorSpecific[i + 2];
                                    int worst = vendorSpecific[i + 3];
                                    ulong raw = id <= 0xFF && i + 8 <= vendorSpecific.Length
                                        ? BitConverter.ToUInt32(vendorSpecific, i + 4)
                                        : 0;
                                    int threshold = i + 10 <= vendorSpecific.Length
                                        ? BitConverter.ToUInt16(vendorSpecific, i + 8)
                                        : 0;

                                    entry.Attributes.Add(new SmartAttributeEntry
                                    {
                                        Id = id,
                                        Name = KnownAttributeNames.TryGetValue(id, out var n) ? n : $"Attribute_{id}",
                                        Current = current,
                                        Worst = worst,
                                        Raw = raw,
                                        Threshold = threshold
                                    });
                                }

                                if (entry.Attributes.Count > 0)
                                    results.Add(entry);
                            }
                            catch (Exception ex)
                            {
                                App.LogMessage($"[SmartAttributes] Parse error: {ex.Message}");
                            }
                        }
                    }

                    if (results.Count > 0)
                        App.LogMessage($"[SmartAttributes] Collected {results.Count} disk(s)");
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[SmartAttributes] WMI error: {ex.Message}");
                }
            }, ct).ConfigureAwait(false);

            return results.Count > 0 ? results : null;
        }
    }
}
