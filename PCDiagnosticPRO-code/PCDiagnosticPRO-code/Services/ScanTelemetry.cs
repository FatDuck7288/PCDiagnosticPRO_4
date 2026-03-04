using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Système de télémétrie pour mesurer les performances du pipeline de scan.
    /// Permet d'identifier les goulots d'étranglement (ex: délai à 85%).
    /// </summary>
    public class ScanTelemetry
    {
        private readonly Dictionary<string, Stopwatch> _timers = new();
        private readonly Dictionary<string, TimeSpan> _durations = new();
        private readonly Stopwatch _totalTimer = new();
        private readonly List<string> _phases = new();

        public void StartTotal()
        {
            _totalTimer.Restart();
            _timers.Clear();
            _durations.Clear();
            _phases.Clear();
            App.LogMessage("[TELEMETRY] === Démarrage mesure performances ===");
        }

        public void StartPhase(string phaseName)
        {
            if (!_timers.ContainsKey(phaseName))
            {
                _timers[phaseName] = new Stopwatch();
                _phases.Add(phaseName);
            }
            _timers[phaseName].Restart();
        }

        public void EndPhase(string phaseName)
        {
            if (_timers.TryGetValue(phaseName, out var timer))
            {
                timer.Stop();
                _durations[phaseName] = timer.Elapsed;
                App.LogMessage($"[TELEMETRY] {phaseName}: {timer.ElapsedMilliseconds}ms");
            }
        }

        public void EndTotal()
        {
            _totalTimer.Stop();
            LogSummary();
        }

        public TimeSpan GetPhaseDuration(string phaseName)
        {
            return _durations.TryGetValue(phaseName, out var duration) ? duration : TimeSpan.Zero;
        }

        public TimeSpan GetTotalDuration()
        {
            return _totalTimer.Elapsed;
        }

        private void LogSummary()
        {
            App.LogMessage("[TELEMETRY] === RÉSUMÉ PERFORMANCES ===");
            App.LogMessage($"[TELEMETRY] Total: {_totalTimer.ElapsedMilliseconds}ms");
            
            long accountedMs = 0;
            foreach (var phase in _phases)
            {
                if (_durations.TryGetValue(phase, out var duration))
                {
                    var pct = _totalTimer.ElapsedMilliseconds > 0 
                        ? (duration.TotalMilliseconds / _totalTimer.ElapsedMilliseconds * 100) 
                        : 0;
                    App.LogMessage($"[TELEMETRY]   {phase}: {duration.TotalMilliseconds:F0}ms ({pct:F1}%)");
                    accountedMs += (long)duration.TotalMilliseconds;
                }
            }
            
            var unaccountedMs = _totalTimer.ElapsedMilliseconds - accountedMs;
            if (unaccountedMs > 100)
            {
                App.LogMessage($"[TELEMETRY]   [Non mesuré]: {unaccountedMs}ms");
            }
            
            App.LogMessage("[TELEMETRY] === FIN RÉSUMÉ ===");
        }

        /// <summary>
        /// Phases standards du pipeline
        /// </summary>
        public static class Phases
        {
            public const string ProcessStart = "1_ProcessStart";
            public const string PowerShellExecution = "2_PowerShellExecution";
            public const string TxtReportWrite = "3_TxtReportWrite";
            public const string JsonReportWrite = "4_JsonReportWrite";
            public const string ProcessExit = "5_ProcessExit";
            public const string HardwareSensors = "6_HardwareSensors";
            public const string JsonResolve = "7_JsonResolve";
            public const string JsonWaitReady = "8_JsonWaitReady";
            public const string JsonRead = "9_JsonRead";
            public const string JsonParse = "10_JsonParse";
            public const string HealthReportBuild = "11_HealthReportBuild";
            public const string GradeCompute = "12_GradeCompute";
            public const string UiUpdate = "13_UiUpdate";
            public const string Navigation = "14_Navigation";
        }
    }
}
