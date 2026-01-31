using System;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.DiagnosticsSignals.Collectors
{
    /// <summary>
    /// PHASE 4.5: DPC/ISR Latency Collector
    /// Requires ETW TraceEvent - if not available, returns unavailable with proper reason.
    /// This is a stub that marks itself unavailable since ETW requires additional dependencies.
    /// </summary>
    public class DpcIsrCollector : ISignalCollector
    {
        public string Name => "dpcIsr";
        public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(15);
        public int Priority => 8;

        public Task<SignalResult> CollectAsync(CancellationToken ct)
        {
            try
            {
                // Check if ETW TraceEvent is available
                // Since Microsoft.Diagnostics.Tracing.TraceEvent is not included,
                // we mark this as unavailable with proper reason
                
                bool etwAvailable = CheckEtwAvailability();

                if (!etwAvailable)
                {
                    SignalsLogger.LogInfo(Name, "ETW TraceEvent not available - DPC/ISR latency collection disabled");
                    return Task.FromResult(SignalResult.Unavailable(
                        Name,
                        "etw_required_for_latency",
                        "ETW_TraceEvent"
                    ));
                }

                // If ETW were available, we would collect DPC/ISR latency here
                // For now, return unavailable
                return Task.FromResult(SignalResult.Unavailable(
                    Name,
                    "etw_required_for_latency",
                    "ETW_TraceEvent"
                ));
            }
            catch (Exception ex)
            {
                SignalsLogger.LogException(Name, ex);
                return Task.FromResult(SignalResult.Unavailable(
                    Name,
                    $"error: {ex.Message}",
                    "ETW_TraceEvent"
                ));
            }
        }

        private bool CheckEtwAvailability()
        {
            try
            {
                // Check if the TraceEvent assembly is loaded
                var assembly = Type.GetType("Microsoft.Diagnostics.Tracing.TraceEventSession, Microsoft.Diagnostics.Tracing.TraceEvent");
                return assembly != null;
            }
            catch
            {
                return false;
            }
        }
    }

    // Result structure for when ETW becomes available
    public class DpcIsrResult
    {
        public double DpcLatencyMsP95 { get; set; }
        public double DpcLatencyMsMax { get; set; }
        public double IsrLatencyMsP95 { get; set; }
        public double IsrLatencyMsMax { get; set; }
        public double WindowSeconds { get; set; }
        public string Source { get; set; } = "ETW";
    }
}
