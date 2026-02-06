using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCDiagnosticPro.DiagnosticsSignals.Collectors;

namespace PCDiagnosticPro.DiagnosticsSignals
{
    /// <summary>
    /// Orchestrates all diagnostic signal collectors with individual timeouts.
    /// </summary>
    public class SignalsOrchestrator
    {
        private readonly List<ISignalCollector> _collectors;
        
        public SignalsOrchestrator()
        {
            // 11 signal collectors as per PHASE 4 requirements + Internet speed test
            _collectors = new List<ISignalCollector>
            {
                new WheaCollector(),              // 4.1 WHEA hardware events
                new GpuRootCauseCollector(),      // 4.2 GPU TDR + PerfCap
                new IoLatencyCollector(),         // 4.3 IO Latency percentiles
                new CpuThrottleCollector(),       // 4.4 CPU Throttle flags
                new DpcIsrCollector(),            // 4.5 DPC/ISR latency (requires ETW)
                new MemoryPressureCollector(),    // 4.6 Memory pressure - Hard faults
                new PowerLimitsCollector(),       // 4.7 Power limits
                new DriverStabilityCollector(),   // 4.8 Driver stability
                new BootPerformanceCollector(),   // 4.9 Boot performance
                new NetworkQualityCollector(),    // 4.10 Network quality (LOCAL ONLY)
                new InternetSpeedTestCollector(), // FIX 7: Optional Internet speed test
            };
        }
        
        /// <summary>
        /// Enable/disable external network tests (for Internet speed test)
        /// </summary>
        public void SetAllowExternalNetworkTests(bool allow)
        {
            InternetSpeedTestCollector.AllowExternalNetworkTests = allow;
        }
        
        /// <summary>
        /// Run all collectors and return combined results.
        /// Each collector has its own timeout.
        /// </summary>
        public async Task<DiagnosticSignalsResult> CollectAllAsync(CancellationToken ct = default)
        {
            SignalsLogger.Initialize();
            SignalsLogger.LogInfo("Orchestrator", $"Starting {_collectors.Count} collectors");
            
            var result = new DiagnosticSignalsResult
            {
                StartTime = DateTime.UtcNow
            };
            
            var overallSw = Stopwatch.StartNew();
            
            // Run collectors in parallel with individual timeouts
            var tasks = _collectors.Select(collector => RunCollectorAsync(collector, ct)).ToList();
            var signalResults = await Task.WhenAll(tasks);
            
            foreach (var signalResult in signalResults)
            {
                result.Signals[signalResult.Name] = signalResult;
            }
            
            overallSw.Stop();
            result.EndTime = DateTime.UtcNow;
            result.TotalDurationMs = overallSw.ElapsedMilliseconds;
            
            // Calculate summary stats
            result.SuccessCount = signalResults.Count(s => s.Available);
            result.FailCount = signalResults.Count(s => !s.Available);
            
            SignalsLogger.LogInfo("Orchestrator", 
                $"Completed: {result.SuccessCount} success, {result.FailCount} fail, {result.TotalDurationMs}ms total");
            
            return result;
        }
        
        /// <summary>Collecteurs critiques : un retry automatique en cas d'échec (WHEA, DriverStability, CpuThrottle).</summary>
        private static readonly HashSet<string> CriticalCollectors = new(StringComparer.OrdinalIgnoreCase)
        {
            "whea", "driverStability", "cpuThrottle"
        };

        private async Task<SignalResult> RunCollectorAsync(ISignalCollector collector, CancellationToken ct)
        {
            var maxAttempts = CriticalCollectors.Contains(collector.Name) ? 2 : 1;
            
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var sw = Stopwatch.StartNew();
                if (attempt == 1) SignalsLogger.LogStart(collector.Name);
                else SignalsLogger.LogInfo(collector.Name, $"Retry attempt {attempt}/{maxAttempts}");
                
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(collector.DefaultTimeout);
                    
                    var result = await collector.CollectAsync(cts.Token);
                    sw.Stop();
                    result.DurationMs = sw.ElapsedMilliseconds;
                    
                    if (result.Available)
                    {
                        SignalsLogger.LogSuccess(collector.Name, sw.ElapsedMilliseconds, 
                            result.Notes ?? $"quality={result.Quality}");
                        return result;
                    }
                    else
                    {
                        SignalsLogger.LogFail(collector.Name, sw.ElapsedMilliseconds, 
                            result.Reason ?? "unknown");
                        if (attempt < maxAttempts)
                        {
                            await Task.Delay(500, ct); // Brief pause before retry
                            continue;
                        }
                        return result;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < maxAttempts)
                {
                    sw.Stop();
                    SignalsLogger.LogInfo(collector.Name, $"Timeout on attempt {attempt}, will retry");
                    await Task.Delay(300, ct);
                    continue;
                }
                catch (OperationCanceledException)
                {
                    sw.Stop();
                    SignalsLogger.LogTimeout(collector.Name, collector.DefaultTimeout);
                    return SignalResult.Unavailable(collector.Name, 
                        maxAttempts > 1 ? $"timeout_after_{maxAttempts}_attempts" : "timeout", "collector");
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    sw.Stop();
                    SignalsLogger.LogInfo(collector.Name, $"Exception on attempt {attempt}: {ex.Message}, will retry");
                    await Task.Delay(300, ct);
                    continue;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    SignalsLogger.LogException(collector.Name, ex);
                    return SignalResult.Unavailable(collector.Name, 
                        maxAttempts > 1 ? $"exception_after_{maxAttempts}_attempts: {ex.Message}" : $"exception: {ex.Message}", "collector");
                }
            }
            
            // Shouldn't reach here, but safety net
            return SignalResult.Unavailable(collector.Name, "exhausted_retries", "collector");
        }
    }
    
    /// <summary>
    /// Combined result from all signal collectors.
    /// </summary>
    public class DiagnosticSignalsResult
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public long TotalDurationMs { get; set; }
        public int SuccessCount { get; set; }
        public int FailCount { get; set; }
        public Dictionary<string, SignalResult> Signals { get; set; } = new();
    }
}
