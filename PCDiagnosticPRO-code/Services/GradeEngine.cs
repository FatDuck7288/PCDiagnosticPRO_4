using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Moteur de notation côté application - INDÉPENDANT du PowerShell.
    /// Applique des règles d'interprétation déterministes et explicables.
    /// </summary>
    public static class GradeEngine
    {
        #region Configuration des règles

        /// <summary>Poids de chaque domaine dans le score global</summary>
        private static readonly Dictionary<HealthDomain, int> DomainWeights = new()
        {
            { HealthDomain.OS, 15 },
            { HealthDomain.CPU, 15 },
            { HealthDomain.GPU, 10 },
            { HealthDomain.RAM, 15 },
            { HealthDomain.Storage, 20 },
            { HealthDomain.Network, 10 },
            { HealthDomain.SystemStability, 10 },
            { HealthDomain.Drivers, 5 }
        };

        /// <summary>Seuils de grades</summary>
        private static readonly (int minScore, string grade, string verdict)[] GradeThresholds =
        {
            (95, "A+", "Excellent - Votre PC est en parfait état"),
            (90, "A", "Très bien - Votre PC fonctionne optimalement"),
            (80, "B+", "Bien - Quelques optimisations mineures possibles"),
            (70, "B", "Correct - Attention recommandée sur certains points"),
            (60, "C", "Dégradé - Des problèmes affectent les performances"),
            (50, "D", "Critique - Intervention recommandée rapidement"),
            (0, "F", "Critique - Intervention urgente nécessaire")
        };

        #endregion

        #region Public API

        /// <summary>
        /// Calcule le score et le grade depuis les données brutes du scan.
        /// Cette méthode est la source de vérité pour les grades UI.
        /// </summary>
        public static GradeResult ComputeGrade(HealthReport report)
        {
            var result = new GradeResult();
            
            try
            {
                // 1. Calculer le score de chaque domaine
                var domainScores = new Dictionary<HealthDomain, DomainScore>();
                int totalWeight = 0;
                int weightedSum = 0;

                foreach (var section in report.Sections)
                {
                    var domainScore = EvaluateDomain(section, report);
                    domainScores[section.Domain] = domainScore;
                    
                    if (domainScore.HasData && DomainWeights.TryGetValue(section.Domain, out var weight))
                    {
                        totalWeight += weight;
                        weightedSum += domainScore.Score * weight;
                    }
                    
                    result.DomainDetails.Add(section.Domain, domainScore);
                }

                // 2. Calculer le score global pondéré
                result.RawScore = totalWeight > 0 ? weightedSum / totalWeight : 0;
                
                // 3. Appliquer les pénalités critiques (override)
                result.FinalScore = ApplyCriticalPenalties(result.RawScore, report, result);
                
                // 4. Déterminer le grade
                (result.Grade, result.Verdict) = DetermineGrade(result.FinalScore);
                
                // 5. Déterminer la sévérité
                result.Severity = HealthReport.ScoreToSeverity(result.FinalScore);
                
                // 6. Générer les explications
                result.Explanations = GenerateExplanations(result, report);
                
                App.LogMessage($"[GradeEngine] Score calculé: Raw={result.RawScore}, Final={result.FinalScore}, Grade={result.Grade}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[GradeEngine] ERREUR: {ex.Message}");
                result.FinalScore = 0;
                result.Grade = "?";
                result.Verdict = "Impossible d'évaluer - données manquantes";
                result.Severity = HealthSeverity.Unknown;
            }
            
            return result;
        }

        /// <summary>
        /// Recalcule le HealthReport avec les grades du moteur application.
        /// </summary>
        public static void ApplyGrades(HealthReport report)
        {
            var gradeResult = ComputeGrade(report);
            
            // Appliquer le score global
            report.GlobalScore = gradeResult.FinalScore;
            report.Grade = gradeResult.Grade;
            report.GlobalSeverity = gradeResult.Severity;
            report.GlobalMessage = gradeResult.Verdict;
            
            // Appliquer les scores par section
            foreach (var section in report.Sections)
            {
                if (gradeResult.DomainDetails.TryGetValue(section.Domain, out var domainScore))
                {
                    section.Score = domainScore.Score;
                    section.Severity = HealthReport.ScoreToSeverity(domainScore.Score);
                    section.StatusMessage = domainScore.StatusMessage;
                    section.DetailedExplanation = domainScore.Explanation;
                }
            }
        }

        #endregion

        #region Evaluation Logic

        private static DomainScore EvaluateDomain(HealthSection section, HealthReport report)
        {
            var score = new DomainScore
            {
                Domain = section.Domain,
                HasData = section.HasData
            };

            if (!section.HasData)
            {
                score.Score = 0;
                score.StatusMessage = "Données non disponibles";
                score.Explanation = "Ce domaine n'a pas pu être analysé.";
                return score;
            }

            // Score de base
            int baseScore = 100;
            var penalties = new List<(int penalty, string reason)>();

            // Évaluation selon le domaine
            switch (section.Domain)
            {
                case HealthDomain.OS:
                    EvaluateOS(section, report, penalties);
                    break;
                case HealthDomain.CPU:
                    EvaluateCPU(section, report, penalties);
                    break;
                case HealthDomain.GPU:
                    EvaluateGPU(section, report, penalties);
                    break;
                case HealthDomain.RAM:
                    EvaluateRAM(section, report, penalties);
                    break;
                case HealthDomain.Storage:
                    EvaluateStorage(section, report, penalties);
                    break;
                case HealthDomain.Network:
                    EvaluateNetwork(section, report, penalties);
                    break;
                case HealthDomain.SystemStability:
                    EvaluateStability(section, report, penalties);
                    break;
                case HealthDomain.Drivers:
                    EvaluateDrivers(section, report, penalties);
                    break;
            }

            // Appliquer les pénalités
            int totalPenalty = penalties.Sum(p => p.penalty);
            score.Score = Math.Max(0, baseScore - totalPenalty);
            score.Penalties = penalties;
            
            // Générer le message de statut
            score.StatusMessage = GenerateDomainStatus(score.Score);
            score.Explanation = GenerateDomainExplanation(section.Domain, score.Score, penalties);

            return score;
        }

        private static void EvaluateOS(HealthSection section, HealthReport report, List<(int, string)> penalties)
        {
            // Vérifier si Windows à jour
            if (section.EvidenceData.TryGetValue("UpdateStatus", out var updateStatus))
            {
                if (updateStatus.Contains("obsolète", StringComparison.OrdinalIgnoreCase) ||
                    updateStatus.Contains("outdated", StringComparison.OrdinalIgnoreCase))
                {
                    penalties.Add((20, "Windows n'est pas à jour"));
                }
            }

            // Vérifier les erreurs critiques liées à l'OS
            var osErrors = report.Errors.Count(e => e.Section.Contains("OS", StringComparison.OrdinalIgnoreCase));
            if (osErrors > 0) penalties.Add((osErrors * 5, $"{osErrors} erreur(s) OS détectée(s)"));

            // Vérifier l'intégrité système si disponible
            if (report.ScoreV2.Breakdown.Critical > 0 && section.Domain == HealthDomain.OS)
            {
                penalties.Add((15, "Problèmes d'intégrité système détectés"));
            }
        }

        private static void EvaluateCPU(HealthSection section, HealthReport report, List<(int, string)> penalties)
        {
            // Vérifier la température si disponible
            if (section.EvidenceData.TryGetValue("Temperature", out var tempStr))
            {
                if (double.TryParse(tempStr.Replace("°C", "").Trim(), out var temp))
                {
                    if (temp > 90) penalties.Add((30, $"Température CPU critique ({temp}°C)"));
                    else if (temp > 80) penalties.Add((15, $"Température CPU élevée ({temp}°C)"));
                    else if (temp > 70) penalties.Add((5, $"Température CPU à surveiller ({temp}°C)"));
                }
            }

            // Vérifier la charge CPU si disponible
            if (section.EvidenceData.TryGetValue("Load", out var loadStr))
            {
                if (double.TryParse(loadStr.Replace("%", "").Trim(), out var load))
                {
                    if (load > 95) penalties.Add((20, "CPU surchargé en permanence"));
                    else if (load > 80) penalties.Add((10, "Charge CPU élevée"));
                }
            }
        }

        private static void EvaluateGPU(HealthSection section, HealthReport report, List<(int, string)> penalties)
        {
            // Vérifier la température GPU
            if (section.EvidenceData.TryGetValue("Temperature", out var tempStr))
            {
                if (double.TryParse(tempStr.Replace("°C", "").Trim(), out var temp))
                {
                    if (temp > 95) penalties.Add((30, $"Température GPU critique ({temp}°C)"));
                    else if (temp > 85) penalties.Add((15, $"Température GPU élevée ({temp}°C)"));
                }
            }

            // Vérifier les pilotes
            var gpuDriverErrors = report.Errors.Count(e => 
                e.Section.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("driver", StringComparison.OrdinalIgnoreCase));
            if (gpuDriverErrors > 0) penalties.Add((10, "Problèmes de pilotes graphiques"));
        }

        private static void EvaluateRAM(HealthSection section, HealthReport report, List<(int, string)> penalties)
        {
            // Vérifier l'utilisation mémoire
            if (section.EvidenceData.TryGetValue("Total", out var totalStr) &&
                section.EvidenceData.TryGetValue("Disponible", out var availStr))
            {
                if (double.TryParse(totalStr.Replace("GB", "").Trim(), out var total) &&
                    double.TryParse(availStr.Replace("GB", "").Trim(), out var avail))
                {
                    var usedPercent = ((total - avail) / total) * 100;
                    if (usedPercent > 95) penalties.Add((30, "Mémoire presque saturée"));
                    else if (usedPercent > 85) penalties.Add((15, "Mémoire très utilisée"));
                    else if (usedPercent > 75) penalties.Add((5, "Utilisation mémoire élevée"));
                }
            }
        }

        private static void EvaluateStorage(HealthSection section, HealthReport report, List<(int, string)> penalties)
        {
            // Le stockage est critique - vérifier l'espace libre et la santé SMART
            if (section.EvidenceData.TryGetValue("EspaceLibre", out var freeStr))
            {
                if (double.TryParse(freeStr.Replace("GB", "").Replace("%", "").Trim(), out var free))
                {
                    if (free < 5) penalties.Add((40, "Espace disque critique (< 5 GB)"));
                    else if (free < 10) penalties.Add((25, "Espace disque faible (< 10 GB)"));
                    else if (free < 20) penalties.Add((10, "Espace disque limité (< 20 GB)"));
                }
            }

            // Vérifier les erreurs SMART
            var smartErrors = report.Errors.Count(e => 
                e.Section.Contains("Smart", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("disk", StringComparison.OrdinalIgnoreCase));
            if (smartErrors > 0) penalties.Add((25, "Problèmes de santé disque détectés"));
        }

        private static void EvaluateNetwork(HealthSection section, HealthReport report, List<(int, string)> penalties)
        {
            // Vérifier la connectivité
            if (section.CollectionStatus == "FAILED")
            {
                penalties.Add((30, "Problèmes de connectivité réseau"));
            }

            // Vérifier la latence si disponible
            if (section.EvidenceData.TryGetValue("Latency", out var latStr))
            {
                if (double.TryParse(latStr.Replace("ms", "").Trim(), out var latency))
                {
                    if (latency > 500) penalties.Add((20, "Latence réseau très élevée"));
                    else if (latency > 200) penalties.Add((10, "Latence réseau élevée"));
                }
            }
        }

        private static void EvaluateStability(HealthSection section, HealthReport report, List<(int, string)> penalties)
        {
            // Analyser les crashs et erreurs système
            var crashCount = report.ScoreV2.Breakdown.Critical;
            if (crashCount > 5) penalties.Add((40, $"Nombreux crashs système ({crashCount})"));
            else if (crashCount > 2) penalties.Add((20, $"Crashs système détectés ({crashCount})"));
            else if (crashCount > 0) penalties.Add((10, $"Crash système isolé ({crashCount})"));

            // Erreurs de collecte
            var collectorErrors = report.ScoreV2.Breakdown.CollectorErrors;
            if (collectorErrors > 3) penalties.Add((15, "Problèmes de collecte multiples"));
        }

        private static void EvaluateDrivers(HealthSection section, HealthReport report, List<(int, string)> penalties)
        {
            // Compter les problèmes de pilotes
            var driverErrors = report.Errors.Count(e => 
                e.Section.Contains("Driver", StringComparison.OrdinalIgnoreCase) ||
                e.Section.Contains("Device", StringComparison.OrdinalIgnoreCase));
            
            if (driverErrors > 5) penalties.Add((30, "Nombreux pilotes problématiques"));
            else if (driverErrors > 2) penalties.Add((15, "Quelques pilotes à mettre à jour"));
            else if (driverErrors > 0) penalties.Add((5, "Pilote à vérifier"));
        }

        #endregion

        #region Critical Penalties

        private static int ApplyCriticalPenalties(int rawScore, HealthReport report, GradeResult result)
        {
            int finalScore = rawScore;

            // Pénalité critique : données partielles
            if (report.Metadata.PartialFailure)
            {
                finalScore -= 10;
                result.CriticalPenalties.Add("Scan partiel - certaines données manquantes");
            }

            // Pénalité critique : erreurs critiques
            if (report.ScoreV2.Breakdown.Critical > 3)
            {
                finalScore -= 20;
                result.CriticalPenalties.Add("Nombreuses erreurs critiques détectées");
            }

            // Pénalité critique : timeouts
            if (report.ScoreV2.Breakdown.Timeouts > 2)
            {
                finalScore -= 10;
                result.CriticalPenalties.Add("Timeouts multiples pendant le scan");
            }

            return Math.Max(0, Math.Min(100, finalScore));
        }

        #endregion

        #region Grade Determination

        private static (string grade, string verdict) DetermineGrade(int score)
        {
            foreach (var (minScore, grade, verdict) in GradeThresholds)
            {
                if (score >= minScore)
                    return (grade, verdict);
            }
            return ("F", "Critique - Intervention urgente nécessaire");
        }

        private static string GenerateDomainStatus(int score)
        {
            return score switch
            {
                >= 90 => "Excellent",
                >= 70 => "Bon état",
                >= 50 => "À surveiller",
                >= 30 => "Problèmes détectés",
                _ => "Critique"
            };
        }

        private static string GenerateDomainExplanation(HealthDomain domain, int score, List<(int penalty, string reason)> penalties)
        {
            var domainName = domain switch
            {
                HealthDomain.OS => "Le système d'exploitation",
                HealthDomain.CPU => "Le processeur",
                HealthDomain.GPU => "La carte graphique",
                HealthDomain.RAM => "La mémoire vive",
                HealthDomain.Storage => "Le stockage",
                HealthDomain.Network => "Le réseau",
                HealthDomain.SystemStability => "La stabilité système",
                HealthDomain.Drivers => "Les pilotes",
                _ => "Ce composant"
            };

            var explanation = $"{domainName} a obtenu un score de {score}/100. ";

            if (penalties.Count == 0)
            {
                explanation += "Aucun problème détecté.";
            }
            else
            {
                explanation += $"Points d'attention : {string.Join(", ", penalties.Select(p => p.reason))}.";
            }

            return explanation;
        }

        private static List<string> GenerateExplanations(GradeResult result, HealthReport report)
        {
            var explanations = new List<string>();

            // Explication du score global
            explanations.Add($"Score global : {result.FinalScore}/100 (Grade {result.Grade})");
            explanations.Add(result.Verdict);

            // Top 3 des domaines les plus impactés
            var worstDomains = result.DomainDetails
                .Where(d => d.Value.HasData && d.Value.Penalties.Count > 0)
                .OrderByDescending(d => d.Value.Penalties.Sum(p => p.penalty))
                .Take(3);

            foreach (var (domain, score) in worstDomains)
            {
                var topPenalty = score.Penalties.OrderByDescending(p => p.penalty).FirstOrDefault();
                if (topPenalty.reason != null)
                {
                    explanations.Add($"• {domain}: {topPenalty.reason}");
                }
            }

            // Pénalités critiques
            foreach (var penalty in result.CriticalPenalties)
            {
                explanations.Add($"⚠️ {penalty}");
            }

            return explanations;
        }

        #endregion
    }

    #region Result Models

    /// <summary>
    /// Résultat du calcul de grade
    /// </summary>
    public class GradeResult
    {
        public int RawScore { get; set; }
        public int FinalScore { get; set; }
        public string Grade { get; set; } = "?";
        public string Verdict { get; set; } = "";
        public HealthSeverity Severity { get; set; }
        public Dictionary<HealthDomain, DomainScore> DomainDetails { get; set; } = new();
        public List<string> CriticalPenalties { get; set; } = new();
        public List<string> Explanations { get; set; } = new();
    }

    /// <summary>
    /// Score d'un domaine spécifique
    /// </summary>
    public class DomainScore
    {
        public HealthDomain Domain { get; set; }
        public int Score { get; set; }
        public bool HasData { get; set; }
        public string StatusMessage { get; set; } = "";
        public string Explanation { get; set; } = "";
        public List<(int penalty, string reason)> Penalties { get; set; } = new();
    }

    #endregion
}
