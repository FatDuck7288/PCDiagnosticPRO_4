using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Best-effort driver update status evaluator.
    /// Uses Windows Update Agent (COM) to find available driver updates.
    /// No third-party or proprietary catalog is used.
    /// </summary>
    public class DriverStatusEvaluator
    {
        public async Task<DriverUpdateEvaluationResult> EvaluateAsync(
            List<DriverInventoryItem> drivers,
            bool onlineSearch,
            CancellationToken ct = default)
        {
            var result = new DriverUpdateEvaluationResult();

            if (drivers == null || drivers.Count == 0)
            {
                return result;
            }

            try
            {
                var candidates = await Task.Run(() => QueryDriverUpdates(onlineSearch, ct), ct);
                result.UpdateCandidates = candidates;
                result.SearchMode = onlineSearch ? "Online" : "Offline";

                ApplyMatches(drivers, candidates);
            }
            catch (OperationCanceledException)
            {
                result.Error = "cancelled";
            }
            catch (Exception ex)
            {
                result.Error = $"exception: {ex.Message}";
                App.LogMessage($"[DriverStatusEvaluator] Error: {ex.Message}");
            }

            return result;
        }

        private static List<DriverUpdateCandidate> QueryDriverUpdates(bool onlineSearch, CancellationToken ct)
        {
            var candidates = new List<DriverUpdateCandidate>();

            // Windows Update Agent (COM) - legal OS API
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType == null)
            {
                throw new InvalidOperationException("Microsoft.Update.Session COM unavailable");
            }

            dynamic session = Activator.CreateInstance(sessionType);
            dynamic searcher = session.CreateUpdateSearcher();

            try { searcher.Online = onlineSearch; } catch { /* Not supported on some builds */ }

            // Driver updates only
            dynamic searchResult = searcher.Search("IsInstalled=0 and Type='Driver'");
            dynamic updates = searchResult.Updates;

            int count = updates.Count;
            for (int i = 0; i < count; i++)
            {
                if (ct.IsCancellationRequested) break;

                dynamic update = updates.Item(i);
                var candidate = new DriverUpdateCandidate
                {
                    Title = TryGetDynamicString(update, "Title") ?? "Driver Update"
                };

                candidate.DriverClass = TryGetDynamicString(update, "DriverClass");
                candidate.DriverModel = TryGetDynamicString(update, "DriverModel");
                candidate.DriverManufacturer = TryGetDynamicString(update, "DriverManufacturer");
                candidate.DriverVerVersion = TryGetDynamicString(update, "DriverVerVersion");
                candidate.DriverVerDate = TryGetDynamicDate(update, "DriverVerDate");
                candidate.DriverHardwareId = TryGetDynamicString(update, "DriverHardwareID");

                candidates.Add(candidate);
            }

            return candidates;
        }

        private static void ApplyMatches(List<DriverInventoryItem> drivers, List<DriverUpdateCandidate> candidates)
        {
            foreach (var driver in drivers)
            {
                var match = FindBestMatch(driver, candidates);
                if (match == null)
                {
                    driver.UpdateStatus = "Unknown";
                    continue;
                }

                driver.UpdateMatch = match.Value.matchInfo;
                driver.UpdateStatus = match.Value.status;
            }
        }

        private static (string status, DriverUpdateMatch matchInfo)? FindBestMatch(DriverInventoryItem driver, List<DriverUpdateCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0) return null;

            // 1) Hardware ID exact/contains match
            var hardwareMatch = candidates.FirstOrDefault(c => HardwareIdMatches(driver, c));
            if (hardwareMatch != null)
            {
                return EvaluateMatch(driver, hardwareMatch, "hardware_id");
            }

            // 2) Model/name + manufacturer match
            var nameMatch = candidates.FirstOrDefault(c => ModelMatches(driver, c) && ManufacturerMatches(driver, c));
            if (nameMatch != null)
            {
                return EvaluateMatch(driver, nameMatch, "model_manufacturer");
            }

            // 3) Class + manufacturer match
            var classMatch = candidates.FirstOrDefault(c => ClassMatches(driver, c) && ManufacturerMatches(driver, c));
            if (classMatch != null)
            {
                return EvaluateMatch(driver, classMatch, "class_manufacturer");
            }

            return null;
        }

        private static (string status, DriverUpdateMatch matchInfo) EvaluateMatch(
            DriverInventoryItem driver,
            DriverUpdateCandidate candidate,
            string reason)
        {
            var matchInfo = new DriverUpdateMatch
            {
                Title = candidate.Title,
                Version = candidate.DriverVerVersion,
                Date = candidate.DriverVerDate,
                MatchReason = reason
            };

            var installedVersion = TryParseVersion(driver.DriverVersion);
            var updateVersion = TryParseVersion(candidate.DriverVerVersion);
            if (installedVersion != null && updateVersion != null)
            {
                return updateVersion.CompareTo(installedVersion) > 0
                    ? ("Outdated", matchInfo)
                    : ("UpToDate", matchInfo);
            }

            var installedDate = TryParseDate(driver.DriverDate);
            var updateDate = TryParseDate(candidate.DriverVerDate);
            if (installedDate.HasValue && updateDate.HasValue)
            {
                return updateDate.Value > installedDate.Value
                    ? ("Outdated", matchInfo)
                    : ("UpToDate", matchInfo);
            }

            return ("Unknown", matchInfo);
        }

        private static bool HardwareIdMatches(DriverInventoryItem driver, DriverUpdateCandidate candidate)
        {
            if (string.IsNullOrEmpty(candidate.DriverHardwareId)) return false;
            if (driver.HardwareIds == null || driver.HardwareIds.Count == 0) return false;

            foreach (var id in driver.HardwareIds)
            {
                if (candidate.DriverHardwareId.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf(candidate.DriverHardwareId, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ModelMatches(DriverInventoryItem driver, DriverUpdateCandidate candidate)
        {
            if (string.IsNullOrEmpty(candidate.DriverModel) || string.IsNullOrEmpty(driver.DeviceName))
                return false;

            return candidate.DriverModel.IndexOf(driver.DeviceName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   driver.DeviceName.IndexOf(candidate.DriverModel, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ManufacturerMatches(DriverInventoryItem driver, DriverUpdateCandidate candidate)
        {
            if (string.IsNullOrEmpty(candidate.DriverManufacturer)) return false;
            var vendor = driver.Provider ?? driver.Manufacturer ?? "";
            if (string.IsNullOrEmpty(vendor)) return false;

            return candidate.DriverManufacturer.IndexOf(vendor, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   vendor.IndexOf(candidate.DriverManufacturer, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ClassMatches(DriverInventoryItem driver, DriverUpdateCandidate candidate)
        {
            if (string.IsNullOrEmpty(candidate.DriverClass) || string.IsNullOrEmpty(driver.DeviceClass))
                return false;

            return string.Equals(candidate.DriverClass, driver.DeviceClass, StringComparison.OrdinalIgnoreCase);
        }

        private static Version? TryParseVersion(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var cleaned = value.Trim();
            // Some versions include extra suffix, keep numeric parts only
            var parts = cleaned.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (Version.TryParse(part, out var v))
                    return v;
            }

            return null;
        }

        private static DateTime? TryParseDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
                return dt;

            return null;
        }

        private static string? TryGetDynamicString(dynamic obj, string propertyName)
        {
            try
            {
                var value = obj.GetType().InvokeMember(propertyName, System.Reflection.BindingFlags.GetProperty, null, obj, Array.Empty<object>());
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string? TryGetDynamicDate(dynamic obj, string propertyName)
        {
            try
            {
                var value = obj.GetType().InvokeMember(propertyName, System.Reflection.BindingFlags.GetProperty, null, obj, Array.Empty<object>());
                if (value is DateTime dt) return dt.ToString("yyyy-MM-dd");
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }

    public class DriverUpdateEvaluationResult
    {
        public List<DriverUpdateCandidate> UpdateCandidates { get; set; } = new();
        public string? SearchMode { get; set; }
        public string? Error { get; set; }
    }
}
