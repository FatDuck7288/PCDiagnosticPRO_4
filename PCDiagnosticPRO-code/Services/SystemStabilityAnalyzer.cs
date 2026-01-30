using System;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Stabilité système : BSOD, crash kernel, event logs critiques, Reliability monitor, driver crash.
    /// Produit SystemStabilityIndex 0-100.
    /// </summary>
    public static class SystemStabilityAnalyzer
    {
        public class StabilityInputs
        {
            public int BsodCount { get; set; }
            public int KernelCrashCount { get; set; }
            public int CriticalEventLogs7d { get; set; }
            public int AppErrors7d { get; set; }
            public int SystemErrors7d { get; set; }
            public int ReliabilityCrashes30d { get; set; }
            public int DriverCrashCount { get; set; }
        }

        /// <summary>
        /// Extrait les entrées stabilité depuis le JSON PowerShell (sections EventLogs, ReliabilityHistory, etc.).
        /// </summary>
        public static StabilityInputs ExtractFromJson(JsonElement root)
        {
            var inputs = new StabilityInputs();
            try
            {
                if (root.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Object)
                {
                    if (sections.TryGetProperty("EventLogs", out var eventLogs) && eventLogs.TryGetProperty("data", out var elData))
                    {
                        if (elData.TryGetProperty("logs", out var logs) && logs.ValueKind == JsonValueKind.Object)
                        {
                            if (logs.TryGetProperty("System", out var systemLog))
                            {
                                if (systemLog.TryGetProperty("errorCount", out var err)) inputs.SystemErrors7d = err.GetInt32();
                            }
                            if (logs.TryGetProperty("Application", out var appLog))
                            {
                                if (appLog.TryGetProperty("errorCount", out var err)) inputs.AppErrors7d = err.GetInt32();
                            }
                            inputs.CriticalEventLogs7d = inputs.SystemErrors7d + inputs.AppErrors7d;
                        }

                        if (elData.TryGetProperty("bsodCount", out var bsod)) inputs.BsodCount = bsod.GetInt32();
                    }
                    if (sections.TryGetProperty("ReliabilityHistory", out var rel) && rel.TryGetProperty("data", out var relData))
                    {
                        if (relData.TryGetProperty("crashCount30d", out var cc)) inputs.ReliabilityCrashes30d = cc.GetInt32();
                        if (relData.TryGetProperty("appCrashes", out var appCrashes)) inputs.ReliabilityCrashes30d = Math.Max(inputs.ReliabilityCrashes30d, appCrashes.GetInt32());
                        if (relData.TryGetProperty("bsodCount", out var relBsod)) inputs.BsodCount = Math.Max(inputs.BsodCount, relBsod.GetInt32());
                    }
                    if (sections.TryGetProperty("MinidumpAnalysis", out var minidump) && minidump.TryGetProperty("data", out var mdData))
                    {
                        if (mdData.TryGetProperty("bsodCount", out var b)) inputs.BsodCount = Math.Max(inputs.BsodCount, b.GetInt32());
                        if (mdData.TryGetProperty("kernelCrashCount", out var k)) inputs.KernelCrashCount = k.GetInt32();
                    }
                }

                if (root.TryGetProperty("scoreV2", out var scoreV2) && scoreV2.TryGetProperty("breakdown", out var bd))
                {
                    if (bd.TryGetProperty("critical", out var crit)) inputs.BsodCount = Math.Max(inputs.BsodCount, crit.GetInt32());
                }

                if (root.TryGetProperty("reliability", out var reliability))
                {
                    if (reliability.TryGetProperty("crashCount", out var rc)) inputs.ReliabilityCrashes30d = rc.GetInt32();
                    if (reliability.TryGetProperty("bsodCount", out var bc)) inputs.BsodCount = bc.GetInt32();
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SystemStabilityAnalyzer] Erreur extraction: {ex.Message}");
            }
            return inputs;
        }

        /// <summary>
        /// Calcule SystemStabilityIndex 0-100.
        /// BSOD = impact majeur ; event logs élevés = dégradation ; 0 = excellent.
        /// </summary>
        public static int ComputeIndex(StabilityInputs inputs)
        {
            int score = 100;

            if (inputs.BsodCount > 0)
                score -= Math.Min(50, 30 + inputs.BsodCount * 15);
            if (inputs.KernelCrashCount > 0)
                score -= Math.Min(30, inputs.KernelCrashCount * 10);
            if (inputs.ReliabilityCrashes30d > 5)
                score -= Math.Min(25, (inputs.ReliabilityCrashes30d - 5) * 3);
            else if (inputs.ReliabilityCrashes30d > 2)
                score -= 10;

            int totalLogErrors = inputs.CriticalEventLogs7d + inputs.SystemErrors7d + inputs.AppErrors7d;
            if (totalLogErrors > 50)
                score -= 20;
            else if (totalLogErrors > 20)
                score -= 10;
            else if (totalLogErrors > 5)
                score -= 5;

            if (inputs.DriverCrashCount > 0)
                score -= Math.Min(15, inputs.DriverCrashCount * 5);

            return Math.Max(0, Math.Min(100, score));
        }
    }
}
