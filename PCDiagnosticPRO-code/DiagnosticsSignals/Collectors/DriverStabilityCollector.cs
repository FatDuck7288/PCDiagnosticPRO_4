using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.DiagnosticsSignals.Collectors
{
    /// <summary>
    /// Collects driver stability signals: TDR, Kernel-Power 41, BugCheck, WER crashes.
    /// </summary>
    public class DriverStabilityCollector : ISignalCollector
    {
        public string Name => "driverStability";
        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(20);
        public int Priority => 8;

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
                var events = new List<StabilityEvent>();

                int tdrCount = 0;
                int kernelPower41Count = 0;
                int bugcheckCount = 0;
                int appCrashGpuRelated = 0;

                // 1. TDR events (Display, nvlddmkm, amdkmdag)
                tdrCount = QueryTdrEvents(last30Days, events, ct);

                // 2. Kernel-Power 41 (unexpected shutdown)
                kernelPower41Count = QueryKernelPower41(last30Days, events, ct);

                // 3. BugCheck events
                bugcheckCount = QueryBugCheckEvents(last30Days, events, ct);

                // 4. WER GPU-related crashes (Application log)
                appCrashGpuRelated = QueryWerGpuCrashes(last30Days, events, ct);

                var result = new DriverStabilityResult
                {
                    TdrCount30d = tdrCount,
                    KernelPower41Count30d = kernelPower41Count,
                    BugcheckCount30d = bugcheckCount,
                    AppCrashGpuRelated = appCrashGpuRelated,
                    LastEvents = events.OrderByDescending(e => e.Time).Take(10).ToList()
                };

                // Determine quality
                bool hasCritical = kernelPower41Count > 0 || bugcheckCount > 0;
                bool hasWarning = tdrCount > 3 || appCrashGpuRelated > 0;
                var quality = hasCritical ? "suspect" : (hasWarning ? "partial" : "ok");

                return new SignalResult
                {
                    Name = Name,
                    Value = result,
                    Available = true,
                    Source = "EventLog_System+Application",
                    Quality = quality,
                    Notes = $"TDR={tdrCount}, KP41={kernelPower41Count}, BugCheck={bugcheckCount}",
                    Timestamp = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                SignalsLogger.LogException(Name, ex);
                return SignalResult.Unavailable(Name, $"error: {ex.Message}", "EventLog");
            }
        }

        private int QueryTdrEvents(DateTime since, List<StabilityEvent> events, CancellationToken ct)
        {
            int count = 0;
            try
            {
                string query = "*[System[Provider[@Name='Display' or @Name='nvlddmkm' or @Name='amdkmdag'] and (Level=2 or Level=3)]]";
                var logQuery = new EventLogQuery("System", PathType.LogName, query);
                using var reader = new EventLogReader(logQuery);

                EventRecord evt;
                while ((evt = reader.ReadEvent()) != null && !ct.IsCancellationRequested)
                {
                    using (evt)
                    {
                        if (!evt.TimeCreated.HasValue || evt.TimeCreated.Value < since) continue;
                        
                        var message = evt.FormatDescription() ?? "";
                        if (IsTdrRelated(message))
                        {
                            count++;
                            if (events.Count < 20)
                            {
                                events.Add(new StabilityEvent
                                {
                                    Type = "TDR",
                                    Time = evt.TimeCreated.Value.ToString("o"),
                                    Details = TruncateMessage(message, 100)
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SignalsLogger.LogWarning(Name, $"TDR query error: {ex.Message}");
            }
            return count;
        }

        private int QueryKernelPower41(DateTime since, List<StabilityEvent> events, CancellationToken ct)
        {
            int count = 0;
            try
            {
                string query = "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41]]";
                var logQuery = new EventLogQuery("System", PathType.LogName, query);
                using var reader = new EventLogReader(logQuery);

                EventRecord evt;
                while ((evt = reader.ReadEvent()) != null && !ct.IsCancellationRequested)
                {
                    using (evt)
                    {
                        if (!evt.TimeCreated.HasValue || evt.TimeCreated.Value < since) continue;
                        count++;
                        if (events.Count < 20)
                        {
                            events.Add(new StabilityEvent
                            {
                                Type = "Kernel-Power-41",
                                Time = evt.TimeCreated.Value.ToString("o"),
                                Details = "Unexpected shutdown (power loss or crash)"
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SignalsLogger.LogWarning(Name, $"Kernel-Power query error: {ex.Message}");
            }
            return count;
        }

        private int QueryBugCheckEvents(DateTime since, List<StabilityEvent> events, CancellationToken ct)
        {
            int count = 0;
            try
            {
                // BugCheck events from various sources
                string query = "*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] or " +
                               "(Provider[@Name='BugCheck'] and EventID=1001)]]";
                var logQuery = new EventLogQuery("System", PathType.LogName, query);
                using var reader = new EventLogReader(logQuery);

                EventRecord evt;
                while ((evt = reader.ReadEvent()) != null && !ct.IsCancellationRequested)
                {
                    using (evt)
                    {
                        if (!evt.TimeCreated.HasValue || evt.TimeCreated.Value < since) continue;
                        count++;
                        if (events.Count < 20)
                        {
                            events.Add(new StabilityEvent
                            {
                                Type = "BugCheck",
                                Time = evt.TimeCreated.Value.ToString("o"),
                                Details = TruncateMessage(evt.FormatDescription(), 100)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SignalsLogger.LogWarning(Name, $"BugCheck query error: {ex.Message}");
            }
            return count;
        }

        private int QueryWerGpuCrashes(DateTime since, List<StabilityEvent> events, CancellationToken ct)
        {
            int count = 0;
            try
            {
                // Application crashes from WER
                string query = "*[System[Provider[@Name='Application Error'] and EventID=1000]]";
                var logQuery = new EventLogQuery("Application", PathType.LogName, query);
                using var reader = new EventLogReader(logQuery);

                EventRecord evt;
                while ((evt = reader.ReadEvent()) != null && !ct.IsCancellationRequested)
                {
                    using (evt)
                    {
                        if (!evt.TimeCreated.HasValue || evt.TimeCreated.Value < since) continue;
                        
                        var message = evt.FormatDescription() ?? "";
                        // Check if GPU-related
                        if (IsGpuRelated(message))
                        {
                            count++;
                            if (events.Count < 20)
                            {
                                events.Add(new StabilityEvent
                                {
                                    Type = "AppCrash_GPU",
                                    Time = evt.TimeCreated.Value.ToString("o"),
                                    Details = TruncateMessage(message, 100)
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SignalsLogger.LogWarning(Name, $"WER query error: {ex.Message}");
            }
            return count;
        }

        private static bool IsTdrRelated(string message)
        {
            var lower = message.ToLowerInvariant();
            return lower.Contains("tdr") || 
                   lower.Contains("timeout detection") ||
                   lower.Contains("display driver stopped") ||
                   lower.Contains("driver has been reset");
        }

        private static bool IsGpuRelated(string message)
        {
            var lower = message.ToLowerInvariant();
            return lower.Contains("nvlddmkm") ||
                   lower.Contains("amdkmdag") ||
                   lower.Contains("d3d") ||
                   lower.Contains("directx") ||
                   lower.Contains("gpu");
        }

        private static string? TruncateMessage(string? message, int maxLength)
        {
            if (string.IsNullOrEmpty(message)) return null;
            return message.Length <= maxLength ? message : message.Substring(0, maxLength) + "...";
        }
    }

    public class DriverStabilityResult
    {
        public int TdrCount30d { get; set; }
        public int KernelPower41Count30d { get; set; }
        public int BugcheckCount30d { get; set; }
        public int AppCrashGpuRelated { get; set; }
        public List<StabilityEvent> LastEvents { get; set; } = new();
    }

    public class StabilityEvent
    {
        public string Type { get; set; } = "";
        public string Time { get; set; } = "";
        public string? Details { get; set; }
    }
}
