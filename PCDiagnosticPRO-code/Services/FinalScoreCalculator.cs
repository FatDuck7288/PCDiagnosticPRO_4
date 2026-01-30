using System;
using System.Collections.Generic;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// PHASE 5: Calcul du FinalScore combiné (PS + C#) avec confidence gating.
    /// Remplace l'écrasement pur par GradeEngine avec une formule adaptative.
    /// </summary>
    public static class FinalScoreCalculator
    {
        #region Score Result Model

        public class FinalScoreResult
        {
            /// <summary>Score brut calculé par GradeEngine C#</summary>
            public int ScoreCSharp { get; set; }
            
            /// <summary>Grade calculé par GradeEngine C#</summary>
            public string GradeCSharp { get; set; } = "?";
            
            /// <summary>Score brut du PowerShell (scoreV2)</summary>
            public int ScoreV2PS { get; set; }
            
            /// <summary>Grade du PowerShell</summary>
            public string GradePS { get; set; } = "?";
            
            /// <summary>Score de confiance (qualité de la collecte)</summary>
            public int ConfidenceScore { get; set; }
            
            /// <summary>Score final combiné (formule adaptative)</summary>
            public int FinalScore { get; set; }
            
            /// <summary>Grade final (basé sur FinalScore)</summary>
            public string FinalGrade { get; set; } = "?";
            
            /// <summary>Verdict utilisateur</summary>
            public string Verdict { get; set; } = "";
            
            /// <summary>Cap appliqué (si applicable)</summary>
            public string? AppliedCap { get; set; }
            
            /// <summary>Formule utilisée pour le calcul</summary>
            public string FormulaUsed { get; set; } = "";
            
            /// <summary>Détails du calcul pour audit</summary>
            public List<string> CalculationDetails { get; set; } = new();
        }

        #endregion

        #region Main Calculation

        /// <summary>
        /// PHASE 5.1: Calcule le FinalScore avec la formule adaptative.
        /// 
        /// Formule:
        /// - Si ConfidenceScore >= 80: FinalScore = 0.6*Score_CSharp + 0.4*ScoreV2_PS
        /// - Si ConfidenceScore >= 60: FinalScore = 0.5*Score_CSharp + 0.5*ScoreV2_PS
        /// - Sinon: FinalScore = 0.3*Score_CSharp + 0.7*ScoreV2_PS
        /// </summary>
        public static FinalScoreResult Calculate(
            int scoreCSharp,
            string gradeCSharp,
            int scoreV2PS,
            string gradePS,
            int confidenceScore,
            CollectorDiagnosticsService.CollectorDiagnosticsResult diagnostics)
        {
            var result = new FinalScoreResult
            {
                ScoreCSharp = scoreCSharp,
                GradeCSharp = gradeCSharp,
                ScoreV2PS = scoreV2PS,
                GradePS = gradePS,
                ConfidenceScore = confidenceScore
            };
            
            // Étape 1: Appliquer la formule adaptative
            double weightCSharp, weightPS;
            
            if (confidenceScore >= 80)
            {
                weightCSharp = 0.6;
                weightPS = 0.4;
                result.FormulaUsed = "Confiance élevée: 60% C# + 40% PS";
            }
            else if (confidenceScore >= 60)
            {
                weightCSharp = 0.5;
                weightPS = 0.5;
                result.FormulaUsed = "Confiance moyenne: 50% C# + 50% PS";
            }
            else
            {
                weightCSharp = 0.3;
                weightPS = 0.7;
                result.FormulaUsed = "Confiance faible: 30% C# + 70% PS (priorité données brutes)";
            }
            
            result.CalculationDetails.Add($"Score C# (GradeEngine): {scoreCSharp}/100");
            result.CalculationDetails.Add($"Score PS (ScoreV2): {scoreV2PS}/100");
            result.CalculationDetails.Add($"Confiance: {confidenceScore}/100");
            result.CalculationDetails.Add($"Formule: {result.FormulaUsed}");
            
            double rawFinal = (scoreCSharp * weightCSharp) + (scoreV2PS * weightPS);
            result.CalculationDetails.Add($"Score brut: ({scoreCSharp} * {weightCSharp}) + ({scoreV2PS} * {weightPS}) = {rawFinal:F1}");
            
            // Étape 2: Appliquer les caps de sécurité (PHASE 5.2)
            int cappedScore = (int)Math.Round(rawFinal);
            
            // Cap 1: Erreurs collecteur
            if (diagnostics.CollectorErrorsLogical > 0)
            {
                if (cappedScore > 70)
                {
                    result.AppliedCap = $"Cap 70 (erreurs collecteur: {diagnostics.CollectorErrorsLogical})";
                    cappedScore = 70;
                    result.CalculationDetails.Add($"⚠️ Cap appliqué: {result.AppliedCap}");
                }
            }
            
            // Cap 2: Données manquantes critiques
            var criticalMissing = diagnostics.MissingDataNormalized.Count > 3;
            if (criticalMissing && cappedScore > 75)
            {
                result.AppliedCap = $"Cap 75 (données manquantes: {diagnostics.MissingDataNormalized.Count})";
                cappedScore = 75;
                result.CalculationDetails.Add($"⚠️ Cap appliqué: {result.AppliedCap}");
            }
            
            // Cap 3: Collecte échouée
            if (diagnostics.CollectionStatus == "FAILED" && cappedScore > 60)
            {
                result.AppliedCap = "Cap 60 (collecte échouée)";
                cappedScore = 60;
                result.CalculationDetails.Add($"⚠️ Cap appliqué: {result.AppliedCap}");
            }
            
            result.FinalScore = Math.Max(0, Math.Min(100, cappedScore));
            result.CalculationDetails.Add($"Score final: {result.FinalScore}/100");
            
            // Étape 3: Calculer le grade final (PHASE 5.3)
            result.FinalGrade = ScoreToGrade(result.FinalScore);
            result.Verdict = ScoreToVerdict(result.FinalScore);
            
            App.LogMessage($"[FinalScoreCalculator] C#={scoreCSharp}, PS={scoreV2PS}, Conf={confidenceScore} => Final={result.FinalScore} ({result.FinalGrade})");
            
            return result;
        }

        #endregion

        #region Grade Conversion

        private static string ScoreToGrade(int score)
        {
            return score switch
            {
                >= 95 => "A+",
                >= 90 => "A",
                >= 80 => "B+",
                >= 70 => "B",
                >= 60 => "C",
                >= 50 => "D",
                _ => "F"
            };
        }

        private static string ScoreToVerdict(int score)
        {
            return score switch
            {
                >= 95 => "Excellent - Votre PC est en parfait état",
                >= 90 => "Très bien - Votre PC fonctionne optimalement",
                >= 80 => "Bien - Quelques optimisations mineures possibles",
                >= 70 => "Correct - Attention recommandée sur certains points",
                >= 60 => "Dégradé - Des problèmes affectent les performances",
                >= 50 => "Critique - Intervention recommandée rapidement",
                _ => "Critique - Intervention urgente nécessaire"
            };
        }

        #endregion

        #region Report Update

        /// <summary>
        /// Met à jour le HealthReport avec le FinalScore calculé.
        /// Garde Score_CSharp et ScoreV2_PS visibles pour l'audit.
        /// </summary>
        public static void ApplyToReport(HealthReport report, FinalScoreResult result)
        {
            // Sauvegarder les scores individuels dans le rapport pour audit
            report.Divergence = new ScoreDivergence
            {
                PowerShellScore = result.ScoreV2PS,
                PowerShellGrade = result.GradePS,
                GradeEngineScore = result.ScoreCSharp,
                GradeEngineGrade = result.GradeCSharp,
                // Delta is computed automatically from GradeEngineScore - PowerShellScore
                SourceOfTruth = "FinalScore (moyenne pondérée + confidence gating)"
            };
            
            // Appliquer le score final à l'UI
            report.GlobalScore = result.FinalScore;
            report.Grade = result.FinalGrade;
            report.GlobalMessage = result.Verdict;
            report.GlobalSeverity = HealthReport.ScoreToSeverity(result.FinalScore);
            
            // Mettre à jour le ConfidenceModel
            if (report.ConfidenceModel != null)
            {
                report.ConfidenceModel.ConfidenceScore = result.ConfidenceScore;
            }
        }

        #endregion
    }
}
