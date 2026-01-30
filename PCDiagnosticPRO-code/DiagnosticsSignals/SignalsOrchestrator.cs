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
            _collectors = new List<ISignalCollector>
            {
                new WheaCollector(),
                new GpuRootCauseCollector(),
                new CpuThrottleCollector(),
                new NetworkQualityCollector(),
                new MemoryPressureCollector(),
                new DriverStabilityCollector(),
                new BootPerformanceCollector(),
                new PowerLimitsCollector(),
                // IoLatencyCollector and DpcIsrCollector require ETW - optional
            };
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
        
        private async Task<SignalResult> RunCollectorAsync(ISignalCollector collector, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            SignalsLogger.LogStart(collector.Name);
            
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
                }
                else
                {
                    SignalsLogger.LogFail(collector.Name, sw.ElapsedMilliseconds, 
                        result.Reason ?? "unknown");
                }
                
                return result;
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                SignalsLogger.LogTimeout(collector.Name, collector.DefaultTimeout);
                return SignalResult.Unavailable(collector.Name, "timeout", "collector");
            }
            catch (Exception ex)
            {
                sw.Stop();
                SignalsLogger.LogException(collector.Name, ex);
                return SignalResult.Unavailable(collector.Name, $"exception: {ex.Message}", "collector");
            }
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
