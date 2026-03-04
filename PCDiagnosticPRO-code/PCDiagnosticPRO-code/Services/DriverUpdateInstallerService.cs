using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Installs driver updates through the official Windows Update Agent (WUA) COM API.
    /// Source database: Windows Update Catalog exposed by Windows Update service.
    /// </summary>
    public sealed class DriverUpdateInstallerService
    {
        private const string DriverSearchCriteria = "IsInstalled=0 and Type='Driver'";

        public async Task<DriverUpdateInstallResult> InstallSelectedDriverUpdatesAsync(
            IReadOnlyList<DriverInventoryItem> selectedDrivers,
            bool onlineSearch = true,
            CancellationToken cancellationToken = default,
            IProgress<DriverUpdateProgressEvent>? progress = null)
        {
            if (selectedDrivers == null || selectedDrivers.Count == 0)
            {
                return new DriverUpdateInstallResult
                {
                    Success = false,
                    Message = "Aucun pilote selectionne pour la mise a jour."
                };
            }

            if (!AdminHelper.IsRunningAsAdmin())
            {
                progress?.Report(new DriverUpdateProgressEvent
                {
                    EventType = DriverUpdateProgressEventType.Info,
                    Message = "Les mises a jour pilotes necessitent des droits administrateur."
                });

                return new DriverUpdateInstallResult
                {
                    Success = false,
                    RequiresElevation = true,
                    SelectedCount = selectedDrivers.Count,
                    Message = "Les mises a jour pilotes necessitent des droits administrateur.",
                    ItemResults = selectedDrivers.Select(CreateSkippedResult).ToList()
                };
            }

            try
            {
                progress?.Report(new DriverUpdateProgressEvent
                {
                    EventType = DriverUpdateProgressEventType.Info,
                    Message = "Recherche des mises a jour pilotes via Windows Update..."
                });

                var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                if (sessionType == null)
                {
                    return new DriverUpdateInstallResult
                    {
                        Success = false,
                        SelectedCount = selectedDrivers.Count,
                        Message = "Windows Update Agent (Microsoft.Update.Session) est indisponible.",
                        ItemResults = selectedDrivers.Select(CreateSkippedResult).ToList()
                    };
                }

                dynamic session = Activator.CreateInstance(sessionType)
                    ?? throw new InvalidOperationException("Impossible de creer Microsoft.Update.Session.");
                dynamic searcher = session.CreateUpdateSearcher();
                TrySetDynamicProperty(searcher, "Online", onlineSearch);

                dynamic searchResult = await Task.Run(
                    () => searcher.Search(DriverSearchCriteria),
                    cancellationToken).ConfigureAwait(false);
                dynamic updates = searchResult.Updates;

                var matchingTargets = FindMatchingUpdates((object)updates, selectedDrivers);
                var matchedDriverKeys = new HashSet<string>(
                    matchingTargets.Select(t => BuildDriverKey(t.Driver)),
                    StringComparer.OrdinalIgnoreCase);

                var skippedResults = new List<DriverUpdateItemResult>();
                foreach (var driver in selectedDrivers.Where(d => !matchedDriverKeys.Contains(BuildDriverKey(d))))
                {
                    var skipped = CreateSkippedResult(driver);
                    skippedResults.Add(skipped);
                    progress?.Report(new DriverUpdateProgressEvent
                    {
                        EventType = DriverUpdateProgressEventType.ItemSkipped,
                        ItemId = skipped.ItemId,
                        DisplayName = skipped.DisplayName,
                        Percent = 100,
                        Message = skipped.Message
                    });
                }

                if (matchingTargets.Count == 0)
                {
                    return new DriverUpdateInstallResult
                    {
                        Success = false,
                        SelectedCount = selectedDrivers.Count,
                        MatchedCount = 0,
                        Message = "Aucune mise a jour Windows Update n'a ete trouvee pour les pilotes coches.",
                        ItemResults = skippedResults
                    };
                }

                dynamic updateCollection = CreateUpdateCollection();
                foreach (var target in matchingTargets)
                {
                    TryAcceptEula(target.UpdateObject);
                    updateCollection.Add(target.UpdateObject);

                    progress?.Report(new DriverUpdateProgressEvent
                    {
                        EventType = DriverUpdateProgressEventType.ItemStarted,
                        ItemId = BuildDriverKey(target.Driver),
                        DisplayName = target.Driver.DeviceName ?? target.Title,
                        Percent = 50,
                        Message = $"Installation en cours: {target.Title}"
                    });
                }

                progress?.Report(new DriverUpdateProgressEvent
                {
                    EventType = DriverUpdateProgressEventType.Info,
                    Message = "Telechargement des mises a jour pilotes..."
                });

                dynamic downloader = session.CreateUpdateDownloader();
                downloader.Updates = updateCollection;
                dynamic downloadResult = await Task.Run(
                    () => downloader.Download(),
                    cancellationToken).ConfigureAwait(false);
                var downloadCode = SafeInt(TryGetDynamicProperty(downloadResult, "ResultCode"));

                progress?.Report(new DriverUpdateProgressEvent
                {
                    EventType = DriverUpdateProgressEventType.Info,
                    Message = "Installation des pilotes..."
                });

                dynamic installer = session.CreateUpdateInstaller();
                installer.Updates = updateCollection;
                TrySetDynamicProperty(installer, "ForceQuiet", true);
                dynamic installResult = await Task.Run(
                    () => installer.Install(),
                    cancellationToken).ConfigureAwait(false);

                var rebootRequired = SafeBool(TryGetDynamicProperty(installResult, "RebootRequired"));
                int installedCount = 0;
                int failedCount = 0;
                var perItemResults = new List<DriverUpdateItemResult>(skippedResults);

                for (int i = 0; i < matchingTargets.Count; i++)
                {
                    var target = matchingTargets[i];
                    var perUpdateResult = TryInvokeDynamicMethod(installResult, "GetUpdateResult", i);
                    var resultCode = SafeInt(TryGetDynamicProperty(perUpdateResult, "ResultCode"));
                    var driverKey = BuildDriverKey(target.Driver);
                    var displayName = target.Driver.DeviceName ?? target.Title;

                    if (resultCode == 2 || resultCode == 3)
                    {
                        installedCount++;
                        perItemResults.Add(new DriverUpdateItemResult
                        {
                            ItemId = driverKey,
                            DisplayName = displayName,
                            State = DriverUpdateItemState.Success,
                            Message = "Mise a jour pilote installee."
                        });
                        progress?.Report(new DriverUpdateProgressEvent
                        {
                            EventType = DriverUpdateProgressEventType.ItemSucceeded,
                            ItemId = driverKey,
                            DisplayName = displayName,
                            Percent = 100,
                            Message = "Mise a jour pilote installee."
                        });
                    }
                    else
                    {
                        failedCount++;
                        var failureMessage = $"Installation en echec (code resultat {resultCode}).";
                        perItemResults.Add(new DriverUpdateItemResult
                        {
                            ItemId = driverKey,
                            DisplayName = displayName,
                            State = DriverUpdateItemState.Failed,
                            Message = failureMessage
                        });
                        progress?.Report(new DriverUpdateProgressEvent
                        {
                            EventType = DriverUpdateProgressEventType.ItemFailed,
                            ItemId = driverKey,
                            DisplayName = displayName,
                            Percent = 100,
                            Message = failureMessage
                        });
                    }
                }

                var success = installedCount > 0;
                var summary = success
                    ? $"Mise a jour terminee: {installedCount}/{matchingTargets.Count} pilote(s) installe(s)."
                    : "Aucun pilote n'a pu etre installe.";

                if (failedCount > 0)
                    summary += $" Echecs: {failedCount}.";

                if (rebootRequired)
                    summary += " Redemarrage requis.";

                App.LogMessage($"[DriverUpdateInstaller] Selected={selectedDrivers.Count}, Matched={matchingTargets.Count}, Installed={installedCount}, Failed={failedCount}, DownloadCode={downloadCode}, Reboot={rebootRequired}");

                return new DriverUpdateInstallResult
                {
                    Success = success,
                    SelectedCount = selectedDrivers.Count,
                    MatchedCount = matchingTargets.Count,
                    InstalledCount = installedCount,
                    FailedCount = failedCount,
                    RebootRequired = rebootRequired,
                    DownloadResultCode = downloadCode,
                    Message = summary,
                    ItemResults = perItemResults
                };
            }
            catch (OperationCanceledException)
            {
                progress?.Report(new DriverUpdateProgressEvent
                {
                    EventType = DriverUpdateProgressEventType.Info,
                    Message = "Mise a jour des pilotes annulee."
                });

                return new DriverUpdateInstallResult
                {
                    Success = false,
                    SelectedCount = selectedDrivers.Count,
                    Message = "Mise a jour des pilotes annulee.",
                    ItemResults = selectedDrivers.Select(CreateSkippedResult).ToList()
                };
            }
            catch (Exception ex)
            {
                var sanitized = SanitizeReason(ex.Message);
                App.LogMessage($"[DriverUpdateInstaller] Error: {ex.Message}");
                progress?.Report(new DriverUpdateProgressEvent
                {
                    EventType = DriverUpdateProgressEventType.Info,
                    Message = sanitized
                });

                return new DriverUpdateInstallResult
                {
                    Success = false,
                    SelectedCount = selectedDrivers.Count,
                    Message = sanitized,
                    ItemResults = selectedDrivers.Select(CreateSkippedResult).ToList()
                };
            }
        }

        private static List<DriverUpdateInstallTarget> FindMatchingUpdates(object updatesCollection, IReadOnlyList<DriverInventoryItem> selectedDrivers)
        {
            var results = new List<DriverUpdateInstallTarget>();
            var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenDrivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = SafeInt(TryGetDynamicProperty(updatesCollection, "Count"));

            for (int i = 0; i < count; i++)
            {
                var update = TryInvokeDynamicMethod(updatesCollection, "Item", i);
                if (update == null)
                    continue;

                var matchedDriver = FindMatchingDriver(update, selectedDrivers);
                if (matchedDriver == null)
                    continue;

                var title = TryGetDynamicString(update, "Title") ?? $"Driver Update #{i + 1}";
                var driverKey = BuildDriverKey(matchedDriver);

                if (!seenTitles.Add(title))
                    continue;

                if (!seenDrivers.Add(driverKey))
                    continue;

                results.Add(new DriverUpdateInstallTarget(update, matchedDriver, title));
            }

            return results;
        }

        private static DriverInventoryItem? FindMatchingDriver(object update, IReadOnlyList<DriverInventoryItem> selectedDrivers)
        {
            var title = TryGetDynamicString(update, "Title");
            var updateHardwareId = TryGetDynamicString(update, "DriverHardwareID");
            var updateClass = TryGetDynamicString(update, "DriverClass");
            var updateManufacturer = TryGetDynamicString(update, "DriverManufacturer");
            var updateModel = TryGetDynamicString(update, "DriverModel");

            foreach (var driver in selectedDrivers)
            {
                if (!string.IsNullOrWhiteSpace(driver.UpdateMatch?.Title) &&
                    string.Equals(driver.UpdateMatch.Title, title, StringComparison.OrdinalIgnoreCase))
                {
                    return driver;
                }

                if (HardwareIdMatches(driver, updateHardwareId))
                    return driver;

                if (IdentityMatches(driver, updateClass, updateManufacturer, updateModel))
                    return driver;
            }

            return null;
        }

        private static bool HardwareIdMatches(DriverInventoryItem driver, string? updateHardwareId)
        {
            if (string.IsNullOrWhiteSpace(updateHardwareId))
                return false;

            var candidateId = NormalizeHardwareId(updateHardwareId);
            if (string.IsNullOrWhiteSpace(candidateId))
                return false;

            var candidateVendorDevice = TryExtractVendorDeviceKey(candidateId);
            var identities = BuildDriverHardwareIdentitySet(driver);
            if (identities.Count == 0)
                return false;

            foreach (var id in identities)
            {
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var localId = NormalizeHardwareId(id);
                if (string.IsNullOrWhiteSpace(localId))
                    continue;

                if (candidateId.IndexOf(localId, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    localId.IndexOf(candidateId, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                var localVendorDevice = TryExtractVendorDeviceKey(localId);
                if (!string.IsNullOrWhiteSpace(localVendorDevice) &&
                    string.Equals(localVendorDevice, candidateVendorDevice, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IdentityMatches(
            DriverInventoryItem driver,
            string? updateClass,
            string? updateManufacturer,
            string? updateModel)
        {
            if (string.IsNullOrWhiteSpace(updateModel))
                return false;

            if (!string.IsNullOrWhiteSpace(updateClass) &&
                !string.Equals(updateClass, driver.DeviceClass, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var manufacturerMatches =
                StringApproxMatches(updateManufacturer, driver.Provider) ||
                StringApproxMatches(updateManufacturer, driver.Manufacturer);

            if (!string.IsNullOrWhiteSpace(updateManufacturer) && !manufacturerMatches)
                return false;

            return StringApproxMatches(updateModel, driver.DeviceName);
        }

        private static List<string> BuildDriverHardwareIdentitySet(DriverInventoryItem driver)
        {
            var identities = new List<string>();
            if (driver.HardwareIds != null)
                identities.AddRange(driver.HardwareIds.Where(id => !string.IsNullOrWhiteSpace(id)));

            if (!string.IsNullOrWhiteSpace(driver.PnpDeviceId))
                identities.Add(driver.PnpDeviceId);

            return identities;
        }

        private static string NormalizeHardwareId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Trim().ToUpperInvariant();
            var firstSlash = normalized.IndexOf('\\');
            if (firstSlash < 0)
                return normalized;

            var bus = normalized.Substring(0, firstSlash);
            var remainder = normalized.Substring(firstSlash + 1);
            var secondSlash = remainder.IndexOf('\\');
            if (secondSlash >= 0)
                remainder = remainder.Substring(0, secondSlash);

            var tokens = remainder
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(t =>
                    t.StartsWith("VEN_", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("VID_", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("DEV_", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("PID_", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (tokens.Length == 0)
                return normalized;

            return $"{bus}\\{string.Join("&", tokens)}";
        }

        private static string? TryExtractVendorDeviceKey(string? normalizedHardwareId)
        {
            if (string.IsNullOrWhiteSpace(normalizedHardwareId))
                return null;

            string? vendor = null;
            string? device = null;
            var parts = normalizedHardwareId.ToUpperInvariant()
                .Split(new[] { '\\', '&', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                if (vendor == null &&
                    (part.StartsWith("VEN_", StringComparison.Ordinal) || part.StartsWith("VID_", StringComparison.Ordinal)))
                {
                    vendor = part;
                    continue;
                }

                if (device == null &&
                    (part.StartsWith("DEV_", StringComparison.Ordinal) || part.StartsWith("PID_", StringComparison.Ordinal)))
                {
                    device = part;
                }

                if (vendor != null && device != null)
                    break;
            }

            return vendor != null && device != null ? $"{vendor}|{device}" : null;
        }

        private static bool StringApproxMatches(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            var leftNorm = NormalizeText(left);
            var rightNorm = NormalizeText(right);
            if (leftNorm.Length == 0 || rightNorm.Length == 0)
                return false;

            if (leftNorm.IndexOf(rightNorm, StringComparison.OrdinalIgnoreCase) >= 0 ||
                rightNorm.IndexOf(leftNorm, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            var leftTokens = leftNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 4);
            var rightTokenSet = new HashSet<string>(
                rightNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 4),
                StringComparer.OrdinalIgnoreCase);

            return leftTokens.Any(t => rightTokenSet.Contains(t));
        }

        private static string NormalizeText(string value)
        {
            var chars = value.ToUpperInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray();
            return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        private static string BuildDriverKey(DriverInventoryItem driver)
        {
            if (!string.IsNullOrWhiteSpace(driver.PnpDeviceId))
                return driver.PnpDeviceId.Trim();
            if (!string.IsNullOrWhiteSpace(driver.DeviceName))
                return driver.DeviceName.Trim();
            if (!string.IsNullOrWhiteSpace(driver.InfName))
                return driver.InfName.Trim();
            if (!string.IsNullOrWhiteSpace(driver.UpdateMatch?.Title))
                return driver.UpdateMatch!.Title.Trim();

            return $"driver-{driver.GetHashCode():X}";
        }

        private static DriverUpdateItemResult CreateSkippedResult(DriverInventoryItem driver)
        {
            return new DriverUpdateItemResult
            {
                ItemId = BuildDriverKey(driver),
                DisplayName = string.IsNullOrWhiteSpace(driver.DeviceName) ? "Pilote" : driver.DeviceName,
                State = DriverUpdateItemState.Skipped,
                Message = "Aucune mise a jour Windows Update correspondante." 
            };
        }

        private static dynamic CreateUpdateCollection()
        {
            var collectionType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl");
            if (collectionType == null)
                throw new InvalidOperationException("Microsoft.Update.UpdateColl COM unavailable.");

            return Activator.CreateInstance(collectionType)
                ?? throw new InvalidOperationException("Impossible de creer Microsoft.Update.UpdateColl.");
        }

        private static void TryAcceptEula(object update)
        {
            try
            {
                var accepted = SafeBool(TryGetDynamicProperty(update, "EulaAccepted"));
                if (!accepted)
                    TryInvokeDynamicMethod(update, "AcceptEula");
            }
            catch
            {
                // Ignore non-fatal EULA reflection issues.
            }
        }

        private static string? TryGetDynamicString(object? obj, string propertyName)
        {
            var value = TryGetDynamicProperty(obj, propertyName);
            return value?.ToString();
        }

        private static object? TryGetDynamicProperty(object? obj, string propertyName)
        {
            if (obj == null)
                return null;

            try
            {
                return obj.GetType().InvokeMember(
                    propertyName,
                    BindingFlags.GetProperty,
                    binder: null,
                    target: obj,
                    args: Array.Empty<object>());
            }
            catch
            {
                return null;
            }
        }

        private static object? TryInvokeDynamicMethod(object? obj, string methodName, params object[] args)
        {
            if (obj == null)
                return null;

            try
            {
                return obj.GetType().InvokeMember(
                    methodName,
                    BindingFlags.InvokeMethod,
                    binder: null,
                    target: obj,
                    args: args);
            }
            catch
            {
                return null;
            }
        }

        private static void TrySetDynamicProperty(object? obj, string propertyName, object value)
        {
            if (obj == null)
                return;

            try
            {
                obj.GetType().InvokeMember(
                    propertyName,
                    BindingFlags.SetProperty,
                    binder: null,
                    target: obj,
                    args: new[] { value });
            }
            catch
            {
                // Ignore optional property set errors.
            }
        }

        private static int SafeInt(object? value)
        {
            if (value == null)
                return 0;

            if (value is int intValue)
                return intValue;

            return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
        }

        private static bool SafeBool(object? value)
        {
            if (value == null)
                return false;

            if (value is bool boolValue)
                return boolValue;

            return bool.TryParse(value.ToString(), out var parsed) && parsed;
        }

        private static string SanitizeReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return "Erreur non detaillee.";

            var firstLine = reason.Replace("\r", string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim() ?? reason.Trim();

            return firstLine.Length <= 280 ? firstLine : firstLine.Substring(0, 280) + "...";
        }

        private sealed class DriverUpdateInstallTarget
        {
            public DriverUpdateInstallTarget(object updateObject, DriverInventoryItem driver, string title)
            {
                UpdateObject = updateObject;
                Driver = driver;
                Title = title;
            }

            public object UpdateObject { get; }
            public DriverInventoryItem Driver { get; }
            public string Title { get; }
        }
    }

    public sealed class DriverUpdateInstallResult
    {
        public bool Success { get; set; }
        public bool RequiresElevation { get; set; }
        public bool RebootRequired { get; set; }
        public int SelectedCount { get; set; }
        public int MatchedCount { get; set; }
        public int InstalledCount { get; set; }
        public int FailedCount { get; set; }
        public int DownloadResultCode { get; set; }
        public string SourceDatabase { get; set; } = "Windows Update Catalog (Windows Update Agent)";
        public string Message { get; set; } = string.Empty;
        public List<DriverUpdateItemResult> ItemResults { get; set; } = new();
    }

    public enum DriverUpdateItemState
    {
        Queued,
        Running,
        Success,
        Failed,
        Skipped
    }

    public sealed class DriverUpdateItemResult
    {
        public string ItemId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public DriverUpdateItemState State { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public enum DriverUpdateProgressEventType
    {
        Info,
        ItemStarted,
        ItemSucceeded,
        ItemFailed,
        ItemSkipped,
        Progress
    }

    public sealed class DriverUpdateProgressEvent
    {
        public DriverUpdateProgressEventType EventType { get; set; }
        public string ItemId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int? Percent { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
