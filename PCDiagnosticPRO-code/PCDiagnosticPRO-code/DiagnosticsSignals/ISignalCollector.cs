using System;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.DiagnosticsSignals
{
    /// <summary>
    /// Interface for all diagnostic signal collectors.
    /// Each collector is independent and has its own timeout.
    /// </summary>
    public interface ISignalCollector
    {
        /// <summary>Name of the signal (used as JSON key)</summary>
        string Name { get; }
        
        /// <summary>Default timeout for this collector</summary>
        TimeSpan DefaultTimeout { get; }
        
        /// <summary>Priority for scoring (higher = more important)</summary>
        int Priority { get; }
        
        /// <summary>
        /// Collect the diagnostic signal asynchronously.
        /// Must handle all exceptions internally and return a valid SignalResult.
        /// </summary>
        Task<SignalResult> CollectAsync(CancellationToken ct);
    }
}
