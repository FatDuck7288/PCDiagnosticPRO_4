using System;
using System.Collections.Generic;
using System.Linq;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Data Reliability Score — logique non brutale.
    /// 0 erreurs → 100, 1 → 92, 2 → 85, 3 → 78, 4 → 70, 5+ dégradation progressive.
    /// MissingData pondéré par importance métier (Security = fort impact, CPU temp = faible).
    /// Collecte partielle ≠ mauvais PC ; collecte partielle = confiance réduite.
    /// </summary>
    public static class DataReliabilityEngine
    {
        private static readonly int[] ErrorToScoreMap = { 100, 92, 85, 78, 70 };

        /// <summary>Poids métier des missing data : plus élevé = plus impact sur DRS</summary>
        private static readonly (string[] Keywords, int Penalty)[] MissingDataWeights =
        {
            (new[] { "Security", "Defender", "Firewall", "Malware", "AV" }, 8),
            (new[] { "SMART", "Disk", "Storage", "Volume" }, 6),
            (new[] { "Memory", "RAM", "GPU", "VRAM" }, 4),
            (new[] { "CPU", "Temp", "Network", "EventLog", "Reliability" }, 2),
            (new[] { "Process", "Startup", "App" }, 1)
        };

        /// <summary>
        /// Calcule le Data Reliability Score (0-100).
        /// </summary>
        /// <param name="collectorErrorsCount">Nombre d'erreurs collecteur (errors[] + invalidated metrics)</param>
        /// <param name="missingData">Liste des données manquantes (clés ou descriptions)</param>
        /// <param name="hasSecurityData">True si données sécurité présentes</param>
        /// <param name="hasSmartData">True si données SMART présentes</param>
        public static int Compute(
            int collectorErrorsCount,
            IReadOnlyList<string> missingData,
            bool hasSecurityData = true,
            bool hasSmartData = true)
        {
            int score = 100;

            // 1. Erreurs collecteur — mapping non brutal
            if (collectorErrorsCount > 0)
            {
                score = collectorErrorsCount <= ErrorToScoreMap.Length
                    ? ErrorToScoreMap[collectorErrorsCount - 1]
                    : Math.Max(0, 70 - (collectorErrorsCount - 5) * 5);
            }

            // 2. MissingData pondéré par importance métier
            int missingPenalty = 0;
            foreach (var item in missingData)
            {
                var normalized = item.ToUpperInvariant();
                foreach (var (keywords, penalty) in MissingDataWeights)
                {
                    if (keywords.Any(k => normalized.Contains(k.ToUpperInvariant())))
                    {
                        missingPenalty += penalty;
                        break;
                    }
                }
            }
            score = Math.Max(0, score - Math.Min(missingPenalty, 25));

            // 3. Données critiques manquantes — impact fort
            if (!hasSecurityData)
                score = Math.Max(0, score - 15);
            if (!hasSmartData && missingData.Count > 0)
                score = Math.Max(0, score - 5);

            return Math.Min(100, score);
        }
    }
}
