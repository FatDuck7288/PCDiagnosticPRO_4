using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Records per-phase scan timing and writes to %TEMP%\PCDiagnosticPro_timing.log.
    /// Used for profiling bottlenecks (P0 audit).
    /// </summary>
    public sealed class ScanTimingTracker
    {
        private readonly string _logPath;
        private readonly string _ndjsonPath;
        private readonly List<PhaseRecord> _records = new();
        private readonly Dictionary<string, (long startMs, string source)> _active = new(StringComparer.OrdinalIgnoreCase);
        private string _runId = string.Empty;

        public ScanTimingTracker(string? runId = null)
        {
            _logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_timing.log");
            _ndjsonPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_timing.ndjson");
            _runId = runId ?? string.Empty;
        }

        public void SetRunId(string? runId)
        {
            _runId = runId ?? string.Empty;
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

        public IReadOnlyList<ScanTimingEntry> GetSnapshot()
        {
            lock (_active)
            {
                var snapshot = new List<ScanTimingEntry>(_records.Count + _active.Count);
                foreach (var r in _records)
                {
                    snapshot.Add(new ScanTimingEntry
                    {
                        PhaseName = r.PhaseName,
                        Source = r.Source,
                        StartMs = r.StartMs,
                        EndMs = r.EndMs,
                        DurationMs = r.DurationMs,
                        Success = r.Success,
                        IsActive = false
                    });
                }

                var now = GetTimestampMs();
                foreach (var kvp in _active)
                {
                    snapshot.Add(new ScanTimingEntry
                    {
                        PhaseName = kvp.Key,
                        Source = kvp.Value.source,
                        StartMs = kvp.Value.startMs,
                        EndMs = now,
                        DurationMs = Math.Max(0, now - kvp.Value.startMs),
                        Success = false,
                        IsActive = true
                    });
                }

                return snapshot;
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
                WriteStructuredNdjson(copy);
                App.LogMessage($"[ScanTiming] Written {copy.Length} phase(s) to {_logPath}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ScanTiming] Failed to write log: {ex.Message}");
            }
        }

        private void WriteStructuredNdjson(PhaseRecord[] records)
        {
            if (records.Length == 0) return;

            try
            {
                var lines = new StringBuilder();
                foreach (var record in records)
                {
                    var row = new
                    {
                        runId = _runId,
                        collector = record.PhaseName,
                        layer = record.Source,
                        durationMs = record.DurationMs,
                        status = record.Success ? "ok" : "failed",
                        errorCode = record.Success ? (string?)null : "phase_failed",
                        attempt = 1,
                        timestampUtc = DateTimeOffset.UtcNow.ToString("o")
                    };
                    lines.AppendLine(JsonSerializer.Serialize(row));
                }
                File.AppendAllText(_ndjsonPath, lines.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ScanTiming] Failed NDJSON write: {ex.Message}");
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

    public class ScanTimingEntry
    {
        public string PhaseName { get; set; } = "";
        public string Source { get; set; } = "";
        public long StartMs { get; set; }
        public long EndMs { get; set; }
        public long DurationMs { get; set; }
        public bool Success { get; set; }
        public bool IsActive { get; set; }
    }
}
