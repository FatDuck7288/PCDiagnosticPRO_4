using System;
using System.Collections.Generic;
using System.Linq;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.Services;

namespace PCDiagnosticPro.Tests
{
    public static class InfoExplanationServiceTests
    {
        private static readonly string[] ExpectedEmojiOrder = { "🔧", "📄", "💡", "⚠️", "🛠" };
        private static readonly string[] CorruptionMarkers = { "�", "Ã", "ðŸ" };

        public static void RunAll()
        {
            Test_DiskTemp_Normal();
            Test_DiskTemp_Danger();
            Test_TDR_Frequent();
            Test_VRAM_High();
            Test_CPUThrottle_Detected();
            Test_CPUThrottle_NotDetected();
        }

        public static void Test_DiskTemp_Normal()
        {
            var service = new InfoExplanationService();
            var lines = service.BuildInfoLines(new InfoContext
            {
                ContextId = InfoContextId.DiskTemp,
                SectionId = InfoSectionId.Storage,
                MetricLabel = "Température des disques",
                Value = 42d,
                Unit = "°C",
                Severity = InfoSeverity.Info,
                Confidence = InfoConfidence.High
            });

            AssertCommonShape(lines);
            Assert(lines[0].Text.Contains("normal", StringComparison.OrdinalIgnoreCase), "DiskTemp_Normal: title should indicate normal state.");
            Assert(lines[4].Text.Contains("surveillance", StringComparison.OrdinalIgnoreCase), "DiskTemp_Normal: action should recommend monitoring.");
            Assert(lines[1].Label == "Définition", "DiskTemp_Normal: definition label must contain correct accent.");
            Assert(lines[0].Text.Contains("°C", StringComparison.Ordinal), "DiskTemp_Normal: title should include °C.");
        }

        public static void Test_DiskTemp_Danger()
        {
            var service = new InfoExplanationService();
            var lines = service.BuildInfoLines(new InfoContext
            {
                ContextId = InfoContextId.DiskTemp,
                SectionId = InfoSectionId.Storage,
                MetricLabel = "Température des disques",
                Value = 67d,
                Unit = "°C",
                Severity = InfoSeverity.Danger,
                Confidence = InfoConfidence.High
            });

            AssertCommonShape(lines);
            Assert(lines[0].Text.Contains("critique", StringComparison.OrdinalIgnoreCase), "DiskTemp_Danger: title should indicate critical state.");
            Assert(lines[3].Text.Contains("corruption", StringComparison.OrdinalIgnoreCase), "DiskTemp_Danger: risks should mention data corruption.");
            Assert(lines[4].Text.Contains("Sauvegarde immédiate", StringComparison.Ordinal), "DiskTemp_Danger: actions should include immediate backup.");
        }

        public static void Test_TDR_Frequent()
        {
            var service = new InfoExplanationService();
            var lines = service.BuildInfoLines(new InfoContext
            {
                ContextId = InfoContextId.TDR,
                SectionId = InfoSectionId.GPU,
                MetricLabel = "TDR (crashes GPU)",
                Value = 5,
                Unit = "événement(s)",
                Severity = InfoSeverity.Danger,
                Confidence = InfoConfidence.High,
                Evidence = new InfoEvidence { EventCount = 5 }
            });

            AssertCommonShape(lines);
            Assert(lines[0].Text.Contains("fréquents", StringComparison.OrdinalIgnoreCase), "TDR_Frequent: title should indicate frequent events.");
            Assert(lines[2].Text.Contains("élevée", StringComparison.OrdinalIgnoreCase), "TDR_Frequent: importance should include the accented word 'élevée'.");
            Assert(lines[4].Text.Contains("réinstallation propre", StringComparison.OrdinalIgnoreCase), "TDR_Frequent: action should mention clean driver reinstall.");
        }

        public static void Test_VRAM_High()
        {
            var service = new InfoExplanationService();
            var lines = service.BuildInfoLines(new InfoContext
            {
                ContextId = InfoContextId.VRAM,
                SectionId = InfoSectionId.GPU,
                MetricLabel = "VRAM dédiée",
                Value = 95d,
                Unit = "%",
                Severity = InfoSeverity.Danger,
                Confidence = InfoConfidence.High
            });

            AssertCommonShape(lines);
            Assert(lines[3].Text.Contains("stutter", StringComparison.OrdinalIgnoreCase), "VRAM_High: risk should mention stutter.");
            Assert(lines[4].Text.Contains("résolution", StringComparison.OrdinalIgnoreCase), "VRAM_High: action should mention lowering resolution.");
        }

        public static void Test_CPUThrottle_Detected()
        {
            var service = new InfoExplanationService();
            var lines = service.BuildInfoLines(new InfoContext
            {
                ContextId = InfoContextId.CPUThrottle,
                SectionId = InfoSectionId.CPU,
                MetricLabel = "Throttling",
                Value = "Oui",
                Severity = InfoSeverity.Warning,
                Confidence = InfoConfidence.High
            });

            AssertCommonShape(lines);
            Assert(lines[0].Text.Contains("détecté", StringComparison.OrdinalIgnoreCase), "CPUThrottle_Detected: title should indicate detection.");
            Assert(lines[4].Text.Contains("refroidissement", StringComparison.OrdinalIgnoreCase), "CPUThrottle_Detected: action should mention cooling.");
        }

        public static void Test_CPUThrottle_NotDetected()
        {
            var service = new InfoExplanationService();
            var lines = service.BuildInfoLines(new InfoContext
            {
                ContextId = InfoContextId.CPUThrottle,
                SectionId = InfoSectionId.CPU,
                MetricLabel = "Throttling",
                Value = "Non détecté",
                Severity = InfoSeverity.Info,
                Confidence = InfoConfidence.High
            });

            AssertCommonShape(lines);
            Assert(lines[0].Text.Contains("non détecté", StringComparison.OrdinalIgnoreCase), "CPUThrottle_NotDetected: title should indicate no throttling.");
            Assert(lines[1].Text.Contains("Aucune limitation", StringComparison.OrdinalIgnoreCase), "CPUThrottle_NotDetected: definition should remain short and educational.");
        }

        private static void AssertCommonShape(IReadOnlyList<InfoLine> lines)
        {
            Assert(lines.Count == 5, $"Expected 5 lines, got {lines.Count}.");

            for (var i = 0; i < ExpectedEmojiOrder.Length; i++)
            {
                Assert(lines[i].Emoji == ExpectedEmojiOrder[i], $"Line {i} emoji mismatch. Expected '{ExpectedEmojiOrder[i]}', got '{lines[i].Emoji}'.");
                Assert(!lines[i].Text.Contains('\n') && !lines[i].Text.Contains('\r'), $"Line {i} text must be single-row without newline.");
            }

            var aggregated = string.Join(" ", lines.Select(l => $"{l.Label} {l.Text}"));
            foreach (var marker in CorruptionMarkers)
            {
                Assert(!aggregated.Contains(marker, StringComparison.Ordinal), $"Output contains encoding corruption marker '{marker}'.");
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
