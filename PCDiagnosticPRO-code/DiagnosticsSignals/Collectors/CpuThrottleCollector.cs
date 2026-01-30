using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.DiagnosticsSignals.Collectors
{
    /// <summary>
    /// Collects CPU throttle events and performance metrics.
    /// Source: Kernel-Processor-Power Event ID 37 + Performance Counters
    /// </summary>
    public class CpuThrottleCollector : ISignalCollector
    {
        public string Name => "cpuThrottle";
        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(20);
        public int Priority => 6;

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

                int firmwareThrottle7d = 0;
                int firmwareThrottle30d = 0;
                var evidence = new List<string>();

                // Query Kernel-Processor-Power Event ID 37 (firmware speed limit)
                string query = "*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and EventID=37]]";

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

                            if (eventTime >= last7Days)
                                firmwareThrottle7d++;
                            if (eventTime >= last30Days)
                                firmwareThrottle30d++;
                        }
                    }
                }
                catch (EventLogNotFoundException)
                {
                    SignalsLogger.LogInfo(Name, "Kernel-Processor-Power provider not found");
                }
                catch (UnauthorizedAccessException)
                {
                    evidence.Add("EventLog access denied");
                }

                // Collect performance counters
                double perfPercentAvg = 0;
                double freqMhzAvg = 0;
                bool perfCountersOk = false;

                try
                {
                    // % Processor Performance (can indicate throttling if < 100%)
                    using var perfCounter = new PerformanceCounter(
                        "Processor Information", "% Processor Performance", "_Total", true);
                    
                    // Take a few samples
                    var samples = new List<double>();
                    for (int i = 0; i < 5 && !ct.IsCancellationRequested; i++)
                    {
                        float val = perfCounter.NextValue();
                        if (i > 0) // Skip first sample (always 0)
                            samples.Add(val);
                        Thread.Sleep(200);
                    }
                    
                    if (samples.Count > 0)
                    {
                        perfPercentAvg = samples.Average();
                        perfCountersOk = true;
                    }
                }
                catch (Exception ex)
                {
                    SignalsLogger.LogWarning(Name, $"PerfCounter error: {ex.Message}");
                }

                // Try to get frequency
                try
                {
                    using var freqCounter = new PerformanceCounter(
                        "Processor Information", "Processor Frequency", "_Total", true);
                    freqMhzAvg = freqCounter.NextValue();
                    Thread.Sleep(100);
                    freqMhzAvg = freqCounter.NextValue();
                }
                catch { /* Frequency counter not always available */ }

                // Build evidence
                if (firmwareThrottle7d > 0)
                    evidence.Add($"Firmware throttle events 7d: {firmwareThrottle7d}");
                if (firmwareThrottle30d > 5)
                    evidence.Add("High firmware throttle frequency (>5/month)");
                if (perfPercentAvg > 0 && perfPercentAvg < 80)
                    evidence.Add($"Processor performance below nominal: {perfPercentAvg:F1}%");

                // Throttle suspected?
                bool throttleSuspected = firmwareThrottle7d >= 3 || 
                                         (perfPercentAvg > 0 && perfPercentAvg < 70);

                var result = new CpuThrottleResult
                {
                    FirmwareThrottleEvents7d = firmwareThrottle7d,
                    FirmwareThrottleEvents30d = firmwareThrottle30d,
                    PerfPercentAvg = Math.Round(perfPercentAvg, 1),
                    FreqMhzAvg = (int)freqMhzAvg,
                    ThrottleSuspected = throttleSuspected,
                    Evidence = evidence
                };

                var quality = throttleSuspected ? "suspect" : 
                              (firmwareThrottle30d > 0 ? "partial" : "ok");

                return new SignalResult
                {
                    Name = Name,
                    Value = result,
                    Available = true,
                    Source = "EventLog+PerfCounter",
                    Quality = quality,
                    Notes = $"throttle7d={firmwareThrottle7d}, perf={perfPercentAvg:F1}%",
                    Timestamp = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                SignalsLogger.LogException(Name, ex);
                return SignalResult.Unavailable(Name, $"error: {ex.Message}", "EventLog+PerfCounter");
            }
        }
    }

    public class CpuThrottleResult
    {
        public int FirmwareThrottleEvents7d { get; set; }
        public int FirmwareThrottleEvents30d { get; set; }
        public double PerfPercentAvg { get; set; }
        public int FreqMhzAvg { get; set; }
        public bool ThrottleSuspected { get; set; }
        public List<string> Evidence { get; set; } = new();
    }
}
