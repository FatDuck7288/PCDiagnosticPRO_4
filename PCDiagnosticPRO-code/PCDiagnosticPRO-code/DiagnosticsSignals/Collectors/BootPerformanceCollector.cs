using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.DiagnosticsSignals.Collectors
{
    /// <summary>
    /// Collects boot performance metrics from Windows Diagnostics-Performance EventLog.
    /// Event ID 100: Boot complete time
    /// Event ID 101-110: Degradation events
    /// </summary>
    public class BootPerformanceCollector : ISignalCollector
    {
        public string Name => "bootPerformance";
        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(15);
        public int Priority => 4;

        public async Task<SignalResult> CollectAsync(CancellationToken ct)
        {
            return await Task.Run(() => CollectInternal(ct), ct);
        }

        private SignalResult CollectInternal(CancellationToken ct)
        {
            try
            {
                var now = DateTime.Now;
                var last30Days = now.AddDays(-30);
                
                int? lastBootMs = null;
                var bootTimes = new List<int>();
                int degradationEvents = 0;
                DateTime? lastEventTime = null;
                var details = new List<BootEvent>();

                // Query Diagnostics-Performance log
                // Event ID 100 = Boot complete, contains boot duration
                // Event ID 101-110 = Various degradation causes
                string query = "*[System[Provider[@Name='Microsoft-Windows-Diagnostics-Performance'] and (EventID>=100 and EventID<=110)]]";

                try
                {
                    var logQuery = new EventLogQuery(
                        "Microsoft-Windows-Diagnostics-Performance/Operational", 
                        PathType.LogName, 
                        query);
                    using var reader = new EventLogReader(logQuery);

                    EventRecord evt;
                    while ((evt = reader.ReadEvent()) != null && !ct.IsCancellationRequested)
                    {
                        using (evt)
                        {
                            if (!evt.TimeCreated.HasValue) continue;
                            var eventTime = evt.TimeCreated.Value;
                            
                            if (eventTime < last30Days) continue;

                            if (evt.Id == 100) // Boot complete
                            {
                                // Extract boot time from event properties
                                int? bootTime = ExtractBootTimeMs(evt);
                                if (bootTime.HasValue)
                                {
                                    bootTimes.Add(bootTime.Value);
                                    if (!lastBootMs.HasValue || eventTime > lastEventTime)
                                    {
                                        lastBootMs = bootTime.Value;
                                        lastEventTime = eventTime;
                                    }
                                }

                                if (details.Count < 10)
                                {
                                    details.Add(new BootEvent
                                    {
                                        Type = "BootComplete",
                                        Time = eventTime.ToString("o"),
                                        BootMs = bootTime
                                    });
                                }
                            }
                            else // Degradation events (101-110)
                            {
                                degradationEvents++;
                                if (details.Count < 10)
                                {
                                    details.Add(new BootEvent
                                    {
                                        Type = $"Degradation_{evt.Id}",
                                        Time = eventTime.ToString("o"),
                                        Details = TruncateMessage(evt.FormatDescription(), 100)
                                    });
                                }
                            }
                        }
                    }
                }
                catch (EventLogNotFoundException)
                {
                    // Try alternate approach using System log
                    SignalsLogger.LogInfo(Name, "Diagnostics-Performance log not found, trying fallback");
                    return CollectFallback(ct);
                }
                catch (UnauthorizedAccessException)
                {
                    return SignalResult.Unavailable(Name, "access_denied", "EventLog_Diagnostics-Performance");
                }

                // Calculate average boot time
                double avgBootMs = bootTimes.Count > 0 ? bootTimes.Average() : 0;

                var result = new BootPerformanceResult
                {
                    LastBootMs = lastBootMs,
                    AvgBootMs = Math.Round(avgBootMs, 0),
                    DegradationEvents30d = degradationEvents,
                    BootCount30d = bootTimes.Count,
                    LastEventTime = lastEventTime?.ToString("o"),
                    Details = details.OrderByDescending(d => d.Time).Take(5).ToList()
                };

                // Determine quality
                bool slowBoot = lastBootMs.HasValue && lastBootMs.Value > 120000; // >2 min
                bool hasDegradation = degradationEvents > 3;
                var quality = (slowBoot || hasDegradation) ? "suspect" : 
                              degradationEvents > 0 ? "partial" : "ok";

                return new SignalResult
                {
                    Name = Name,
                    Value = result,
                    Available = true,
                    Source = "EventLog_Diagnostics-Performance",
                    Quality = quality,
                    Notes = $"lastBoot={lastBootMs}ms, avg={avgBootMs:F0}ms, degradation={degradationEvents}",
                    Timestamp = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                SignalsLogger.LogException(Name, ex);
                return SignalResult.Unavailable(Name, $"error: {ex.Message}", "EventLog");
            }
        }

        private SignalResult CollectFallback(CancellationToken ct)
        {
            // Fallback: use last boot time from registry
            try
            {
                var lastBoot = Environment.TickCount64;
                var uptime = TimeSpan.FromMilliseconds(lastBoot);
                
                // Can't get actual boot duration from this, just uptime
                return SignalResult.Partial(Name, new BootPerformanceResult
                {
                    LastBootMs = null,
                    AvgBootMs = 0,
                    DegradationEvents30d = 0,
                    BootCount30d = 0,
                    Notes = $"Uptime: {uptime.Days}d {uptime.Hours}h (boot time unavailable)"
                }, "Fallback_TickCount", "Boot time unavailable, showing uptime only");
            }
            catch (Exception ex)
            {
                return SignalResult.Unavailable(Name, $"fallback_error: {ex.Message}", "Fallback_TickCount");
            }
        }

        private int? ExtractBootTimeMs(EventRecord evt)
        {
            try
            {
                // Event 100 typically has BootTime in properties[0] or in XML
                var props = evt.Properties;
                if (props != null && props.Count > 0)
                {
                    // First property is usually MainPathBootTime in ms
                    var val = props[0].Value;
                    if (val is int intVal) return intVal;
                    if (val is long longVal) return (int)longVal;
                    if (int.TryParse(val?.ToString(), out int parsed)) return parsed;
                }

                // Try parsing from description
                var desc = evt.FormatDescription();
                if (!string.IsNullOrEmpty(desc))
                {
                    // Look for patterns like "boot time of 45123 milliseconds"
                    var match = System.Text.RegularExpressions.Regex.Match(
                        desc, @"(\d+)\s*(ms|milliseconds)", 
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int ms))
                        return ms;
                }
            }
            catch { }
            return null;
        }

        private static string? TruncateMessage(string? message, int maxLength)
        {
            if (string.IsNullOrEmpty(message)) return null;
            return message.Length <= maxLength ? message : message.Substring(0, maxLength) + "...";
        }
    }

    public class BootPerformanceResult
    {
        public int? LastBootMs { get; set; }
        public double AvgBootMs { get; set; }
        public int DegradationEvents30d { get; set; }
        public int BootCount30d { get; set; }
        public string? LastEventTime { get; set; }
        public string? Notes { get; set; }
        public List<BootEvent> Details { get; set; } = new();
    }

    public class BootEvent
    {
        public string Type { get; set; } = "";
        public string Time { get; set; } = "";
        public int? BootMs { get; set; }
        public string? Details { get; set; }
    }
}
