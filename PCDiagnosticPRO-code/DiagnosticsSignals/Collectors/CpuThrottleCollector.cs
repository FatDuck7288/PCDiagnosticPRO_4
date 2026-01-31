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
    /// P0.4: Collects CPU throttle events and performance metrics.
    /// Source: Kernel-Processor-Power Event ID 37 + Performance Counters
    /// Provides percentOfMaxFreqAvg, percentOfMaxFreqMin, throttlingEventCount7d
    /// </summary>
    public class CpuThrottleCollector : ISignalCollector
    {
        public string Name => "cpuThrottle";
        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(20);
        public int Priority => 6;

        private const int SampleCount = 5;
        private const int SampleDelayMs = 200;

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

                int throttleEvents7d = 0;
                int throttleEvents30d = 0;
                int thermalThrottleCount = 0;
                int powerLimitCount = 0;
                DateTime? lastThrottleTime = null;
                var evidence = new List<string>();

                // Query Kernel-Processor-Power events for throttling
                CollectThrottleEvents(ct, last7Days, last30Days,
                    ref throttleEvents7d, ref throttleEvents30d,
                    ref thermalThrottleCount, ref powerLimitCount,
                    ref lastThrottleTime, evidence);

                // Collect % of Maximum Frequency performance counter
                var (perfPercentAvg, perfPercentMin, perfCountersOk) = CollectFrequencyCounters(ct, evidence);

                // Collect actual frequency if available
                var freqMhzAvg = CollectActualFrequency(ct);

                // Throttle suspected analysis
                bool throttleSuspected = throttleEvents7d >= 3 ||
                                         thermalThrottleCount > 0 ||
                                         (perfCountersOk && perfPercentAvg > 0 && perfPercentAvg < 85);

                var result = new CpuThrottleResult
                {
                    // Event-based metrics
                    ThrottlingEventCount7d = throttleEvents7d,
                    ThrottlingEventCount30d = throttleEvents30d,
                    ThermalThrottleCount = thermalThrottleCount,
                    PowerLimitCount = powerLimitCount,
                    LastThrottleEventTime = lastThrottleTime?.ToString("o"),

                    // Frequency metrics (required by spec)
                    PercentOfMaxFreqAvg = Math.Round(perfPercentAvg, 1),
                    PercentOfMaxFreqMin = Math.Round(perfPercentMin, 1),
                    FreqMhzAvg = (int)freqMhzAvg,
                    
                    // Analysis
                    ThrottleSuspected = throttleSuspected,
                    Evidence = evidence,
                    
                    // Counters availability
                    CountersAvailable = perfCountersOk
                };

                var quality = throttleSuspected ? "suspect" :
                              (throttleEvents30d > 0 ? "partial" : "ok");

                return new SignalResult
                {
                    Name = Name,
                    Value = result,
                    Available = true,
                    Source = "EventLog+PerfCounter",
                    Quality = quality,
                    Notes = $"throttle7d={throttleEvents7d}, perf={perfPercentAvg:F1}%, min={perfPercentMin:F1}%",
                    Timestamp = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                SignalsLogger.LogException(Name, ex);
                return SignalResult.Unavailable(Name, $"error: {ex.Message}", "EventLog+PerfCounter");
            }
        }

        private void CollectThrottleEvents(
            CancellationToken ct,
            DateTime last7Days,
            DateTime last30Days,
            ref int throttle7d,
            ref int throttle30d,
            ref int thermalCount,
            ref int powerLimitCount,
            ref DateTime? lastThrottleTime,
            List<string> evidence)
        {
            // Event ID 37 = Firmware limit (CPU throttled by firmware)
            // Event ID 34 = Thermal throttle
            var eventIds = new[] { 37, 34 };

            foreach (var eventId in eventIds)
            {
                try
                {
                    string query = $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and EventID={eventId}]]";
                    var logQuery = new EventLogQuery("System", PathType.LogName, query);
                    using var reader = new EventLogReader(logQuery);

                    EventRecord? evt;
                    while ((evt = reader.ReadEvent()) != null && !ct.IsCancellationRequested)
                    {
                        using (evt)
                        {
                            if (!evt.TimeCreated.HasValue) continue;
                            var eventTime = evt.TimeCreated.Value;

                            if (eventTime >= last7Days)
                            {
                                throttle7d++;
                                if (eventId == 34) thermalCount++;
                                if (eventId == 37) powerLimitCount++;
                            }
                            if (eventTime >= last30Days)
                            {
                                throttle30d++;
                            }

                            // Track last event time
                            if (!lastThrottleTime.HasValue || eventTime > lastThrottleTime.Value)
                            {
                                lastThrottleTime = eventTime;
                            }
                        }
                    }
                }
                catch (EventLogNotFoundException)
                {
                    SignalsLogger.LogInfo(Name, $"EventID {eventId} query returned no results");
                }
                catch (UnauthorizedAccessException)
                {
                    evidence.Add("EventLog access denied");
                }
                catch (Exception ex)
                {
                    SignalsLogger.LogWarning(Name, $"EventLog query error for EventID {eventId}: {ex.Message}");
                }
            }

            // Build evidence
            if (throttle7d > 0)
                evidence.Add($"Throttle events 7d: {throttle7d}");
            if (thermalCount > 0)
                evidence.Add($"Thermal throttle events: {thermalCount}");
            if (powerLimitCount > 0)
                evidence.Add($"Power limit events: {powerLimitCount}");
            if (throttle30d > 5)
                evidence.Add("High throttle frequency (>5/month)");
        }

        private (double avg, double min, bool ok) CollectFrequencyCounters(CancellationToken ct, List<string> evidence)
        {
            double avg = 0;
            double min = double.MaxValue;
            bool ok = false;

            try
            {
                // % of Maximum Frequency - most reliable indicator of throttling
                using var perfCounter = new PerformanceCounter(
                    "Processor Information", "% of Maximum Frequency", "_Total", true);

                var samples = new List<double>();
                for (int i = 0; i < SampleCount && !ct.IsCancellationRequested; i++)
                {
                    float val = perfCounter.NextValue();
                    if (i > 0) // Skip first sample (always 0)
                    {
                        samples.Add(val);
                        if (val < min) min = val;
                    }
                    Thread.Sleep(SampleDelayMs);
                }

                if (samples.Count > 0)
                {
                    avg = samples.Average();
                    ok = true;

                    // Evidence based on performance
                    if (avg < 70)
                        evidence.Add($"CPU running below 70% of max frequency: {avg:F1}%");
                    else if (avg < 85)
                        evidence.Add($"CPU running at reduced frequency: {avg:F1}%");
                }

                if (min == double.MaxValue) min = 0;
            }
            catch (Exception ex)
            {
                SignalsLogger.LogWarning(Name, $"PerfCounter '% of Maximum Frequency' error: {ex.Message}");
                evidence.Add("counters_not_available");
                min = 0;
            }

            return (avg, min, ok);
        }

        private double CollectActualFrequency(CancellationToken ct)
        {
            try
            {
                using var freqCounter = new PerformanceCounter(
                    "Processor Information", "Processor Frequency", "_Total", true);
                
                // Take a few samples
                freqCounter.NextValue();
                Thread.Sleep(100);
                var freq = freqCounter.NextValue();
                
                return freq;
            }
            catch
            {
                // Frequency counter not always available
                return 0;
            }
        }
    }

    public class CpuThrottleResult
    {
        // Required by spec: metrics.cpuThrottle.*
        public double PercentOfMaxFreqAvg { get; set; }
        public double PercentOfMaxFreqMin { get; set; }
        public int ThrottlingEventCount7d { get; set; }
        public int ThrottlingEventCount30d { get; set; }
        public string? LastThrottleEventTime { get; set; }
        
        // Additional metrics
        public int ThermalThrottleCount { get; set; }
        public int PowerLimitCount { get; set; }
        public int FreqMhzAvg { get; set; }
        
        // Analysis
        public bool ThrottleSuspected { get; set; }
        public List<string> Evidence { get; set; } = new();
        public bool CountersAvailable { get; set; }
        
        // Legacy compatibility
        public int FirmwareThrottleEvents7d => ThrottlingEventCount7d;
        public int FirmwareThrottleEvents30d => ThrottlingEventCount30d;
        public double PerfPercentAvg => PercentOfMaxFreqAvg;
    }
}
