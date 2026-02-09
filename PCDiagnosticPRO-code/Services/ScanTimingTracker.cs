using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Records per-phase scan timing and writes to %TEMP%\PCDiagnosticPro_timing.log.
    /// Used for profiling bottlenecks (P0 audit).
    /// </summary>
    public sealed class ScanTimingTracker
    {
        private readonly string _logPath;
        private readonly List<PhaseRecord> _records = new();
        private readonly Dictionary<string, (long startMs, string source)> _active = new(StringComparer.OrdinalIgnoreCase);

        public ScanTimingTracker()
        {
            _logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_timing.log");
        }

        /// <summary>Start a phase (source = PS | C#).</summary>
        public void StartPhase(string phaseName, string source = "C#")
        {
            if (string.IsNullOrWhiteSpace(phaseName)) return;
            var now = GetTimestampMs();
            lock (_active)
            {
                _active[phaseName] = (now, source ?? "C#");
            }
        }

        /// <summary>End a phase and record duration.</summary>
        public void EndPhase(string phaseName, bool success = true)
        {
            if (string.IsNullOrWhiteSpace(phaseName)) return;
            var endMs = GetTimestampMs();
            lock (_active)
            {
                if (_active.TryGetValue(phaseName, out var start))
                {
                    _active.Remove(phaseName);
                    _records.Add(new PhaseRecord
                    {
                        PhaseName = phaseName,
                        Source = start.source,
                        StartMs = start.startMs,
                        EndMs = endMs,
                        DurationMs = endMs - start.startMs,
                        Success = success
                    });
                }
            }
        }

        /// <summary>Write all records to the log file and clear.</summary>
        public void FlushToLog()
        {
            PhaseRecord[] copy;
            lock (_active)
            {
                foreach (var kvp in _active)
                    _records.Add(new PhaseRecord
                    {
                        PhaseName = kvp.Key,
                        Source = kvp.Value.source,
                        StartMs = kvp.Value.startMs,
                        EndMs = GetTimestampMs(),
                        DurationMs = -1,
                        Success = false
                    });
                _active.Clear();
                copy = _records.ToArray();
                _records.Clear();
            }

            if (copy.Length == 0) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"=== PCDiagnosticPro timing — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                sb.AppendLine("Phase|Source|StartMs|EndMs|DurationMs|Success");
                foreach (var r in copy)
                    sb.AppendLine($"{r.PhaseName}|{r.Source}|{r.StartMs}|{r.EndMs}|{r.DurationMs}|{(r.Success ? "1" : "0")}");
                sb.AppendLine();

                File.AppendAllText(_logPath, sb.ToString(), Encoding.UTF8);
                App.LogMessage($"[ScanTiming] Written {copy.Length} phase(s) to {_logPath}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ScanTiming] Failed to write log: {ex.Message}");
            }
        }

        private static long GetTimestampMs()
        {
            return Environment.TickCount64;
        }

        private class PhaseRecord
        {
            public string PhaseName { get; set; } = "";
            public string Source { get; set; } = "";
            public long StartMs { get; set; }
            public long EndMs { get; set; }
            public long DurationMs { get; set; }
            public bool Success { get; set; }
        }
    }
}
