using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.DiagnosticsSignals.Collectors
{
    /// <summary>
    /// PHASE 4.3: IO Latency Collector
    /// Source: PerfCounter or WMI (NOT ETW for simplicity)
    /// Collects: readLatencyMsP95, writeLatencyMsP95, queueDepthP95
    /// </summary>
    public class IoLatencyCollector : ISignalCollector
    {
        public string Name => "storageLatency";
        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(15);
        public int Priority => 6;

        private const int SampleCount = 10;
        private const int SampleIntervalMs = 500;

        public async Task<SignalResult> CollectAsync(CancellationToken ct)
        {
            try
            {
                var result = new IoLatencyResult();
                var readLatencies = new List<double>();
                var writeLatencies = new List<double>();
                var queueDepths = new List<double>();

                // Try to collect performance counter samples
                bool countersAvailable = await Task.Run(() =>
                {
                    try
                    {
                        using var readCounter = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Read", "_Total", true);
                        using var writeCounter = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Write", "_Total", true);
                        using var queueCounter = new PerformanceCounter("PhysicalDisk", "Avg. Disk Queue Length", "_Total", true);

                        // Prime the counters
                        readCounter.NextValue();
                        writeCounter.NextValue();
                        queueCounter.NextValue();
                        Thread.Sleep(100);

                        for (int i = 0; i < SampleCount && !ct.IsCancellationRequested; i++)
                        {
                            var readMs = readCounter.NextValue() * 1000; // Convert to ms
                            var writeMs = writeCounter.NextValue() * 1000;
                            var queue = queueCounter.NextValue();

                            // Validate - reject invalid values
                            if (!double.IsNaN(readMs) && !double.IsInfinity(readMs) && readMs >= 0 && readMs < 10000)
                                readLatencies.Add(readMs);

                            if (!double.IsNaN(writeMs) && !double.IsInfinity(writeMs) && writeMs >= 0 && writeMs < 10000)
                                writeLatencies.Add(writeMs);

                            if (!double.IsNaN(queue) && !double.IsInfinity(queue) && queue >= 0 && queue < 1000)
                                queueDepths.Add(queue);

                            Thread.Sleep(SampleIntervalMs);
                        }

                        return true;
                    }
                    catch (Exception ex)
                    {
                        SignalsLogger.LogWarning(Name, $"PerfCounter failed: {ex.Message}");
                        return false;
                    }
                }, ct);

                if (!countersAvailable || (readLatencies.Count == 0 && writeLatencies.Count == 0))
                {
                    return SignalResult.Unavailable(Name, "perf_counter_not_supported", "PerfCounter_PhysicalDisk");
                }

                // Calculate percentiles
                if (readLatencies.Count > 0)
                {
                    readLatencies.Sort();
                    result.ReadLatencyMsP50 = GetPercentile(readLatencies, 50);
                    result.ReadLatencyMsP95 = GetPercentile(readLatencies, 95);
                    result.ReadLatencyMsP99 = GetPercentile(readLatencies, 99);
                }

                if (writeLatencies.Count > 0)
                {
                    writeLatencies.Sort();
                    result.WriteLatencyMsP50 = GetPercentile(writeLatencies, 50);
                    result.WriteLatencyMsP95 = GetPercentile(writeLatencies, 95);
                    result.WriteLatencyMsP99 = GetPercentile(writeLatencies, 99);
                }

                if (queueDepths.Count > 0)
                {
                    queueDepths.Sort();
                    result.QueueDepthAvg = Math.Round(queueDepths.Average(), 2);
                    result.QueueDepthP95 = GetPercentile(queueDepths, 95);
                }

                result.SampleCount = SampleCount;
                result.WindowSeconds = SampleCount * SampleIntervalMs / 1000.0;

                return SignalResult.Ok(Name, result, "PerfCounter_PhysicalDisk",
                    $"readP95={result.ReadLatencyMsP95:F2}ms, writeP95={result.WriteLatencyMsP95:F2}ms");
            }
            catch (Exception ex)
            {
                SignalsLogger.LogException(Name, ex);
                return SignalResult.Unavailable(Name, $"error: {ex.Message}", "PerfCounter_PhysicalDisk");
            }
        }

        private double GetPercentile(List<double> sortedValues, int percentile)
        {
            if (sortedValues.Count == 0) return 0;
            int index = (int)Math.Ceiling(sortedValues.Count * percentile / 100.0) - 1;
            return Math.Round(sortedValues[Math.Max(0, Math.Min(index, sortedValues.Count - 1))], 2);
        }
    }

    public class IoLatencyResult
    {
        public double ReadLatencyMsP50 { get; set; }
        public double ReadLatencyMsP95 { get; set; }
        public double ReadLatencyMsP99 { get; set; }
        public double WriteLatencyMsP50 { get; set; }
        public double WriteLatencyMsP95 { get; set; }
        public double WriteLatencyMsP99 { get; set; }
        public double QueueDepthAvg { get; set; }
        public double QueueDepthP95 { get; set; }
        public int SampleCount { get; set; }
        public double WindowSeconds { get; set; }
    }
}
