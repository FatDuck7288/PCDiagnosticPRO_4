using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.DiagnosticsSignals.Collectors
{
    /// <summary>
    /// Collects Windows Hardware Error Architecture (WHEA) events.
    /// Detects real hardware degradation signals.
    /// </summary>
    public class WheaCollector : ISignalCollector
    {
        public string Name => "whea";
        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(15);
        public int Priority => 15;

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

                var events7d = new List<WheaEvent>();
                var events30d = new List<WheaEvent>();
                int correctedCount = 0;
                int fatalCount = 0;

                // Query WHEA-Logger events from System log
                string query = "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger']]]";
                
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
                            var wheaEvent = new WheaEvent
                            {
                                Id = evt.Id,
                                Time = eventTime.ToString("o"),
                                Level = evt.Level ?? 0,
                                Message = TruncateMessage(evt.FormatDescription(), 200)
                            };

                            // Categorize by severity
                            if (evt.Level <= 2) // Critical or Error
                                fatalCount++;
                            else
                                correctedCount++;

                            if (eventTime >= last7Days)
                            {
                                events7d.Add(wheaEvent);
                            }
                            if (eventTime >= last30Days)
                            {
                                events30d.Add(wheaEvent);
                            }
                        }
                    }
                }
                catch (EventLogNotFoundException)
                {
                    SignalsLogger.LogInfo(Name, "WHEA-Logger provider not found in System log");
                }
                catch (UnauthorizedAccessException)
                {
                    return SignalResult.Unavailable(Name, "access_denied", "EventLog_System_WHEA-Logger");
                }

                var result = new WheaResult
                {
                    Last7dCount = events7d.Count,
                    Last30dCount = events30d.Count,
                    CorrectedCount = correctedCount,
                    FatalCount = fatalCount,
                    LastEvents = events7d.OrderByDescending(e => e.Time).Take(5).ToList()
                };

                var quality = fatalCount > 0 ? "suspect" : (events30d.Count > 0 ? "partial" : "ok");
                
                return new SignalResult
                {
                    Name = Name,
                    Value = result,
                    Available = true,
                    Source = "EventLog_System_WHEA-Logger",
                    Quality = quality,
                    Notes = $"7d={events7d.Count}, 30d={events30d.Count}, fatal={fatalCount}",
                    Timestamp = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                SignalsLogger.LogException(Name, ex);
                return SignalResult.Unavailable(Name, $"error: {ex.Message}", "EventLog_System_WHEA-Logger");
            }
        }

        private static string? TruncateMessage(string? message, int maxLength)
        {
            if (string.IsNullOrEmpty(message)) return null;
            return message.Length <= maxLength ? message : message.Substring(0, maxLength) + "...";
        }
    }

    public class WheaResult
    {
        public int Last7dCount { get; set; }
        public int Last30dCount { get; set; }
        public int CorrectedCount { get; set; }
        public int FatalCount { get; set; }
        public List<WheaEvent> LastEvents { get; set; } = new();
    }

    public class WheaEvent
    {
        public int Id { get; set; }
        public string Time { get; set; } = "";
        public byte Level { get; set; }
        public string? Message { get; set; }
    }
}
