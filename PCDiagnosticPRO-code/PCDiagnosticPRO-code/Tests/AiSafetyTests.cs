using System;
using System.Collections.Generic;
using System.Linq;
using PCDiagnosticPro.AI;

namespace PCDiagnosticPro.Tests
{
    public static class AiSafetyTests
    {
        private static readonly List<string> Failures = new();
        private static readonly List<string> Successes = new();

        public static (int passed, int failed, List<string> failures) RunAllTests()
        {
            Failures.Clear();
            Successes.Clear();

            Run(nameof(Test_MissingCapabilities_DoesNotBlockAutofix_WhenMandatoryGuardsPresent), Test_MissingCapabilities_DoesNotBlockAutofix_WhenMandatoryGuardsPresent);
            Run(nameof(Test_MissingTryCatch_BlocksAutofix), Test_MissingTryCatch_BlocksAutofix);
            Run(nameof(Test_UnauthorizedCapability_DisablesAutofix), Test_UnauthorizedCapability_DisablesAutofix);
            Run(nameof(Test_SafeScript_WithAllowedCapabilities_IsApproved), Test_SafeScript_WithAllowedCapabilities_IsApproved);
            Run(nameof(Test_BlockedCommands_DisableAutofix), Test_BlockedCommands_DisableAutofix);

            return (Successes.Count, Failures.Count, Failures.ToList());
        }

        private static void Test_MissingCapabilities_DoesNotBlockAutofix_WhenMandatoryGuardsPresent()
        {
            var settings = AiSettings.CreateDefaultSafe();
            var engine = new SafetyPolicyEngine(settings);

            const string script =
                "#Requires -Version 5.1\n" +
                "# SUMMARY: test\n" +
                "try {\n" +
                "  Write-Host 'hello'\n" +
                "} catch {\n" +
                "  Write-Host $_\n" +
                "}\n";
            var judge = engine.Analyse(script);
            var gate = engine.EvaluateForAutoFix(script, judge);

            Assert(gate.IsApproved, "AutoFix should stay available when no dangerous behavior is detected.");
            Assert(judge.Flags.Any(f => f.Contains("CAPABILITIES_MISSING", StringComparison.OrdinalIgnoreCase)),
                "CAPABILITIES_MISSING flag expected.");
        }

        private static void Test_MissingTryCatch_BlocksAutofix()
        {
            var settings = AiSettings.CreateDefaultSafe();
            var engine = new SafetyPolicyEngine(settings);

            const string script =
                "#Requires -Version 5.1\n" +
                "# SUMMARY: test\n" +
                "# CAPABILITIES: read-only diagnostics\n" +
                "Write-Host 'hello'\n";

            var judge = engine.Analyse(script);
            var gate = engine.EvaluateForAutoFix(script, judge);

            Assert(!gate.IsApproved, "AutoFix must be blocked when mandatory try/catch is missing.");
            Assert(judge.HasMandatoryGuardViolations, "Mandatory guard flag expected.");
        }

        private static void Test_UnauthorizedCapability_DisablesAutofix()
        {
            var settings = AiSettings.CreateDefaultSafe();
            var engine = new SafetyPolicyEngine(settings);

            const string script = "# SUMMARY: test\n# CAPABILITIES: disable antivirus\nWrite-Host 'hello'";
            var judge = engine.Analyse(script);
            var gate = engine.EvaluateForAutoFix(script, judge);

            Assert(!gate.IsApproved, "AutoFix must be disabled for unauthorized capabilities.");
            Assert(judge.UnauthorizedCapabilities.Count > 0, "Unauthorized capabilities expected.");
        }

        private static void Test_SafeScript_WithAllowedCapabilities_IsApproved()
        {
            var settings = AiSettings.CreateDefaultSafe();
            var engine = new SafetyPolicyEngine(settings);

            const string script =
                "#Requires -Version 5.1\n" +
                "# SUMMARY: run diagnostics\n" +
                "# DOES_NOT: no destructive actions\n" +
                "# RISKS: low\n" +
                "# ROLLBACK: none\n" +
                "# REQUIRES_ADMIN: No\n" +
                "# CAPABILITIES: read-only diagnostics, export logs\n" +
                "try {\n" +
                "  Write-Host 'Collecting data...'\n" +
                "  Get-Service | Out-File \"$env:TEMP\\services.txt\"\n" +
                "  Write-Host \"AutoFix script completed.\" -ForegroundColor Green\n" +
                "} catch {\n" +
                "  Write-Host $_\n" +
                "  throw\n" +
                "}\n";

            var judge = engine.Analyse(script);
            var gate = engine.EvaluateForAutoFix(script, judge);

            Assert(gate.IsApproved, "Safe script should be approved for AutoFix.");
            Assert(!judge.ContainsBlockedCommand, "No blocked commands expected.");
            Assert(judge.OverallScore0_100 >= 60, "Overall quality score should remain acceptable.");
        }

        private static void Test_BlockedCommands_DisableAutofix()
        {
            var settings = AiSettings.CreateDefaultSafe();
            var engine = new SafetyPolicyEngine(settings);

            const string script =
                "# CAPABILITIES: read-only diagnostics\n" +
                "IEX (\"Write-Host hacked\")\n" +
                "powershell -EncodedCommand AAAA\n";

            var judge = engine.Analyse(script);
            var gate = engine.EvaluateForAutoFix(script, judge);

            Assert(!gate.IsApproved, "Blocked commands must disable AutoFix.");
            Assert(judge.ContainsBlockedCommand, "Blocked command flag expected.");
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
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
