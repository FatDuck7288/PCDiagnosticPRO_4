using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Threading.Tasks;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Collects the last N critical/error event log entries (EventId, ProviderName, Message, Timestamp).
    /// </summary>
    public static class EventLogDetailedCollector
    {
        public const int DefaultMaxEventsPerLog = 50;
        public const int DefaultHoursBack = 48;

        /// <summary>
        /// Collect critical (1) and error (2) events from System and Application logs.
        /// </summary>
        public static async Task<List<EventLogDetailedEntry>?> CollectAsync(
            int maxEventsPerLog = DefaultMaxEventsPerLog,
            int hoursBack = DefaultHoursBack,
            System.Threading.CancellationToken ct = default)
        {
            var results = new List<EventLogDetailedEntry>();
            var startTime = DateTime.UtcNow.AddHours(-hoursBack);

            await Task.Run(() =>
            {
                try
                {
                    foreach (var logName in new[] { "System", "Application" })
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            // Restrict to Critical (1) and Error (2) only
                            var xpath = "*[System[Level=1 or Level=2]]";
                            var query = new EventLogQuery(logName, PathType.LogName, xpath)
                            {
                                ReverseDirection = true
                            };
                            using var reader = new EventLogReader(query);
                            int count = 0;
                            for (EventRecord record = reader.ReadEvent(); record != null && count < maxEventsPerLog; record = reader.ReadEvent())
                            {
                                if (ct.IsCancellationRequested) break;
                                if (record.TimeCreated.HasValue)
                                {
                                    var t = record.TimeCreated.Value;
                                    var utc = t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();
                                    if (utc < startTime) continue;
                                }
                                int? level = null;
                                try { level = record.Level; } catch { }
                                DateTime? timeCreatedUtc = record.TimeCreated.HasValue
                                    ? (record.TimeCreated!.Value.Kind == DateTimeKind.Utc ? record.TimeCreated.Value : record.TimeCreated.Value.ToUniversalTime())
                                    : (DateTime?)null;
                                results.Add(new EventLogDetailedEntry
                                {
                                    EventId = record.Id,
                                    ProviderName = record.ProviderName ?? "",
                                    Message = record.FormatDescription() ?? record.ToXml() ?? "",
                                    TimeCreated = timeCreatedUtc,
                                    LogName = logName,
                                    Level = level
                                });
                                count++;
                            }
                        }
                        catch (Exception ex)
                        {
                            App.LogMessage($"[EventLogDetailed] {logName}: {ex.Message}");
                        }
                    }

                    if (results.Count > 0)
                        App.LogMessage($"[EventLogDetailed] Collected {results.Count} events");
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[EventLogDetailed] Error: {ex.Message}");
                }
            }, ct).ConfigureAwait(false);

            return results.Count > 0 ? results : null;
        }
    }
}
