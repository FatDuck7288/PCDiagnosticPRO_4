using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Samples performance counters over a time window (e.g. 10-30s) and produces min/max/avg aggregates.
    /// </summary>
    public static class PerformanceTimeseriesCollector
    {
        public const int DefaultIntervalSeconds = 15;
        public const int SampleIntervalMs = 1000;

        /// <summary>
        /// Collect samples over intervalSeconds (1 sample per second), then compute min/max/avg.
        /// </summary>
        public static async Task<PerformanceTimeseriesSummary?> CollectAsync(
            int intervalSeconds = DefaultIntervalSeconds,
            CancellationToken ct = default)
        {
            if (intervalSeconds < 5) intervalSeconds = 5;
            if (intervalSeconds > 60) intervalSeconds = 60;

            var cpuSamples = new List<double>();
            var memAvailSamples = new List<double>();
            var memCommittedSamples = new List<double>();
            var diskReadSamples = new List<double>();
            var diskWriteSamples = new List<double>();
            var diskQueueSamples = new List<double>();
            var networkSamples = new List<double>();
            var gpuSamples = new List<double>();

            try
            {
                using var counters = CounterScope.Create();
                counters.Warmup();

                // Warm up rate counters (first NextValue often 0)
                await Task.Delay(500, ct).ConfigureAwait(false);

                for (int i = 0; i < intervalSeconds && !ct.IsCancellationRequested; i++)
                {
                    SampleOnce(
                        counters,
                        cpuSamples, memAvailSamples, memCommittedSamples,
                        diskReadSamples, diskWriteSamples, diskQueueSamples,
                        networkSamples, gpuSamples);
                    if (i < intervalSeconds - 1)
                        await Task.Delay(SampleIntervalMs, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                App.LogMessage("[PerfTimeseries] Collection cancelled");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[PerfTimeseries] Error: {ex.Message}");
            }

            int sampleCount = Math.Max(1, Math.Max(cpuSamples.Count, Math.Max(memAvailSamples.Count, diskReadSamples.Count)));
            if (sampleCount == 0)
                return null;

            var summary = new PerformanceTimeseriesSummary
            {
                IntervalSeconds = intervalSeconds,
                SampleCount = sampleCount,
                CpuPercent = ToMinMaxAvg(cpuSamples),
                MemoryAvailableMB = ToMinMaxAvg(memAvailSamples),
                MemoryCommittedPercent = ToMinMaxAvg(memCommittedSamples),
                DiskReadBytesPerSec = ToMinMaxAvg(diskReadSamples),
                DiskWriteBytesPerSec = ToMinMaxAvg(diskWriteSamples),
                DiskQueueLength = ToMinMaxAvg(diskQueueSamples),
                NetworkBytesPerSec = ToMinMaxAvg(networkSamples)
            };
            var gpuAgg = ToMinMaxAvg(gpuSamples);
            if (gpuAgg != null)
                summary.GpuUtilizationPercent = gpuAgg;

            App.LogMessage($"[PerfTimeseries] Collected {sampleCount} samples over {intervalSeconds}s");
            return summary;
        }

        private static void SampleOnce(
            CounterScope counters,
            List<double> cpu, List<double> memAvail, List<double> memCommitted,
            List<double> diskRead, List<double> diskWrite, List<double> diskQueue,
            List<double> network, List<double> gpu)
        {
            try
            {
                SampleCpu(counters, cpu);
                SampleMemory(counters, memAvail, memCommitted);
                SampleDisk(counters, diskRead, diskWrite, diskQueue);
                SampleNetwork(counters, network);
                SampleGpu(counters, gpu);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[PerfTimeseries] Sample error: {ex.Message}");
            }
        }

        private static void SampleCpu(CounterScope counters, List<double> list)
        {
            var v = counters.Read(counters.CpuCounter);
            if (v.HasValue && v.Value >= 0 && v.Value <= 100) list.Add(v.Value);
        }

        private static void SampleMemory(CounterScope counters, List<double> availList, List<double> committedList)
        {
            var avail = counters.Read(counters.AvailableMemoryCounter);
            if (avail.HasValue && avail.Value >= 0) availList.Add(avail.Value);

            var pct = counters.Read(counters.CommittedMemoryCounter);
            if (pct.HasValue && pct.Value >= 0 && pct.Value <= 100) committedList.Add(pct.Value);
        }

        private static void SampleDisk(CounterScope counters, List<double> readList, List<double> writeList, List<double> queueList)
        {
            var r = counters.Read(counters.DiskReadCounter);
            var w = counters.Read(counters.DiskWriteCounter);
            var q = counters.Read(counters.DiskQueueCounter);

            if (r.HasValue && r.Value >= 0) readList.Add(r.Value);
            if (w.HasValue && w.Value >= 0) writeList.Add(w.Value);
            if (q.HasValue && q.Value >= 0 && q.Value < 1000) queueList.Add(q.Value);
        }

        private static void SampleNetwork(CounterScope counters, List<double> list)
        {
            double total = 0;
            foreach (var counter in counters.NetworkCounters)
            {
                var value = counters.Read(counter);
                if (value.HasValue && value.Value >= 0)
                    total += value.Value;
            }

            if (total >= 0) list.Add(total);
        }

        private static void SampleGpu(CounterScope counters, List<double> list)
        {
            if (counters.GpuCounters.Count == 0)
                return;

            double maxUtil = 0;
            foreach (var counter in counters.GpuCounters)
            {
                var value = counters.Read(counter);
                if (value.HasValue && value.Value >= 0 && value.Value <= 100)
                    maxUtil = Math.Max(maxUtil, value.Value);
            }

            if (maxUtil >= 0) list.Add(maxUtil);
        }

        private static MinMaxAvg? ToMinMaxAvg(List<double> values)
        {
            if (values == null || values.Count == 0) return null;
            double min = values[0], max = values[0], sum = 0;
            foreach (var v in values)
            {
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
            }
            return new MinMaxAvg
            {
                Min = Math.Round(min, 2),
                Max = Math.Round(max, 2),
                Avg = Math.Round(sum / values.Count, 2)
            };
        }

        private sealed class CounterScope : IDisposable
        {
            public PerformanceCounter? CpuCounter { get; private set; }
            public PerformanceCounter? AvailableMemoryCounter { get; private set; }
            public PerformanceCounter? CommittedMemoryCounter { get; private set; }
            public PerformanceCounter? DiskReadCounter { get; private set; }
            public PerformanceCounter? DiskWriteCounter { get; private set; }
            public PerformanceCounter? DiskQueueCounter { get; private set; }
            public List<PerformanceCounter> NetworkCounters { get; } = new();
            public List<PerformanceCounter> GpuCounters { get; } = new();

            private readonly List<PerformanceCounter> _allCounters = new();

            public static CounterScope Create()
            {
                var scope = new CounterScope();
                scope.Initialize();
                return scope;
            }

            public void Warmup()
            {
                foreach (var counter in _allCounters)
                {
                    try
                    {
                        _ = counter.NextValue();
                    }
                    catch
                    {
                        // Ignore warmup errors per counter.
                    }
                }
            }

            public float? Read(PerformanceCounter? counter)
            {
                if (counter is null)
                    return null;

                try
                {
                    return counter.NextValue();
                }
                catch
                {
                    return null;
                }
            }

            public void Dispose()
            {
                foreach (var counter in _allCounters)
                {
                    try
                    {
                        counter.Dispose();
                    }
                    catch
                    {
                        // Ignore dispose errors.
                    }
                }
                _allCounters.Clear();
                NetworkCounters.Clear();
                GpuCounters.Clear();
            }

            private void Initialize()
            {
                CpuCounter = AddCounterSafe("Processor", "% Processor Time", "_Total");
                AvailableMemoryCounter = AddCounterSafe("Memory", "Available MBytes", "");
                CommittedMemoryCounter = AddCounterSafe("Memory", "% Committed Bytes In Use", "");
                DiskReadCounter = AddCounterSafe("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                DiskWriteCounter = AddCounterSafe("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                DiskQueueCounter = AddCounterSafe("PhysicalDisk", "Current Disk Queue Length", "_Total");

                try
                {
                    var networkCategory = new PerformanceCounterCategory("Network Interface");
                    foreach (var instance in networkCategory.GetInstanceNames())
                    {
                        var counter = AddCounterSafe("Network Interface", "Bytes Total/sec", instance);
                        if (counter != null)
                            NetworkCounters.Add(counter);
                    }
                }
                catch
                {
                    // Optional category may not be available.
                }

                try
                {
                    var gpuCategory = new PerformanceCounterCategory("GPU Engine");
                    foreach (var instance in gpuCategory.GetInstanceNames())
                    {
                        if (!instance.Contains("_Total", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var counter = AddCounterSafe("GPU Engine", "Utilization Percentage", instance);
                        if (counter != null)
                            GpuCounters.Add(counter);
                    }
                }
                catch
                {
                    // GPU counters are often unavailable; ignore.
                }
            }

            private PerformanceCounter? AddCounterSafe(string category, string counterName, string instanceName)
            {
                try
                {
                    var counter = new PerformanceCounter(category, counterName, instanceName, true);
                    _allCounters.Add(counter);
                    return counter;
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}
