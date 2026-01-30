using System;
using System.IO;
using System.Linq;
using System.Text;

namespace PCDiagnosticPro.DiagnosticsSignals
{
    /// <summary>
    /// Centralized logger for diagnostic signals.
    /// Writes to %TEMP%\PCDiagnosticPro_signals.log
    /// </summary>
    public static class SignalsLogger
    {
        private static readonly object _lock = new();
        private static readonly string _logPath;
        private static bool _initialized = false;
        
        static SignalsLogger()
        {
            var tempPath = Environment.GetEnvironmentVariable("TEMP") 
                ?? Path.GetTempPath();
            _logPath = Path.Combine(tempPath, "PCDiagnosticPro_signals.log");
        }
        
        public static string LogPath => _logPath;
        
        /// <summary>Initialize log file (clears previous)</summary>
        public static void Initialize()
        {
            lock (_lock)
            {
                try
                {
                    var header = $"=== PC DIAGNOSTIC PRO — SIGNALS LOG ==={Environment.NewLine}" +
                                 $"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                                 $"========================================{Environment.NewLine}";
                    File.WriteAllText(_logPath, header, Encoding.UTF8);
                    _initialized = true;
                }
                catch { /* Silent fail */ }
            }
        }
        
        /// <summary>Log collector start</summary>
        public static void LogStart(string collectorName)
        {
            Log($"[{collectorName}] START");
        }
        
        /// <summary>Log collector success</summary>
        public static void LogSuccess(string collectorName, long durationMs, string? details = null)
        {
            var msg = $"[{collectorName}] SUCCESS ({durationMs}ms)";
            if (!string.IsNullOrEmpty(details))
                msg += $" — {details}";
            Log(msg);
        }
        
        /// <summary>Log collector failure</summary>
        public static void LogFail(string collectorName, long durationMs, string reason)
        {
            Log($"[{collectorName}] FAIL ({durationMs}ms) — {reason}");
        }
        
        /// <summary>Log exception with details</summary>
        public static void LogException(string collectorName, Exception ex)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{collectorName}] EXCEPTION: {ex.GetType().Name}");
            sb.AppendLine($"  Message: {ex.Message}");
            if (ex.HResult != 0)
                sb.AppendLine($"  HResult: 0x{ex.HResult:X8}");
            if (ex.StackTrace != null)
            {
                var stack = ex.StackTrace.Split('\n');
                foreach (var line in stack.Take(5))
                    sb.AppendLine($"  {line.Trim()}");
                if (stack.Length > 5)
                    sb.AppendLine($"  ... {stack.Length - 5} more lines");
            }
            Log(sb.ToString().TrimEnd());
        }
        
        /// <summary>Log timeout</summary>
        public static void LogTimeout(string collectorName, TimeSpan timeout)
        {
            Log($"[{collectorName}] TIMEOUT after {timeout.TotalMilliseconds:F0}ms");
        }
        
        /// <summary>Log info message</summary>
        public static void LogInfo(string collectorName, string message)
        {
            Log($"[{collectorName}] INFO: {message}");
        }
        
        /// <summary>Log warning</summary>
        public static void LogWarning(string collectorName, string message)
        {
            Log($"[{collectorName}] WARN: {message}");
        }
        
        private static void Log(string message)
        {
            lock (_lock)
            {
                try
                {
                    if (!_initialized)
                        Initialize();
                    
                    var line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
                    File.AppendAllText(_logPath, line, Encoding.UTF8);
                }
                catch { /* Silent fail */ }
            }
        }
    }
}
