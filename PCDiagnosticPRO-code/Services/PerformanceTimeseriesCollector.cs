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
                // Warm up rate counters (first NextValue often 0)
                await Task.Delay(500, ct).ConfigureAwait(false);

                for (int i = 0; i < intervalSeconds && !ct.IsCancellationRequested; i++)
                {
                    SampleOnce(
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
            List<double> cpu, List<double> memAvail, List<double> memCommitted,
            List<double> diskRead, List<double> diskWrite, List<double> diskQueue,
            List<double> network, List<double> gpu)
        {
            try
            {
                SampleCpu(cpu);
                SampleMemory(memAvail, memCommitted);
                SampleDisk(diskRead, diskWrite, diskQueue);
                SampleNetwork(network);
                SampleGpu(gpu);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[PerfTimeseries] Sample error: {ex.Message}");
            }
        }

        private static void SampleCpu(List<double> list)
        {
            try
            {
                using var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                var v = counter.NextValue();
                if (v >= 0 && v <= 100) list.Add(v);
            }
            catch { /* ignore */ }
        }

        private static void SampleMemory(List<double> availList, List<double> committedList)
        {
            try
            {
                using var availCounter = new PerformanceCounter("Memory", "Available MBytes", "", true);
                var avail = availCounter.NextValue();
                if (avail >= 0) availList.Add(avail);
            }
            catch { /* ignore */ }
            try
            {
                using var commitCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use", "", true);
                var pct = commitCounter.NextValue();
                if (pct >= 0 && pct <= 100) committedList.Add(pct);
            }
            catch { /* ignore */ }
        }

        private static void SampleDisk(List<double> readList, List<double> writeList, List<double> queueList)
        {
            try
            {
                using var readCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", true);
                using var writeCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);
                using var queueCounter = new PerformanceCounter("PhysicalDisk", "Current Disk Queue Length", "_Total", true);
                var r = readCounter.NextValue();
                var w = writeCounter.NextValue();
                var q = queueCounter.NextValue();
                if (r >= 0) readList.Add(r);
                if (w >= 0) writeList.Add(w);
                if (q >= 0 && q < 1000) queueList.Add(q);
            }
            catch { /* ignore */ }
        }

        private static void SampleNetwork(List<double> list)
        {
            try
            {
                var category = new PerformanceCounterCategory("Network Interface");
                var instances = category.GetInstanceNames();
                double total = 0;
                foreach (var instance in instances)
                {
                    try
                    {
                        using var counter = new PerformanceCounter("Network Interface", "Bytes Total/sec", instance, true);
                        var v = counter.NextValue();
                        if (v >= 0) total += v;
                    }
                    catch { /* skip */ }
                }
                if (total >= 0) list.Add(total);
            }
            catch { /* ignore */ }
        }

        private static void SampleGpu(List<double> list)
        {
            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                var instances = category.GetInstanceNames();
                double maxUtil = 0;
                foreach (var instance in instances)
                {
                    if (!instance.Contains("_Total")) continue;
                    try
                    {
                        using var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
                        var v = counter.NextValue();
                        if (v >= 0 && v <= 100) maxUtil = Math.Max(maxUtil, v);
                    }
                    catch { /* skip */ }
                }
                if (maxUtil >= 0) list.Add(maxUtil);
            }
            catch { /* GPU counters often unavailable */ }
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
    }
}
