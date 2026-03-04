using System;
using System.Collections.Generic;
using System.Linq;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    public static class DeterministicMode
    {
        public const string Flag = "--deterministic";
        public const string EnvKey = "PCDIAG_DETERMINISTIC";
        public const string FixedTimestamp = "2000-01-01T00:00:00.0000000Z";
        public const string FixedRunId = "deterministic-run";

        public static bool IsEnabled(string[]? args = null)
        {
            var env = Environment.GetEnvironmentVariable(EnvKey);
            if (!string.IsNullOrWhiteSpace(env) &&
                (string.Equals(env, "1", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(env, "true", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(env, "yes", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var commandLine = args ?? Environment.GetCommandLineArgs();
            return commandLine.Any(a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));
        }

        public static void NormalizeCombinedInPlace(CombinedScanResult combined)
        {
            if (combined == null)
                return;

            combined.Trace.RunId = FixedRunId;
            combined.Trace.TraceId = FixedRunId;
            combined.Metadata.RunId = FixedRunId;
            combined.Metadata.Timestamp = FixedTimestamp;

            if (combined.DiagnosticSnapshot != null)
                combined.DiagnosticSnapshot.GeneratedAt = FixedTimestamp;

            if (combined.UpdatesCsharp != null)
            {
                combined.UpdatesCsharp.Timestamp = FixedTimestamp;
                combined.UpdatesCsharp.LastCheckedUtc ??= FixedTimestamp;
            }

            if (combined.RunStatus != null)
            {
                combined.RunStatus.ReasonCodes = combined.RunStatus.ReasonCodes
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                combined.RunStatus.FailedGates = combined.RunStatus.FailedGates
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            combined.MissingData = combined.MissingData
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            combined.Sections = combined.Sections
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            combined.Errors = combined.Errors
                .OrderBy(e => e.Code, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Section, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Message, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (combined.Timings?.PhaseTotals != null && combined.Timings.PhaseTotals.Count > 0)
            {
                var ordered = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in combined.Timings.PhaseTotals.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    ordered[kv.Key] = kv.Value;
                combined.Timings.PhaseTotals = ordered;
            }
        }
    }
}
