using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// P0 Bloc C: Calcule les métriques de qualité de collecte (Coverage, Reliability, Actionability).
    /// Objectif 90%: Coverage ≥ 90%, Reliability ≥ 90%, Actionability ≥ 80%.
    /// </summary>
    public static class QualityScoreCalculator
    {
        /// <summary>Domaines clés pour le calcul Coverage (présence de données exploitables).</summary>
        private static readonly HealthDomain[] KeyDomains = new[]
        {
            HealthDomain.OS, HealthDomain.CPU, HealthDomain.GPU, HealthDomain.RAM, HealthDomain.Storage,
            HealthDomain.Network, HealthDomain.SystemStability, HealthDomain.Security, HealthDomain.Performance
        };

        /// <summary>
        /// Calcule DiagnosticQuality à partir du rapport et des diagnostics (si disponibles).
        /// </summary>
        public static DiagnosticQualityResult Compute(
            HealthReport? report,
            CollectorDiagnosticsService.CollectorDiagnosticsResult? diagnostics = null)
        {
            var result = new DiagnosticQualityResult
            {
                CoverageScore = 100,
                ReliabilityScore = 100,
                ActionabilityScore = 100,
                OverallScore = 100,
                TimestampUtc = DateTime.UtcNow
            };

            if (report == null)
            {
                result.CoverageScore = 0;
                result.ReliabilityScore = 0;
                result.ActionabilityScore = 0;
                result.OverallScore = 0;
                result.Message = "Rapport absent";
                return result;
            }

            // --- Coverage: % de domaines clés avec données non vides ---
            int domainsWithData = 0;
            foreach (var domain in KeyDomains)
            {
                if (report.Sections.Any(s => s.Domain == domain && s.HasData))
                    domainsWithData++;
            }
            result.CoverageScore = KeyDomains.Length > 0
                ? Math.Min(100, (domainsWithData * 100) / KeyDomains.Length)
                : 100;
            result.CoverageDetails = $"{domainsWithData}/{KeyDomains.Length} domaines avec données";

            // --- Reliability: pénaliser erreurs collecteur, missingData, sentinelles ---
            int reliabilityPenalty = 0;
            if (report.CollectorErrorsLogical > 0)
                reliabilityPenalty += Math.Min(30, report.CollectorErrorsLogical * 6);
            if (report.MissingData?.Count > 0)
                reliabilityPenalty += Math.Min(25, report.MissingData.Count * 3);
            if (report.CollectionStatus == "FAILED")
                reliabilityPenalty += 40;
            else if (report.CollectionStatus == "PARTIAL")
                reliabilityPenalty += 10;
            if (diagnostics != null && diagnostics.InvalidatedMetrics?.Count > 0)
                reliabilityPenalty += Math.Min(15, diagnostics.InvalidatedMetrics.Count * 3);
            result.ReliabilityScore = Math.Max(0, 100 - reliabilityPenalty);
            result.ReliabilityDetails = report.CollectorErrorsLogical > 0 || (report.MissingData?.Count ?? 0) > 0
                ? $"collectorErrors={report.CollectorErrorsLogical}, missingData={report.MissingData?.Count ?? 0}"
                : "OK";

            // --- Actionability: pénaliser si findings vides alors qu'anomalies possibles ---
            int actionabilityPenalty = 0;
            var findingsCount = report.UdisFindings?.Count ?? 0;
            bool hasAnomalies = (report.Errors?.Count ?? 0) > 0
                || (report.MissingData?.Count ?? 0) > 0
                || report.Sections.Any(s => s.Severity >= HealthSeverity.Warning && s.HasData);
            if (hasAnomalies && findingsCount == 0)
                actionabilityPenalty += 30;
            if (findingsCount > 0)
            {
                var withoutEvidence = report.UdisFindings!.Count(f => f.EvidencePaths == null || f.EvidencePaths.Count == 0);
                if (withoutEvidence > 0)
                    actionabilityPenalty += Math.Min(15, withoutEvidence * 2);
            }
            result.ActionabilityScore = Math.Max(0, 100 - actionabilityPenalty);
            result.ActionabilityDetails = findingsCount > 0
                ? $"{findingsCount} findings"
                : (hasAnomalies ? "anomalies sans findings" : "OK");

            // --- Overall: moyenne pondérée (Coverage 40%, Reliability 35%, Actionability 25%) ---
            result.OverallScore = (int)Math.Round(
                0.40 * result.CoverageScore + 0.35 * result.ReliabilityScore + 0.25 * result.ActionabilityScore);
            result.OverallScore = Math.Max(0, Math.Min(100, result.OverallScore));
            result.Message = result.OverallScore >= 90 ? "Qualité objectif 90% atteinte"
                : $"Qualité {result.OverallScore}% (objectif: Coverage≥90, Reliability≥90, Actionability≥80)";

            return result;
        }

        /// <summary>
        /// Écrit le résumé qualité dans %TEMP%\PCDiagnosticPro_quality.log (append).
        /// </summary>
        public static void WriteQualityLog(DiagnosticQualityResult quality, string? logPath = null)
        {
            try
            {
                var path = logPath ?? Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_quality.log");
                var lines = new List<string>
                {
                    $"[{quality.TimestampUtc:yyyy-MM-dd HH:mm:ss}Z] DiagnosticQuality",
                    $"  Coverage={quality.CoverageScore}% ({quality.CoverageDetails})",
                    $"  Reliability={quality.ReliabilityScore}% ({quality.ReliabilityDetails})",
                    $"  Actionability={quality.ActionabilityScore}% ({quality.ActionabilityDetails})",
                    $"  Overall={quality.OverallScore}% — {quality.Message}",
                    ""
                };
                File.AppendAllLines(path, lines);
                App.LogMessage($"[QualityScore] Log écrit: {path}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[QualityScore] Erreur écriture log: {ex.Message}");
            }
        }
    }
}
