using System;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Boot Time Health — mesure temps démarrage système et services critiques.
    /// </summary>
    public static class BootTimeHealthAnalyzer
    {
        public class BootTimeResult
        {
            public double? BootTimeSeconds { get; set; }
            public double? MainPathTimeSeconds { get; set; }
            public string BootHealthTier { get; set; } = "N/A";
            public int BootHealthScore { get; set; } = 100;
            public string Recommendation { get; set; } = "";
        }

        /// <summary>
        /// Mesure le temps de démarrage depuis les données Windows (EventLog ou registry).
        /// </summary>
        public static BootTimeResult Analyze(JsonElement? psRoot)
        {
            var result = new BootTimeResult();
            try
            {
                // Méthode 1: Depuis JSON PowerShell si disponible
                if (psRoot.HasValue && TryExtractFromJson(psRoot.Value, result))
                {
                    ComputeTierAndScore(result);
                    return result;
                }

                // Méthode 2: Lecture registre Windows
                TryReadFromRegistry(result);
                ComputeTierAndScore(result);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[BootTimeHealth] Erreur: {ex.Message}");
            }
            return result;
        }

        private static bool TryExtractFromJson(JsonElement root, BootTimeResult result)
        {
            try
            {
                if (root.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("OS", out var os) &&
                    os.TryGetProperty("data", out var data))
                {
                    if (data.TryGetProperty("bootTime", out var bt))
                        result.BootTimeSeconds = bt.GetDouble();
                    if (data.TryGetProperty("mainPathTime", out var mp))
                        result.MainPathTimeSeconds = mp.GetDouble();
                    return result.BootTimeSeconds.HasValue;
                }
            }
            catch { }
            return false;
        }

        private static void TryReadFromRegistry(BootTimeResult result)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
                if (key != null)
                {
                    var lastBoot = key.GetValue("LastBootUpTime") as string;
                    // Si disponible, on pourrait parser. Sinon on utilise Stopwatch depuis le démarrage.
                }

                // Approximation via Environment.TickCount64 (temps depuis boot)
                var uptimeMs = Environment.TickCount64;
                // Ce n'est pas le boot time, mais indique depuis combien de temps le système tourne.
            }
            catch { }
        }

        private static void ComputeTierAndScore(BootTimeResult result)
        {
            if (!result.BootTimeSeconds.HasValue)
            {
                result.BootHealthTier = "Non mesuré";
                result.Recommendation = "Temps de démarrage non disponible.";
                return;
            }

            var seconds = result.BootTimeSeconds.Value;
            if (seconds <= 15)
            {
                result.BootHealthTier = "Excellent (SSD/NVMe rapide)";
                result.BootHealthScore = 100;
                result.Recommendation = "Démarrage optimal.";
            }
            else if (seconds <= 30)
            {
                result.BootHealthTier = "Bon";
                result.BootHealthScore = 90;
                result.Recommendation = "Démarrage acceptable.";
            }
            else if (seconds <= 60)
            {
                result.BootHealthTier = "Moyen";
                result.BootHealthScore = 70;
                result.Recommendation = "Vérifier les programmes au démarrage.";
            }
            else if (seconds <= 120)
            {
                result.BootHealthTier = "Lent";
                result.BootHealthScore = 50;
                result.Recommendation = "Désactiver les applications inutiles au démarrage.";
            }
            else
            {
                result.BootHealthTier = "Très lent";
                result.BootHealthScore = 30;
                result.Recommendation = "Considérer un SSD ou nettoyer le démarrage.";
            }
        }
    }
}
