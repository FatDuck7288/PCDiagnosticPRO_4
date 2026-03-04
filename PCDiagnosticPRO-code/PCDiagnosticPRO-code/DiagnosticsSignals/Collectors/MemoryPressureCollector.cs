using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.DiagnosticsSignals.Collectors
{
    /// <summary>
    /// Collects memory pressure metrics: hard faults sustained over time.
    /// </summary>
    public class MemoryPressureCollector : ISignalCollector
    {
        public string Name => "memoryPressure";
        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(35);
        public int Priority => 5;

        private const int SampleDurationSeconds = 30;
        private const int SampleIntervalMs = 1000;
        private const double HighFaultThreshold = 500; // Faults/sec considered high

        public async Task<SignalResult> CollectAsync(CancellationToken ct)
        {
            try
            {
                var samples = new List<double>();
                int sustainedAboveThreshold = 0;

                using var counter = new PerformanceCounter(
                    "Memory", "Page Faults/sec", "", true);

                // Prime the counter
                counter.NextValue();
                await Task.Delay(100, ct);

                // Collect samples
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < SampleDurationSeconds && !ct.IsCancellationRequested)
                {
                    try
                    {
                        float value = counter.NextValue();
                        samples.Add(value);
                        
                        if (value > HighFaultThreshold)
                            sustainedAboveThreshold++;
                    }
                    catch (Exception ex)
                    {
                        SignalsLogger.LogWarning(Name, $"Sample error: {ex.Message}");
                    }

                    await Task.Delay(SampleIntervalMs, ct);
                }

                if (samples.Count == 0)
                {
                    return SignalResult.Unavailable(Name, "no_samples_collected", "PerfCounter_Memory");
                }

                // Calculate statistics
                samples.Sort();
                double avg = samples.Average();
                double max = samples.Max();
                int p95Index = (int)Math.Ceiling(samples.Count * 0.95) - 1;
                double p95 = samples[Math.Max(0, p95Index)];

                var result = new MemoryPressureResult
                {
                    HardFaultsAvg = Math.Round(avg, 1),
                    HardFaultsP95 = Math.Round(p95, 1),
                    HardFaultsMax = Math.Round(max, 1),
                    SustainedSeconds = sustainedAboveThreshold,
                    WindowSeconds = (int)sw.Elapsed.TotalSeconds,
                    SampleCount = samples.Count
                };

                var quality = sustainedAboveThreshold > 5 ? "suspect" :
                              p95 > HighFaultThreshold ? "partial" : "ok";

                return new SignalResult
                {
                    Name = Name,
                    Value = result,
                    Available = true,
                    Source = "PerfCounter_Memory",
                    Quality = quality,
                    Notes = $"avg={avg:F0}/s, p95={p95:F0}/s, sustained={sustainedAboveThreshold}s",
                    Timestamp = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                SignalsLogger.LogException(Name, ex);
                return SignalResult.Unavailable(Name, $"error: {ex.Message}", "PerfCounter_Memory");
            }
        }
    }

    public class MemoryPressureResult
    {
        public double HardFaultsAvg { get; set; }
        public double HardFaultsP95 { get; set; }
        public double HardFaultsMax { get; set; }
        public int SustainedSeconds { get; set; }
        public int WindowSeconds { get; set; }
        public int SampleCount { get; set; }
    }
}
