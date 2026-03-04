using System;
using System.Collections.Generic;

namespace PCDiagnosticPro.Services
{
    public enum ScanProgressPhase
    {
        PowerShellScan = 0,
        Sensors = 1,
        MergeJson = 2,
        ReportBuild = 3,
        UiFinalize = 4
    }

    public sealed class ProgressPhaseState
    {
        public ScanProgressPhase Phase { get; init; }
        public string Section { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public int? Done { get; init; }
        public int? Total { get; init; }
        public int WeightedPercent { get; init; }
        public bool IsIndeterminate { get; init; }
    }

    /// <summary>
    /// Weighted progress engine for scan pipeline phases.
    /// Weights: PowerShell=60, Sensors=15, MergeJson=10, ReportBuild=10, UiFinalize=5.
    /// </summary>
    public sealed class ScanProgressEngine
    {
        private static readonly ScanProgressPhase[] OrderedPhases =
        {
            ScanProgressPhase.PowerShellScan,
            ScanProgressPhase.Sensors,
            ScanProgressPhase.MergeJson,
            ScanProgressPhase.ReportBuild,
            ScanProgressPhase.UiFinalize
        };

        private static readonly IReadOnlyDictionary<ScanProgressPhase, int> PhaseWeights =
            new Dictionary<ScanProgressPhase, int>
            {
                [ScanProgressPhase.PowerShellScan] = 60,
                [ScanProgressPhase.Sensors] = 15,
                [ScanProgressPhase.MergeJson] = 10,
                [ScanProgressPhase.ReportBuild] = 10,
                [ScanProgressPhase.UiFinalize] = 5
            };

        private readonly Dictionary<ScanProgressPhase, double> _phaseCompletion = new();
        private ScanProgressPhase _currentPhase = ScanProgressPhase.PowerShellScan;
        private string _currentSection = string.Empty;
        private string _currentMessage = string.Empty;
        private int? _currentDone;
        private int? _currentTotal;
        private bool _isIndeterminate = true;

        public event Action<ProgressPhaseState>? PhaseChanged;
        public event Action<ProgressPhaseState>? StepChanged;
        public event Action<ProgressPhaseState>? ProgressChanged;

        public ScanProgressEngine()
        {
            Reset();
        }

        public void Reset()
        {
            _phaseCompletion.Clear();
            foreach (var phase in OrderedPhases)
                _phaseCompletion[phase] = 0d;

            _currentPhase = ScanProgressPhase.PowerShellScan;
            _currentSection = string.Empty;
            _currentMessage = string.Empty;
            _currentDone = null;
            _currentTotal = null;
            _isIndeterminate = true;
            Publish(progressOnly: true);
        }

        public void BeginPhase(
            ScanProgressPhase phase,
            string section,
            string? message = null,
            bool indeterminate = false)
        {
            _currentPhase = phase;
            _currentSection = section ?? string.Empty;
            _currentMessage = message ?? string.Empty;
            _currentDone = null;
            _currentTotal = null;
            _isIndeterminate = indeterminate;

            Publish(phaseChanged: true, stepChanged: true);
        }

        public void ReportStep(
            string? section = null,
            string? message = null,
            int? done = null,
            int? total = null,
            int? explicitPercent = null,
            bool? indeterminate = null)
        {
            if (!string.IsNullOrWhiteSpace(section))
                _currentSection = section!.Trim();
            if (!string.IsNullOrWhiteSpace(message))
                _currentMessage = message!.Trim();

            _currentDone = done;
            _currentTotal = total;

            if (indeterminate.HasValue)
                _isIndeterminate = indeterminate.Value;
            else if ((done.HasValue && total.HasValue && total.Value > 0) || explicitPercent.HasValue)
                _isIndeterminate = false;

            var completion = _phaseCompletion[_currentPhase];
            if (explicitPercent.HasValue)
            {
                completion = Math.Max(completion, Clamp01(explicitPercent.Value / 100d));
            }
            else if (done.HasValue && total.HasValue && total.Value > 0)
            {
                completion = Math.Max(completion, Clamp01(done.Value / (double)total.Value));
            }
            _phaseCompletion[_currentPhase] = completion;

            Publish(stepChanged: true);
        }

        public void CompletePhase(ScanProgressPhase phase, string? message = null)
        {
            _currentPhase = phase;
            _phaseCompletion[phase] = 1d;
            if (!string.IsNullOrWhiteSpace(message))
                _currentMessage = message!;
            _isIndeterminate = false;
            _currentDone = null;
            _currentTotal = null;
            Publish(phaseChanged: true, stepChanged: true);
        }

        public ProgressPhaseState Snapshot()
        {
            return new ProgressPhaseState
            {
                Phase = _currentPhase,
                Section = _currentSection,
                Message = _currentMessage,
                Done = _currentDone,
                Total = _currentTotal,
                WeightedPercent = ComputeWeightedPercent(),
                IsIndeterminate = _isIndeterminate
            };
        }

        private void Publish(bool phaseChanged = false, bool stepChanged = false, bool progressOnly = false)
        {
            var state = Snapshot();
            if (phaseChanged)
                PhaseChanged?.Invoke(state);
            if (stepChanged)
                StepChanged?.Invoke(state);
            if (progressOnly || phaseChanged || stepChanged)
                ProgressChanged?.Invoke(state);
        }

        private int ComputeWeightedPercent()
        {
            var weighted = 0d;
            foreach (var phase in OrderedPhases)
            {
                var weight = PhaseWeights[phase];
                var completion = _phaseCompletion.TryGetValue(phase, out var value) ? value : 0d;
                weighted += weight * Clamp01(completion);
            }

            return Math.Max(0, Math.Min(100, (int)Math.Round(weighted)));
        }

        private static double Clamp01(double value)
        {
            if (value < 0d) return 0d;
            if (value > 1d) return 1d;
            return value;
        }
    }
}
