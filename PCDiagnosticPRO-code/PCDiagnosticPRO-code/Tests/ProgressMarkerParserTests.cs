using System;
using System.Collections.Generic;
using System.Linq;
using PCDiagnosticPro.Services;

namespace PCDiagnosticPro.Tests
{
    public static class ProgressMarkerParserTests
    {
        private static readonly List<string> Failures = new();
        private static readonly List<string> Successes = new();

        public static (int passed, int failed, List<string> failures) RunAllTests()
        {
            Failures.Clear();
            Successes.Clear();

            Run(nameof(Test_ParseProgressMarker_Nominal), Test_ParseProgressMarker_Nominal);
            Run(nameof(Test_ParseProgressMarker_MissingCounts), Test_ParseProgressMarker_MissingCounts);
            Run(nameof(Test_ParseProgressMarker_InvalidValues), Test_ParseProgressMarker_InvalidValues);
            Run(nameof(Test_ParseLiveMarker_Nominal), Test_ParseLiveMarker_Nominal);

            return (Successes.Count, Failures.Count, Failures.ToList());
        }

        private static void Test_ParseProgressMarker_Nominal()
        {
            const string line = "PROGRESS|phase=PowerShell|section=Network|done=12|total=35|percent=34|message=Collecting network adapters";
            Assert(ProgressMarkerParser.TryParseProgress(line, out var marker), "Nominal progress marker must parse.");
            Assert(marker.Phase == "PowerShell", "Phase parsing failed.");
            Assert(marker.Section == "Network", "Section parsing failed.");
            Assert(marker.Done == 12, "Done parsing failed.");
            Assert(marker.Total == 35, "Total parsing failed.");
            Assert(marker.Percent == 34, "Percent parsing failed.");
            Assert(marker.Message.Contains("Collecting", StringComparison.Ordinal), "Message parsing failed.");
        }

        private static void Test_ParseProgressMarker_MissingCounts()
        {
            const string line = "PROGRESS|phase=PowerShell|section=Apps|message=Collecting installed applications";
            Assert(ProgressMarkerParser.TryParseProgress(line, out var marker), "Marker with missing counts should still parse.");
            Assert(marker.Done == null, "Done should be null when absent.");
            Assert(marker.Total == null, "Total should be null when absent.");
            Assert(marker.Percent == null, "Percent should be null when absent.");
            Assert(marker.Section == "Apps", "Section parsing failed for missing-count marker.");
        }

        private static void Test_ParseProgressMarker_InvalidValues()
        {
            const string line = "PROGRESS|phase=PowerShell|section=Drivers|done=abc|total=zzz|percent=142|message=Collecting drivers";
            Assert(ProgressMarkerParser.TryParseProgress(line, out var marker), "Invalid numeric marker should still parse.");
            Assert(marker.Done == null, "Invalid done must map to null.");
            Assert(marker.Total == null, "Invalid total must map to null.");
            Assert(marker.Percent == 100, "Percent should be clamped to 100.");
        }

        private static void Test_ParseLiveMarker_Nominal()
        {
            const string line = "LIVE|Collecting network adapters...";
            Assert(ProgressMarkerParser.TryParseLive(line, out var message), "LIVE marker should parse.");
            Assert(message.Contains("Collecting", StringComparison.Ordinal), "LIVE payload parsing failed.");
            Assert(!ProgressMarkerParser.TryParseLive("PROGRESS|section=Network", out _), "Non LIVE marker must not parse as LIVE.");
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
