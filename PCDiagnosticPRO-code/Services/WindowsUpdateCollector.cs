using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Collects Windows Update availability via Windows Update Agent (COM).
    /// Legal OS API; no scraping or proprietary sources.
    /// </summary>
    public class WindowsUpdateCollector
    {
        public async Task<WindowsUpdateResult> CollectAsync(
            CancellationToken ct = default,
            bool onlineSearch = false)
        {
            var result = new WindowsUpdateResult
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                Available = true,
                Source = "WindowsUpdateCollector",
                SearchMode = onlineSearch ? "Online" : "Offline"
            };

            var sw = Stopwatch.StartNew();

            try
            {
                var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                if (sessionType == null)
                    throw new InvalidOperationException("Microsoft.Update.Session COM unavailable");

                dynamic session = Activator.CreateInstance(sessionType);
                dynamic searcher = session.CreateUpdateSearcher();
                try { searcher.Online = onlineSearch; } catch { /* Not supported on some builds */ }

                dynamic searchResult = await Task.Run(() => searcher.Search("IsInstalled=0 and Type='Software'"), ct);
                dynamic updates = searchResult.Updates;

                int count = updates.Count;
                result.PendingCount = count;
                result.SearchMode = onlineSearch ? "Online" : "Offline";

                var items = new List<WindowsUpdateItem>();
                for (int i = 0; i < Math.Min(count, 10); i++)
                {
                    if (ct.IsCancellationRequested) break;
                    dynamic update = updates.Item(i);

                    var item = new WindowsUpdateItem
                    {
                        Title = TryGetDynamicString(update, "Title") ?? "Update"
                    };

                    var kbList = TryGetDynamicStringArray(update, "KBArticleIDs");
                    if (kbList?.Length > 0) item.KB = kbList[0];

                    items.Add(item);
                }

                if (items.Count > 0)
                    result.Updates = items;

                result.RebootRequired = CheckRebootRequired();
            }
            catch (OperationCanceledException)
            {
                result.Available = false;
                result.Error = "cancelled";
            }
            catch (Exception ex)
            {
                result.Available = false;
                result.Error = $"exception: {ex.Message}";
                App.LogMessage($"[WindowsUpdateCollector] Error: {ex.Message}");
            }

            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            App.LogMessage($"[WindowsUpdateCollector] Pending={result.PendingCount}, Available={result.Available}");
            return result;
        }

        private static bool? CheckRebootRequired()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
                return key != null;
            }
            catch
            {
                return null;
            }
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

        private static string[]? TryGetDynamicStringArray(dynamic obj, string propertyName)
        {
            try
            {
                var value = obj.GetType().InvokeMember(propertyName, System.Reflection.BindingFlags.GetProperty, null, obj, Array.Empty<object>());
                return value as string[];
            }
            catch
            {
                return null;
            }
        }
    }
}
