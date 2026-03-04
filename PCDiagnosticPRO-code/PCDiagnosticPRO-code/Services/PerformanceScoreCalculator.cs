using System;
using System.Collections.Generic;
using System.Linq;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Version: 1.0 — Table locale/heuristique (aucune base en ligne).
    /// Calcule un score de performance 0-100 et des catégories d'usage (Bureautique, Création, Jeux)
    /// à partir de CPU, GPU, RAM, stockage. Règles transparentes et versionnées dans le repo.
    /// </summary>
    public static class PerformanceScoreCalculator
    {
        public const string TableVersion = "1.0";

        public class PerformanceResult
        {
            public int Score { get; set; }
            public List<string> CapableDe { get; set; } = new();
            public List<string> Limites { get; set; } = new();
            public List<string> Categories { get; set; } = new();
        }

        /// <summary>
        /// Calcule le score et les textes à partir des infos matérielles.
        /// cpuName/gpuName peuvent être null; cores/threads/vramMb/ramGb à 0 = inconnu.
        /// </summary>
        public static PerformanceResult Calculate(
            string? cpuName,
            int cpuCores,
            int cpuThreads,
            string? gpuName,
            double gpuVramMb,
            double ramGb,
            bool hasSsd)
        {
            var result = new PerformanceResult();
            int cpuTier = TierCpu(cpuName, cpuCores, cpuThreads);
            int gpuTier = TierGpu(gpuName, gpuVramMb);
            int ramTier = TierRam(ramGb);
            int storageTier = hasSsd ? 2 : 1;

            // Score 0-100: moyenne pondérée (CPU 35%, GPU 35%, RAM 20%, Stockage 10%)
            double score = (TierToScore(cpuTier) * 0.35) + (TierToScore(gpuTier) * 0.35)
                + (TierToScore(ramTier) * 0.20) + (TierToScore(storageTier) * 0.10);
            result.Score = Math.Max(0, Math.Min(100, (int)Math.Round(score)));

            // Catégories
            if (result.Score >= 20) result.Categories.Add("Bureautique");
            if (result.Score >= 40) result.Categories.Add("Création (photo/vidéo légère)");
            if (result.Score >= 55) result.Categories.Add("Jeux 1080p");
            if (result.Score >= 75) result.Categories.Add("Jeux 1440p / Création pro");

            // Capable de
            if (result.Score >= 15) result.CapableDe.Add("Navigation web, bureautique, visio.");
            if (result.Score >= 40) result.CapableDe.Add("Montage vidéo léger, retouche photo.");
            if (result.Score >= 55) result.CapableDe.Add("Jeux récents en 1080p (réglages moyens à élevés).");
            if (result.Score >= 75) result.CapableDe.Add("Jeux en 1440p, streaming, création exigeante.");

            // Limites
            if (cpuTier <= 1) result.Limites.Add("CPU limitant pour tâches lourdes.");
            if (gpuTier <= 1 && result.Score < 50) result.Limites.Add("GPU intégré ou faible — jeux limités.");
            if (ramGb > 0 && ramGb < 8) result.Limites.Add("RAM insuffisante pour multitâche ou jeux récents.");
            if (!hasSsd) result.Limites.Add("Disque dur mécanique — lenteur au démarrage et chargements.");
            if (result.Limites.Count == 0) result.Limites.Add("Aucune limite majeure identifiée.");

            return result;
        }

        /// <summary>Tier CPU 1-5 (1=faible, 5=haut de gamme). Heuristique par cœurs/threads et nom.</summary>
        private static int TierCpu(string? name, int cores, int threads)
        {
            int t = threads > 0 ? threads : cores * 2;
            if (t >= 24) return 5;
            if (t >= 16) return 4;
            if (t >= 8) return 3;
            if (t >= 4) return 2;
            if (t >= 2) return 1;
            if (!string.IsNullOrEmpty(name))
            {
                var n = name.ToLowerInvariant();
                if (n.Contains("ryzen 9") || n.Contains("core i9") || n.Contains("xeon")) return 5;
                if (n.Contains("ryzen 7") || n.Contains("core i7")) return 4;
                if (n.Contains("ryzen 5") || n.Contains("core i5")) return 3;
                if (n.Contains("ryzen 3") || n.Contains("core i3") || n.Contains("pentium")) return 2;
            }
            return 1;
        }

        /// <summary>Tier GPU 1-5. Heuristique par VRAM et nom.</summary>
        private static int TierGpu(string? name, double vramMb)
        {
            if (vramMb >= 8192) return 5;
            if (vramMb >= 4096) return 4;
            if (vramMb >= 2048) return 3;
            if (vramMb >= 1024) return 2;
            if (!string.IsNullOrEmpty(name))
            {
                var n = name.ToLowerInvariant();
                if (n.Contains("rtx 40") || n.Contains("rx 7")) return 5;
                if (n.Contains("rtx 30") || n.Contains("rx 6")) return 4;
                if (n.Contains("gtx 16") || n.Contains("rx 5")) return 3;
                if (n.Contains("uhd") || n.Contains("iris") || n.Contains("vega")) return 2;
            }
            return 1;
        }

        private static int TierRam(double ramGb)
        {
            if (ramGb >= 32) return 5;
            if (ramGb >= 16) return 4;
            if (ramGb >= 8) return 3;
            if (ramGb >= 4) return 2;
            return 1;
        }

        private static double TierToScore(int tier) => tier switch { 5 => 100, 4 => 80, 3 => 60, 2 => 40, _ => 20 };
    }
}
