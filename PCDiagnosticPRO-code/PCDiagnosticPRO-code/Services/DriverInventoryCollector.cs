using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Collects driver inventory using Windows WMI APIs.
    /// Sources:
    /// - Win32_PnPSignedDriver (driver metadata)
    /// - Win32_PnPEntity (hardware IDs, status)
    /// No third-party code or proprietary databases are used.
    /// </summary>
    public class DriverInventoryCollector
    {
        private static readonly string[] PriorityClasses =
        {
            "DISPLAY", "MEDIA", "NET", "HDC", "SCSIADAPTER", "USB", "BLUETOOTH", "CHIPSET", "SYSTEM", "PROCESSOR"
        };

        public async Task<DriverInventoryResult> CollectAsync(
            CancellationToken ct = default,
            bool includeUpdateLookup = true,
            bool onlineUpdateSearch = false)
        {
            var result = new DriverInventoryResult
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                Available = true,
                Source = "DriverInventoryCollector"
            };

            var sw = Stopwatch.StartNew();

            try
            {
                // Build PnP index first (hardware IDs + status)
                var pnpIndex = await Task.Run(() => BuildPnpIndex(ct), ct);

                // Collect driver metadata
                var drivers = await Task.Run(() => CollectDrivers(pnpIndex, ct), ct);
                result.Drivers = drivers;
                result.TotalCount = drivers.Count;
                result.SignedCount = drivers.Count(d => d.IsSigned == true);
                result.UnsignedCount = drivers.Count(d => d.IsSigned == false);
                result.ProblemCount = drivers.Count(d => !string.IsNullOrEmpty(d.Status) && !d.Status.Equals("OK", StringComparison.OrdinalIgnoreCase));
                result.ByClass = drivers
                    .GroupBy(d => string.IsNullOrEmpty(d.DeviceClass) ? "UNKNOWN" : d.DeviceClass)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

                // Optional: Evaluate update status via Windows Update Agent
                if (includeUpdateLookup && drivers.Count > 0)
                {
                    var evaluator = new DriverStatusEvaluator();
                    var evalResult = await evaluator.EvaluateAsync(drivers, onlineUpdateSearch, ct);
                    result.UpdateCandidates = evalResult.UpdateCandidates;
                    result.UpdateSearchMode = evalResult.SearchMode;
                    result.UpdateSearchError = evalResult.Error;
                }
            }
            catch (OperationCanceledException)
            {
                result.Available = false;
                result.Reason = "cancelled";
            }
            catch (Exception ex)
            {
                result.Available = false;
                result.Reason = $"exception: {ex.Message}";
                App.LogMessage($"[DriverInventory] Error: {ex.Message}");
            }

            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            App.LogMessage($"[DriverInventory] Collected {result.TotalCount} drivers in {result.DurationMs}ms");

            return result;
        }

        private static Dictionary<string, (List<string> hardwareIds, string? status)> BuildPnpIndex(CancellationToken ct)
        {
            var map = new Dictionary<string, (List<string>, string?)>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "root\\cimv2",
                    "SELECT PNPDeviceID, HardwareID, Status FROM Win32_PnPEntity");
                foreach (var obj in searcher.Get().OfType<ManagementObject>())
                {
                    if (ct.IsCancellationRequested) break;

                    var pnpId = obj["PNPDeviceID"]?.ToString();
                    if (string.IsNullOrEmpty(pnpId)) continue;

                    var status = obj["Status"]?.ToString();
                    var hwIds = new List<string>();
                    if (obj["HardwareID"] is string[] arr)
                    {
                        hwIds.AddRange(arr.Where(s => !string.IsNullOrWhiteSpace(s)));
                    }

                    map[pnpId] = (hwIds, status);
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[DriverInventory] BuildPnpIndex failed: {ex.Message}");
            }

            return map;
        }

        private static List<DriverInventoryItem> CollectDrivers(
            Dictionary<string, (List<string> hardwareIds, string? status)> pnpIndex,
            CancellationToken ct)
        {
            var drivers = new List<DriverInventoryItem>();

            using var searcher = new ManagementObjectSearcher(
                "root\\cimv2",
                "SELECT DeviceClass, DeviceName, DriverVersion, DriverDate, DriverProviderName, InfName, IsSigned, Manufacturer, FriendlyName, DeviceID FROM Win32_PnPSignedDriver WHERE DeviceClass IS NOT NULL");

            foreach (var obj in searcher.Get().OfType<ManagementObject>())
            {
                if (ct.IsCancellationRequested) break;

                var deviceClass = obj["DeviceClass"]?.ToString() ?? "";
                var deviceName = obj["DeviceName"]?.ToString() ?? obj["FriendlyName"]?.ToString() ?? "";
                var provider = obj["DriverProviderName"]?.ToString();
                var manufacturer = obj["Manufacturer"]?.ToString();
                var driverVersion = obj["DriverVersion"]?.ToString();
                var driverDateRaw = obj["DriverDate"]?.ToString();
                var infName = obj["InfName"]?.ToString();
                var pnpDeviceId = obj["DeviceID"]?.ToString();

                bool? isSigned = null;
                if (obj["IsSigned"] != null)
                {
                    if (bool.TryParse(obj["IsSigned"].ToString(), out var b)) isSigned = b;
                }

                var item = new DriverInventoryItem
                {
                    DeviceClass = deviceClass,
                    DeviceName = deviceName,
                    Provider = provider,
                    Manufacturer = manufacturer,
                    DriverVersion = driverVersion,
                    DriverDate = ParseWmiDate(driverDateRaw),
                    InfName = infName,
                    PnpDeviceId = pnpDeviceId,
                    IsSigned = isSigned
                };

                if (!string.IsNullOrEmpty(pnpDeviceId) && pnpIndex.TryGetValue(pnpDeviceId, out var pnp))
                {
                    item.HardwareIds = pnp.hardwareIds;
                    item.Status = pnp.status;
                }

                drivers.Add(item);
            }

            // Sort by priority class then by device name
            return drivers
                .OrderBy(d => GetPriorityIndex(d.DeviceClass))
                .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int GetPriorityIndex(string? cls)
        {
            if (string.IsNullOrEmpty(cls)) return PriorityClasses.Length + 1;
            for (int i = 0; i < PriorityClasses.Length; i++)
            {
                if (string.Equals(PriorityClasses[i], cls, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return PriorityClasses.Length + 1;
        }

        private static string ParseWmiDate(string? wmiDate)
        {
            if (string.IsNullOrEmpty(wmiDate) || wmiDate.Length < 8) return "";
            try
            {
                var y = wmiDate.Substring(0, 4);
                var m = wmiDate.Substring(4, 2);
                var d = wmiDate.Substring(6, 2);
                return $"{y}-{m}-{d}";
            }
            catch
            {
                return wmiDate ?? "";
            }
        }
    }
}
