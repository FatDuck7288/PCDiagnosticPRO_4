using System;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Profil CPU complet depuis JSON (marque, modèle, cores, GHz) + CpuPerformanceTier.
    /// Gaming capable / Workstation capable / Office only.
    /// </summary>
    public static class CpuProfileAnalyzer
    {
        public class CpuProfile
        {
            public string Brand { get; set; } = "N/A";
            public string Model { get; set; } = "N/A";
            public int Cores { get; set; }
            public int Threads { get; set; }
            public double BaseGhz { get; set; }
            public double BoostGhz { get; set; }
            public double? RealtimeGhz { get; set; }
            public string PerformanceTier { get; set; } = "Office only";
        }

        /// <summary>
        /// Extrait le profil CPU depuis le JSON PowerShell (cpuList).
        /// </summary>
        public static CpuProfile Analyze(JsonElement root)
        {
            var profile = new CpuProfile();
            try
            {
                if (!root.TryGetProperty("cpuList", out var cpuList) || cpuList.ValueKind != JsonValueKind.Array)
                    return profile;

                var first = cpuList.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Object)
                    return profile;

                if (first.TryGetProperty("name", out var name))
                {
                    var fullName = name.GetString() ?? "";
                    profile.Model = fullName.Trim();
                    profile.Brand = fullName.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" :
                                   fullName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD" : "N/A";
                }
                if (first.TryGetProperty("cores", out var cores))
                    profile.Cores = cores.GetInt32();
                if (first.TryGetProperty("physicalCores", out var phys))
                    profile.Cores = phys.GetInt32();
                if (first.TryGetProperty("processors", out var procs))
                    profile.Cores = procs.GetInt32();
                if (first.TryGetProperty("threads", out var threads))
                    profile.Threads = threads.GetInt32();
                if (profile.Threads == 0)
                    profile.Threads = profile.Cores;

                if (first.TryGetProperty("speed", out var speed))
                    profile.RealtimeGhz = speed.GetDouble();
                if (first.TryGetProperty("speedMin", out var min))
                    profile.BaseGhz = min.GetDouble();
                if (first.TryGetProperty("speedMax", out var max))
                    profile.BoostGhz = max.GetDouble();
                if (first.TryGetProperty("baseClock", out var baseClock))
                    profile.BaseGhz = baseClock.GetDouble();
                if (first.TryGetProperty("turboClock", out var turbo))
                    profile.BoostGhz = turbo.GetDouble();
                if (profile.BoostGhz <= 0 && profile.RealtimeGhz.HasValue)
                    profile.BoostGhz = profile.RealtimeGhz.Value;
                if (profile.BaseGhz <= 0)
                    profile.BaseGhz = profile.BoostGhz > 0 ? profile.BoostGhz * 0.7 : 2.0;

                profile.PerformanceTier = ComputePerformanceTier(profile);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[CpuProfileAnalyzer] Erreur: {ex.Message}");
            }
            return profile;
        }

        /// <summary>
        /// Gaming capable = 8+ threads, boost ≥ 4 GHz.
        /// Workstation capable = 6+ threads, boost ≥ 3.5 GHz.
        /// Office only = reste.
        /// </summary>
        private static string ComputePerformanceTier(CpuProfile p)
        {
            int effectiveThreads = p.Threads > 0 ? p.Threads : p.Cores * 2;
            double maxGhz = p.BoostGhz > 0 ? p.BoostGhz : p.BaseGhz;

            if (effectiveThreads >= 8 && maxGhz >= 4.0)
                return "Gaming capable";
            if (effectiveThreads >= 6 && maxGhz >= 3.5)
                return "Workstation capable";
            return "Office only";
        }
    }
}
