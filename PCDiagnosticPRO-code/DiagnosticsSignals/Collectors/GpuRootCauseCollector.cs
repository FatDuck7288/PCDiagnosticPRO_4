using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.DiagnosticsSignals.Collectors
{
    /// <summary>
    /// Collects GPU TDR (Timeout Detection and Recovery) events and root cause signals.
    /// Sources: Display, nvlddmkm (NVIDIA), amdkmdag (AMD)
    /// </summary>
    public class GpuRootCauseCollector : ISignalCollector
    {
        public string Name => "gpuRootCause";
        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(15);
        public int Priority => 10;

        // TDR-related event sources
        private static readonly string[] TdrSources = { "Display", "nvlddmkm", "amdkmdag", "dxgkrnl" };

        public async Task<SignalResult> CollectAsync(CancellationToken ct)
        {
            return await Task.Run(() => CollectInternal(ct), ct);
        }

        private SignalResult CollectInternal(CancellationToken ct)
        {
            try
            {
                var now = DateTime.Now;
                var last7Days = now.AddDays(-7);
                var last30Days = now.AddDays(-30);

                int tdrCount7d = 0;
                int tdrCount30d = 0;
                var tdrEvents = new List<TdrEvent>();
                var evidence = new List<string>();

                // Build query for TDR sources
                var sourceConditions = string.Join(" or ", TdrSources.Select(s => $"@Name='{s}'"));
                string query = $"*[System[Provider[{sourceConditions}] and (Level=2 or Level=3)]]";

                try
                {
                    var logQuery = new EventLogQuery("System", PathType.LogName, query);
                    using var reader = new EventLogReader(logQuery);

                    EventRecord evt;
                    while ((evt = reader.ReadEvent()) != null && !ct.IsCancellationRequested)
                    {
                        using (evt)
                        {
                            if (!evt.TimeCreated.HasValue) continue;

                            var eventTime = evt.TimeCreated.Value;
                            var providerName = evt.ProviderName ?? "";
                            var message = evt.FormatDescription() ?? "";

                            // Check if this is a TDR event
                            bool isTdr = IsTdrEvent(evt.Id, providerName, message);
                            if (!isTdr) continue;

                            if (eventTime >= last7Days)
                            {
                                tdrCount7d++;
                            }
                            if (eventTime >= last30Days)
                            {
                                tdrCount30d++;
                                if (tdrEvents.Count < 10)
                                {
                                    tdrEvents.Add(new TdrEvent
                                    {
                                        Id = evt.Id,
                                        Time = eventTime.ToString("o"),
                                        Source = providerName,
                                        Message = TruncateMessage(message, 150)
                                    });
                                }
                            }
                        }
                    }
                }
                catch (EventLogNotFoundException)
                {
                    SignalsLogger.LogInfo(Name, "No TDR events found in System log");
                }
                catch (UnauthorizedAccessException)
                {
                    return SignalResult.Unavailable(Name, "access_denied", "EventLog_System_TDR");
                }

                // Build evidence
                if (tdrCount7d > 0)
                    evidence.Add($"TDR events last 7 days: {tdrCount7d}");
                if (tdrCount30d > 3)
                    evidence.Add("High TDR frequency (>3/month)");

                // Throttling suspected if TDR is recurring
                bool throttlingSuspected = tdrCount7d >= 2 || tdrCount30d >= 5;
                if (throttlingSuspected)
                    evidence.Add("Throttling suspected due to recurring TDR");

                var result = new GpuRootCauseResult
                {
                    TdrCount7d = tdrCount7d,
                    TdrCount30d = tdrCount30d,
                    PerfcapAvailable = false, // NVML not implemented
                    PerfcapReason = null,
                    ThrottlingSuspected = throttlingSuspected,
                    Evidence = evidence,
                    LastEvents = tdrEvents.OrderByDescending(e => e.Time).Take(5).ToList()
                };

                var quality = tdrCount7d > 0 ? "suspect" : "ok";

                return new SignalResult
                {
                    Name = Name,
                    Value = result,
                    Available = true,
                    Source = "EventLog_System_TDR",
                    Quality = quality,
                    Notes = $"TDR: 7d={tdrCount7d}, 30d={tdrCount30d}",
                    Timestamp = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                SignalsLogger.LogException(Name, ex);
                return SignalResult.Unavailable(Name, $"error: {ex.Message}", "EventLog_System_TDR");
            }
        }

        private static bool IsTdrEvent(int eventId, string provider, string message)
        {
            // Common TDR event IDs
            // Display: 4101 (TDR success), 4097 (reset)
            // nvlddmkm: various
            if (eventId == 4101 || eventId == 4097) return true;

            // Check message for TDR keywords
            var lowerMessage = message.ToLowerInvariant();
            return lowerMessage.Contains("tdr") ||
                   lowerMessage.Contains("timeout detection and recovery") ||
                   lowerMessage.Contains("display driver stopped responding") ||
                   lowerMessage.Contains("driver has been reset");
        }

        private static string? TruncateMessage(string? message, int maxLength)
        {
            if (string.IsNullOrEmpty(message)) return null;
            return message.Length <= maxLength ? message : message.Substring(0, maxLength) + "...";
        }
    }

    public class GpuRootCauseResult
    {
        public int TdrCount7d { get; set; }
        public int TdrCount30d { get; set; }
        public bool PerfcapAvailable { get; set; }
        public string? PerfcapReason { get; set; }
        public bool ThrottlingSuspected { get; set; }
        public List<string> Evidence { get; set; } = new();
        public List<TdrEvent> LastEvents { get; set; } = new();
    }

    public class TdrEvent
    {
        public int Id { get; set; }
        public string Time { get; set; } = "";
        public string Source { get; set; } = "";
        public string? Message { get; set; }
    }
}
