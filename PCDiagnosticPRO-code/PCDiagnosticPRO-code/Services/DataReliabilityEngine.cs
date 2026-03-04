using System;
using System.Collections.Generic;
using System.Linq;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Data Reliability Score — GOD TIER logique progressive et non punitive.
    /// 
    /// Nouvelle courbe (Phase 4):
    /// 0 erreurs → 100, 1 → 95, 2 → 90, 3 → 84, 4 → 78, 5 → 72, 5+ → -4 par erreur
    /// 
    /// MissingData pondéré par criticité métier:
    /// - Security: pénalité élevée (8)
    /// - SMART/Storage: pénalité moyenne (4)
    /// - CPU Temp: pénalité faible (2)
    /// - ProcessList: pénalité faible (1)
    /// 
    /// Principe clé: Collecte partielle ≠ mauvais PC ; collecte partielle = confiance réduite.
    /// Le DRS mesure la FIABILITÉ DU DIAGNOSTIC, pas la santé du PC.
    /// </summary>
    public static class DataReliabilityEngine
    {
        /// <summary>Courbe progressive: index = errorCount, value = base score</summary>
        private static readonly int[] ErrorToScoreMap = { 100, 95, 90, 84, 78, 72 };

        /// <summary>Dégradation après 5 erreurs (par erreur supplémentaire)</summary>
        private const int DegradationPerErrorAfterFive = 4;

        /// <summary>Score minimum garanti</summary>
        private const int MinimumScore = 40;

        /// <summary>Poids métier des missing data : criticité → pénalité</summary>
        private static readonly (string[] Keywords, int Penalty, string Category)[] MissingDataWeights =
        {
            (new[] { "Security", "Defender", "Firewall", "Malware", "AV", "Antivirus" }, 8, "Security"),
            (new[] { "SMART", "PredictFailure", "ReallocatedSectors" }, 4, "SMART"),
            (new[] { "Disk", "Storage", "Volume" }, 4, "Storage"),
            (new[] { "Memory", "RAM", "GPU", "VRAM" }, 3, "Hardware"),
            (new[] { "CPU", "Temp", "Temperature" }, 2, "CPU_Temp"),
            (new[] { "Network", "EventLog", "Reliability", "BSOD" }, 2, "Monitoring"),
            (new[] { "Process", "ProcessList", "Startup", "App" }, 1, "Processes")
        };

        /// <summary>
        /// Calcule le Data Reliability Score (0-100) avec la nouvelle courbe progressive.
        /// </summary>
        /// <param name="collectorErrorsCount">Nombre d'erreurs collecteur (errors[] + invalidated metrics)</param>
        /// <param name="missingData">Liste des données manquantes (clés ou descriptions)</param>
        /// <param name="hasSecurityData">True si données sécurité présentes</param>
        /// <param name="hasSmartData">True si données SMART présentes</param>
        /// <returns>Score DRS entre 0 et 100</returns>
        public static int Compute(
            int collectorErrorsCount,
            IReadOnlyList<string> missingData,
            bool hasSecurityData = true,
            bool hasSmartData = true)
        {
            int score = 100;
            var appliedPenalties = new List<(string Source, int Penalty)>();

            // 1. Erreurs collecteur — courbe progressive non brutale
            if (collectorErrorsCount > 0)
            {
                if (collectorErrorsCount <= ErrorToScoreMap.Length)
                {
                    score = ErrorToScoreMap[collectorErrorsCount - 1];
                }
                else
                {
                    // Au-delà de 5 erreurs: dégradation douce de -4 par erreur
                    int extraErrors = collectorErrorsCount - ErrorToScoreMap.Length;
                    score = Math.Max(MinimumScore, 72 - (extraErrors * DegradationPerErrorAfterFive));
                }
                appliedPenalties.Add(($"CollectorErrors({collectorErrorsCount})", 100 - score));
            }

            // 2. MissingData pondéré par criticité métier (max cap: 20 points)
            int missingPenalty = 0;
            foreach (var item in missingData ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(item)) continue;
                
                var normalized = item.ToUpperInvariant();
                bool matched = false;
                
                foreach (var (keywords, penalty, category) in MissingDataWeights)
                {
                    if (keywords.Any(k => normalized.Contains(k.ToUpperInvariant())))
                    {
                        missingPenalty += penalty;
                        appliedPenalties.Add(($"Missing_{category}", penalty));
                        matched = true;
                        break;
                    }
                }
                
                // Pénalité par défaut pour items non reconnus
                if (!matched)
                {
                    missingPenalty += 1;
                    appliedPenalties.Add(($"Missing_Unknown", 1));
                }
            }
            
            // Cap la pénalité missingData à 20 points max
            int cappedMissingPenalty = Math.Min(missingPenalty, 20);
            score = Math.Max(MinimumScore, score - cappedMissingPenalty);

            // 3. Données critiques manquantes — impact additionnel limité
            if (!hasSecurityData)
            {
                score = Math.Max(MinimumScore, score - 10);
                appliedPenalties.Add(("NoSecurityData", 10));
            }
            
            if (!hasSmartData)
            {
                score = Math.Max(MinimumScore, score - 3);
                appliedPenalties.Add(("NoSmartData", 3));
            }

            // Log pour debug
            if (appliedPenalties.Count > 0)
            {
                var summary = string.Join(", ", appliedPenalties.Select(p => $"{p.Source}=-{p.Penalty}"));
                App.LogMessage($"[DRS] Score={score}/100 | Penalties: {summary}");
            }

            return Math.Clamp(score, MinimumScore, 100);
        }

        /// <summary>
        /// Calcul détaillé avec breakdown pour audit.
        /// </summary>
        public static (int Score, List<string> Breakdown) ComputeDetailed(
            int collectorErrorsCount,
            IReadOnlyList<string> missingData,
            bool hasSecurityData = true,
            bool hasSmartData = true)
        {
            var breakdown = new List<string>();
            int score = 100;
            breakdown.Add($"Base: 100");

            // Erreurs collecteur
            if (collectorErrorsCount > 0)
            {
                int errPenalty;
                if (collectorErrorsCount <= ErrorToScoreMap.Length)
                {
                    errPenalty = 100 - ErrorToScoreMap[collectorErrorsCount - 1];
                }
                else
                {
                    int extraErrors = collectorErrorsCount - ErrorToScoreMap.Length;
                    errPenalty = 100 - Math.Max(MinimumScore, 72 - (extraErrors * DegradationPerErrorAfterFive));
                }
                score -= errPenalty;
                breakdown.Add($"CollectorErrors({collectorErrorsCount}): -{errPenalty}");
            }

            // MissingData
            int missingPenalty = 0;
            foreach (var item in missingData ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(item)) continue;
                var normalized = item.ToUpperInvariant();
                
                foreach (var (keywords, penalty, category) in MissingDataWeights)
                {
                    if (keywords.Any(k => normalized.Contains(k.ToUpperInvariant())))
                    {
                        missingPenalty += penalty;
                        breakdown.Add($"Missing[{category}]: -{penalty}");
                        break;
                    }
                }
            }
            
            int cappedMissing = Math.Min(missingPenalty, 20);
            if (cappedMissing != missingPenalty)
                breakdown.Add($"MissingPenalty capped: {missingPenalty}→{cappedMissing}");
            score = Math.Max(MinimumScore, score - cappedMissing);

            if (!hasSecurityData)
            {
                score = Math.Max(MinimumScore, score - 10);
                breakdown.Add("NoSecurityData: -10");
            }
            
            if (!hasSmartData)
            {
                score = Math.Max(MinimumScore, score - 3);
                breakdown.Add("NoSmartData: -3");
            }

            breakdown.Add($"Final: {score}");
            return (Math.Clamp(score, MinimumScore, 100), breakdown);
        }
    }
}
