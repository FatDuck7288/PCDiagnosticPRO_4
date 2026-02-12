using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Threading.Tasks;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Collects the last N critical/error event log entries (EventId, ProviderName, Message, Timestamp).
    /// Also collects stability-specific events (WHEA, Kernel-Power, BugCheck) over 30 days.
    /// </summary>
    public static class EventLogDetailedCollector
    {
        public const int DefaultMaxEventsPerLog = 50;
        public const int DefaultHoursBack = 48;
        public const int StabilityHoursBack = 720; // 30 days for stability events
        public const int StabilityMaxEvents = 100;

        /// <summary>
        /// Collect critical (1) and error (2) events from System and Application logs.
        /// Includes targeted 30-day collection for WHEA, Kernel-Power, and BugCheck events.
        /// </summary>
        public static async Task<List<EventLogDetailedEntry>?> CollectAsync(
            int maxEventsPerLog = DefaultMaxEventsPerLog,
            int hoursBack = DefaultHoursBack,
            System.Threading.CancellationToken ct = default)
        {
            var results = new List<EventLogDetailedEntry>();
            var seenKeys = new HashSet<string>(); // Dedup: "LogName|EventId|TimeCreated"
            var startTime = DateTime.UtcNow.AddHours(-hoursBack);

            await Task.Run(() =>
            {
                try
                {
                    // === Phase 1: General critical/error events (48h) ===
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
                                var entry = RecordToEntry(record, logName);
                                var key = $"{logName}|{entry.EventId}|{entry.TimeCreated:o}";
                                if (seenKeys.Add(key))
                                {
                                    results.Add(entry);
                                    count++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            App.LogMessage($"[EventLogDetailed] {logName}: {ex.Message}");
                        }
                    }

                    // === Phase 2: Stability-specific events (30 days) - WHEA, Kernel-Power, BugCheck ===
                    CollectStabilityEvents(results, seenKeys, ct);

                    if (results.Count > 0)
                        App.LogMessage($"[EventLogDetailed] Collected {results.Count} events (incl. stability 30d)");
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[EventLogDetailed] Error: {ex.Message}");
                }
            }, ct).ConfigureAwait(false);

            return results.Count > 0 ? results : null;
        }

        /// <summary>
        /// Collect WHEA, Kernel-Power 41, and BugCheck events from the last 30 days.
        /// These are critical for the Stability section and need a wider time window than general events.
        /// </summary>
        private static void CollectStabilityEvents(
            List<EventLogDetailedEntry> results, HashSet<string> seenKeys, System.Threading.CancellationToken ct)
        {
            var stabilityStart = DateTime.UtcNow.AddHours(-StabilityHoursBack);

            // WHEA-Logger events (all levels, including warnings)
            CollectProviderEvents(results, seenKeys, "System",
                "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger']]]",
                stabilityStart, StabilityMaxEvents, ct);

            // Kernel-Power EventID 41 (unexpected shutdown)
            CollectProviderEvents(results, seenKeys, "System",
                "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41]]",
                stabilityStart, StabilityMaxEvents, ct);

            // Kernel-Power EventID 1 (power state change: sleep/wake, AC/battery, power plan — informational only)
            CollectProviderEvents(results, seenKeys, "System",
                "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=1]]",
                stabilityStart, StabilityMaxEvents, ct);

            // BugCheck events (WER-SystemErrorReporting or BugCheck provider)
            CollectProviderEvents(results, seenKeys, "System",
                "*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] or (Provider[@Name='BugCheck'] and EventID=1001)]]",
                stabilityStart, StabilityMaxEvents, ct);

            // Kernel-Processor-Power: EventID 37 (firmware limit / CPU throttled), EventID 34 (thermal throttle) — for CPU throttling fallback
            CollectProviderEvents(results, seenKeys, "System",
                "*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and (EventID=37 or EventID=34)]]",
                stabilityStart, StabilityMaxEvents, ct);
        }

        private static void CollectProviderEvents(
            List<EventLogDetailedEntry> results, HashSet<string> seenKeys,
            string logName, string xpath, DateTime startTime, int maxEvents,
            System.Threading.CancellationToken ct)
        {
            try
            {
                var query = new EventLogQuery(logName, PathType.LogName, xpath)
                {
                    ReverseDirection = true
                };
                using var reader = new EventLogReader(query);
                int count = 0;
                for (EventRecord record = reader.ReadEvent(); record != null && count < maxEvents; record = reader.ReadEvent())
                {
                    if (ct.IsCancellationRequested) break;
                    if (record.TimeCreated.HasValue)
                    {
                        var t = record.TimeCreated.Value;
                        var utc = t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();
                        if (utc < startTime) continue;
                    }
                    var entry = RecordToEntry(record, logName);
                    var key = $"{logName}|{entry.EventId}|{entry.TimeCreated:o}";
                    if (seenKeys.Add(key))
                    {
                        results.Add(entry);
                        count++;
                    }
                }
            }
            catch (EventLogNotFoundException)
            {
                // Provider not found — normal, some systems don't have these
            }
            catch (UnauthorizedAccessException)
            {
                App.LogMessage($"[EventLogDetailed] Access denied for stability query in {logName}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[EventLogDetailed] Stability query error ({logName}): {ex.Message}");
            }
        }

        private static EventLogDetailedEntry RecordToEntry(EventRecord record, string logName)
        {
            int? level = null;
            try { level = record.Level; } catch { }
            DateTime? timeCreatedUtc = record.TimeCreated.HasValue
                ? (record.TimeCreated!.Value.Kind == DateTimeKind.Utc ? record.TimeCreated.Value : record.TimeCreated.Value.ToUniversalTime())
                : (DateTime?)null;
            return new EventLogDetailedEntry
            {
                EventId = record.Id,
                ProviderName = record.ProviderName ?? "",
                Message = record.FormatDescription() ?? record.ToXml() ?? "",
                TimeCreated = timeCreatedUtc,
                LogName = logName,
                Level = level
            };
        }
    }
}
