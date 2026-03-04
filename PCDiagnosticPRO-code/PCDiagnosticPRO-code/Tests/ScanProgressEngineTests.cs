using System;
using System.Collections.Generic;
using System.Linq;
using PCDiagnosticPro.Services;

namespace PCDiagnosticPro.Tests
{
    public static class ScanProgressEngineTests
    {
        private static readonly List<string> Failures = new();
        private static readonly List<string> Successes = new();

        public static (int passed, int failed, List<string> failures) RunAllTests()
        {
            Failures.Clear();
            Successes.Clear();

            Run(nameof(Test_WeightedProgress_ComputesExpectedPercent), Test_WeightedProgress_ComputesExpectedPercent);
            Run(nameof(Test_IndeterminateState_PersistsWithoutNumericProgress), Test_IndeterminateState_PersistsWithoutNumericProgress);
            Run(nameof(Test_CompletePhase_AdvancesToExpectedWeight), Test_CompletePhase_AdvancesToExpectedWeight);

            return (Successes.Count, Failures.Count, Failures.ToList());
        }

        private static void Test_WeightedProgress_ComputesExpectedPercent()
        {
            var engine = new ScanProgressEngine();
            engine.Reset();
            engine.BeginPhase(ScanProgressPhase.PowerShellScan, "PowerShell", "Collecting", indeterminate: false);
            engine.ReportStep(done: 12, total: 35);

            var state = engine.Snapshot();
            // 12/35 ~= 34.29% of phase weight 60 => ~20.57 => 21
            Assert(state.WeightedPercent == 21, $"Expected weighted percent 21, got {state.WeightedPercent}.");
            Assert(!state.IsIndeterminate, "State should be determinate after numeric progress.");
        }

        private static void Test_IndeterminateState_PersistsWithoutNumericProgress()
        {
            var engine = new ScanProgressEngine();
            engine.Reset();
            engine.BeginPhase(ScanProgressPhase.PowerShellScan, "PowerShell", "Collecting", indeterminate: true);
            engine.ReportStep(section: "PowerShell", message: "Waiting for script marker");

            var state = engine.Snapshot();
            Assert(state.IsIndeterminate, "State should stay indeterminate without numeric markers.");
        }

        private static void Test_CompletePhase_AdvancesToExpectedWeight()
        {
            var engine = new ScanProgressEngine();
            engine.Reset();
            engine.CompletePhase(ScanProgressPhase.PowerShellScan, "PowerShell done");
            var stateAfterPs = engine.Snapshot();
            Assert(stateAfterPs.WeightedPercent == 60, $"Expected 60 after PowerShell completion, got {stateAfterPs.WeightedPercent}.");

            engine.BeginPhase(ScanProgressPhase.Sensors, "Sensors", "Collecting sensors", indeterminate: false);
            engine.ReportStep(explicitPercent: 50);
            var stateAfterSensorsHalf = engine.Snapshot();
            // 60 + (50% of Sensors 15) = 67.5 => 68
            Assert(stateAfterSensorsHalf.WeightedPercent == 68, $"Expected 68 after half Sensors phase, got {stateAfterSensorsHalf.WeightedPercent}.");
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Successes.Add(name);
            }
            catch (Exception ex)
            {
                Failures.Add($"{name}: {ex.Message}");
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
