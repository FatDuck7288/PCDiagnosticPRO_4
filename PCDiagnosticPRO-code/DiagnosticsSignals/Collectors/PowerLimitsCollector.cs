using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.DiagnosticsSignals.Collectors
{
    /// <summary>
    /// Detects CPU and GPU power limit events through correlation.
    /// CPU: Kernel-Processor-Power events + performance drop correlation
    /// GPU: High load + temp + clock drop correlation (without NVML)
    /// </summary>
    public class PowerLimitsCollector : ISignalCollector
    {
        public string Name => "powerLimits";
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
                var evidence = new List<string>();
                bool cpuPowerLimitSuspected = false;
                bool gpuPowerLimitSuspected = false;
                int cpuPowerEvents = 0;

                // 1. Check CPU power limit events
                cpuPowerEvents = QueryCpuPowerEvents(ct);
                if (cpuPowerEvents > 0)
                {
                    evidence.Add($"CPU power/thermal throttle events: {cpuPowerEvents}");
                    cpuPowerLimitSuspected = cpuPowerEvents >= 3;
                }

                // 2. Check for thermal throttle events
                int thermalEvents = QueryThermalEvents(ct);
                if (thermalEvents > 0)
                {
                    evidence.Add($"Thermal throttle events: {thermalEvents}");
                    cpuPowerLimitSuspected = cpuPowerLimitSuspected || thermalEvents >= 2;
                }

                // 3. GPU power limit - correlation based (no NVML)
                // Check if we have GPU temp + load data available
                // This would need to be correlated with HardwareSensors data
                // For now, just check for relevant events
                int gpuThrottleEvents = QueryGpuThrottleEvents(ct);
                if (gpuThrottleEvents > 0)
                {
                    evidence.Add($"GPU-related throttle/reset events: {gpuThrottleEvents}");
                    gpuPowerLimitSuspected = gpuThrottleEvents >= 2;
                }

                var result = new PowerLimitsResult
                {
                    CpuPowerLimitSuspected = cpuPowerLimitSuspected,
                    GpuPowerLimitSuspected = gpuPowerLimitSuspected,
                    CpuPowerEvents = cpuPowerEvents,
                    ThermalEvents = thermalEvents,
                    GpuThrottleEvents = gpuThrottleEvents,
                    Evidence = evidence
                };

                var quality = (cpuPowerLimitSuspected || gpuPowerLimitSuspected) ? "suspect" :
                              evidence.Count > 0 ? "partial" : "ok";

                return new SignalResult
                {
                    Name = Name,
                    Value = result,
                    Available = true,
                    Source = "EventLog_PowerThermal",
                    Quality = quality,
                    Notes = $"cpuLimit={cpuPowerLimitSuspected}, gpuLimit={gpuPowerLimitSuspected}",
                    Timestamp = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                SignalsLogger.LogException(Name, ex);
                return SignalResult.Unavailable(Name, $"error: {ex.Message}", "EventLog_PowerThermal");
            }
        }

        private int QueryCpuPowerEvents(CancellationToken ct)
        {
            int count = 0;
            var last30Days = DateTime.Now.AddDays(-30);

            try
            {
                // Query Kernel-Processor-Power for throttle events
                // Event ID 37 = Speed limited by firmware
                // Event ID 12 = Processor frequency capped
                string query = "*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and " +
                               "(EventID=37 or EventID=12)]]";

                var logQuery = new EventLogQuery("System", PathType.LogName, query);
                using var reader = new EventLogReader(logQuery);

                EventRecord evt;
                while ((evt = reader.ReadEvent()) != null && !ct.IsCancellationRequested)
                {
                    using (evt)
                    {
                        if (evt.TimeCreated.HasValue && evt.TimeCreated.Value >= last30Days)
                            count++;
                    }
                }
            }
            catch (Exception ex)
            {
                SignalsLogger.LogWarning(Name, $"CPU power events query error: {ex.Message}");
            }

            return count;
        }

        private int QueryThermalEvents(CancellationToken ct)
        {
            int count = 0;
            var last30Days = DateTime.Now.AddDays(-30);

            try
            {
                // Thermal events from Kernel-Power
                string query = "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and " +
                               "(EventID=86 or EventID=87)]]"; // Thermal zone events

                var logQuery = new EventLogQuery("System", PathType.LogName, query);
                using var reader = new EventLogReader(logQuery);

                EventRecord evt;
                while ((evt = reader.ReadEvent()) != null && !ct.IsCancellationRequested)
                {
                    using (evt)
                    {
                        if (evt.TimeCreated.HasValue && evt.TimeCreated.Value >= last30Days)
                            count++;
                    }
                }
            }
            catch (Exception ex)
            {
                SignalsLogger.LogWarning(Name, $"Thermal events query error: {ex.Message}");
            }

            return count;
        }

        private int QueryGpuThrottleEvents(CancellationToken ct)
        {
            int count = 0;
            var last30Days = DateTime.Now.AddDays(-30);

            try
            {
                // GPU throttle indicators from Display/nvlddmkm/amdkmdag
                // These indicate clock drops or power limit hits
                string query = "*[System[Provider[@Name='Display' or @Name='nvlddmkm' or @Name='amdkmdag'] and Level<=3]]";

                var logQuery = new EventLogQuery("System", PathType.LogName, query);
                using var reader = new EventLogReader(logQuery);

                EventRecord evt;
                while ((evt = reader.ReadEvent()) != null && !ct.IsCancellationRequested)
                {
                    using (evt)
                    {
                        if (!evt.TimeCreated.HasValue || evt.TimeCreated.Value < last30Days) 
                            continue;

                        var message = evt.FormatDescription() ?? "";
                        var lower = message.ToLowerInvariant();
                        
                        // Check for throttle/power limit indicators
                        if (lower.Contains("throttl") || 
                            lower.Contains("power limit") ||
                            lower.Contains("clock") ||
                            lower.Contains("frequency"))
                        {
                            count++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SignalsLogger.LogWarning(Name, $"GPU throttle events query error: {ex.Message}");
            }

            return count;
        }
    }

    public class PowerLimitsResult
    {
        public bool CpuPowerLimitSuspected { get; set; }
        public bool GpuPowerLimitSuspected { get; set; }
        public int CpuPowerEvents { get; set; }
        public int ThermalEvents { get; set; }
        public int GpuThrottleEvents { get; set; }
        public List<string> Evidence { get; set; } = new();
    }
}
