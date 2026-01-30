using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// PHASE 6: Service d'évaluation de la readiness pour AutoFix LLM.
    /// Classifie les problèmes en: Fixable (auto-fix safe), Suggest-only, Not enough data.
    /// </summary>
    public static class AutoFixReadinessService
    {
        #region Actionability Categories

        public enum ActionabilityLevel
        {
            /// <summary>Peut être corrigé automatiquement de façon sûre</summary>
            Fixable,
            
            /// <summary>Suggestion de correction, mais intervention utilisateur requise</summary>
            SuggestOnly,
            
            /// <summary>Pas assez de données pour agir</summary>
            NotEnoughData,
            
            /// <summary>Aucune action nécessaire</summary>
            NoActionNeeded
        }

        public class RemediationItem
        {
            public string IssueId { get; set; } = "";
            public string Description { get; set; } = "";
            public string Category { get; set; } = "";
            public ActionabilityLevel Actionability { get; set; }
            public string? SuggestedAction { get; set; }
            public string? SafetyNote { get; set; }
            public bool IsSafe { get; set; }
            public int ConfidenceRequired { get; set; } = 60;
        }

        public class RemediationReadiness
        {
            /// <summary>Score global de readiness LLM (0-100)</summary>
            public int ReadinessScore { get; set; }
            
            /// <summary>Peut-on déclencher AutoFix?</summary>
            public bool AutoFixAllowed { get; set; }
            
            /// <summary>Raison si AutoFix bloqué</summary>
            public string? BlockReason { get; set; }
            
            /// <summary>Items fixables automatiquement</summary>
            public List<RemediationItem> Fixable { get; set; } = new();
            
            /// <summary>Items suggestion seulement</summary>
            public List<RemediationItem> SuggestOnly { get; set; } = new();
            
            /// <summary>Items sans assez de données</summary>
            public List<RemediationItem> NotEnoughData { get; set; } = new();
            
            /// <summary>Règles safe explicitement documentées</summary>
            public List<string> SafeRules { get; set; } = new()
            {
                "Start-Service wuauserv (Windows Update service)",
                "Restart-Service spooler (Print Spooler)",
                "Clear-RecycleBin (Corbeille)",
                "Remove-Item $env:TEMP\\* -Recurse (Fichiers temporaires)",
                "sfc /scannow (Vérification intégrité système)",
                "DISM /Online /Cleanup-Image /RestoreHealth"
            };
        }

        #endregion

        #region Main Evaluation

        /// <summary>
        /// PHASE 6.1: Évalue la readiness pour AutoFix LLM.
        /// </summary>
        public static RemediationReadiness Evaluate(
            HealthReport report,
            CollectorDiagnosticsService.CollectorDiagnosticsResult diagnostics,
            int confidenceScore)
        {
            var result = new RemediationReadiness();
            
            // Safety Gate (PHASE 6.2)
            if (confidenceScore < 60)
            {
                result.AutoFixAllowed = false;
                result.BlockReason = $"Confiance trop faible ({confidenceScore}/100 < 60)";
                result.ReadinessScore = 0;
            }
            else if (diagnostics.CollectorErrorsLogical > 0 && !HasOnlySafeErrors(diagnostics))
            {
                result.AutoFixAllowed = false;
                result.BlockReason = $"Erreurs collecteur non-safe ({diagnostics.CollectorErrorsLogical})";
                result.ReadinessScore = 30;
            }
            else
            {
                result.AutoFixAllowed = true;
            }
            
            // Classifier les problèmes
            ClassifyIssues(result, report, diagnostics, confidenceScore);
            
            // Calculer le score de readiness
            result.ReadinessScore = CalculateReadinessScore(result, confidenceScore);
            
            App.LogMessage($"[AutoFixReadiness] Score={result.ReadinessScore}, AutoFix={(result.AutoFixAllowed ? "ALLOWED" : "BLOCKED")}, " +
                          $"Fixable={result.Fixable.Count}, SuggestOnly={result.SuggestOnly.Count}, NotEnoughData={result.NotEnoughData.Count}");
            
            return result;
        }

        #endregion

        #region Issue Classification

        private static void ClassifyIssues(
            RemediationReadiness result,
            HealthReport report,
            CollectorDiagnosticsService.CollectorDiagnosticsResult diagnostics,
            int confidenceScore)
        {
            // === WINDOWS UPDATE ===
            // Windows Update service STOP + pending updates => Fixable
            var updateIssues = report.Sections
                .Where(s => s.Domain == HealthDomain.OS)
                .SelectMany(s => s.Findings)
                .Where(f => f.Source?.Contains("Update", StringComparison.OrdinalIgnoreCase) == true ||
                           f.Description?.Contains("Update", StringComparison.OrdinalIgnoreCase) == true);
            
            foreach (var issue in updateIssues)
            {
                result.Fixable.Add(new RemediationItem
                {
                    IssueId = "WU_SERVICE",
                    Description = "Windows Update service arrêté ou mises à jour en attente",
                    Category = "Windows Update",
                    Actionability = ActionabilityLevel.Fixable,
                    SuggestedAction = "Start-Service wuauserv; Get-WindowsUpdate -Install -AcceptAll",
                    SafetyNote = "Redémarrer le service Windows Update est safe. L'installation des mises à jour nécessite confirmation.",
                    IsSafe = true,
                    ConfidenceRequired = 50
                });
            }
            
            // Vérifier les erreurs de mise à jour dans les erreurs PS
            foreach (var err in diagnostics.Errors.Where(e => 
                e.Code.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("update", StringComparison.OrdinalIgnoreCase)))
            {
                if (!result.Fixable.Any(f => f.IssueId == "WU_SERVICE"))
                {
                    result.Fixable.Add(new RemediationItem
                    {
                        IssueId = "WU_ERROR",
                        Description = $"Erreur Windows Update: {err.Message}",
                        Category = "Windows Update",
                        Actionability = ActionabilityLevel.Fixable,
                        SuggestedAction = "Start-Service wuauserv",
                        IsSafe = true
                    });
                }
            }
            
            // === THERMAL ISSUES ===
            // Disk thermal élevé => Suggest-only
            foreach (var section in report.Sections.Where(s => s.Domain == HealthDomain.Storage))
            {
                foreach (var finding in section.Findings.Where(f => 
                    f.Description?.Contains("temp", StringComparison.OrdinalIgnoreCase) == true ||
                    f.Description?.Contains("thermal", StringComparison.OrdinalIgnoreCase) == true))
                {
                    result.SuggestOnly.Add(new RemediationItem
                    {
                        IssueId = "DISK_THERMAL",
                        Description = finding.Description ?? "Température disque élevée",
                        Category = "Thermal",
                        Actionability = ActionabilityLevel.SuggestOnly,
                        SuggestedAction = "Vérifier ventilation, nettoyer poussière, vérifier emplacement du PC",
                        SafetyNote = "Intervention physique requise, non automatisable",
                        IsSafe = false
                    });
                }
            }
            
            // CPU thermal => Suggest-only ou NotEnoughData
            foreach (var section in report.Sections.Where(s => s.Domain == HealthDomain.CPU))
            {
                var cpuTempValid = diagnostics.InvalidatedMetrics.All(m => !m.Contains("CPU Temp"));
                
                foreach (var finding in section.Findings.Where(f => 
                    f.Description?.Contains("temp", StringComparison.OrdinalIgnoreCase) == true))
                {
                    if (cpuTempValid)
                    {
                        result.SuggestOnly.Add(new RemediationItem
                        {
                            IssueId = "CPU_THERMAL",
                            Description = finding.Description ?? "Température CPU élevée",
                            Category = "Thermal",
                            Actionability = ActionabilityLevel.SuggestOnly,
                            SuggestedAction = "Vérifier pâte thermique, ventilateur CPU, airflow boîtier",
                            IsSafe = false
                        });
                    }
                    else
                    {
                        result.NotEnoughData.Add(new RemediationItem
                        {
                            IssueId = "CPU_THERMAL_INVALID",
                            Description = "Température CPU invalide (capteur défaillant)",
                            Category = "Thermal",
                            Actionability = ActionabilityLevel.NotEnoughData,
                            SafetyNote = "Capteur invalide, impossible d'évaluer"
                        });
                    }
                }
            }
            
            // === MÉTRIQUES INVALIDÉES ===
            foreach (var invalid in diagnostics.InvalidatedMetrics)
            {
                result.NotEnoughData.Add(new RemediationItem
                {
                    IssueId = "INVALID_METRIC",
                    Description = invalid,
                    Category = "Collecte",
                    Actionability = ActionabilityLevel.NotEnoughData,
                    SafetyNote = "Métrique invalidée par DataSanitizer"
                });
            }
            
            // === DRIVERS ===
            foreach (var section in report.Sections.Where(s => s.Domain == HealthDomain.Drivers))
            {
                foreach (var finding in section.Findings)
                {
                    result.SuggestOnly.Add(new RemediationItem
                    {
                        IssueId = "DRIVER_ISSUE",
                        Description = finding.Description ?? "Problème de pilote",
                        Category = "Drivers",
                        Actionability = ActionabilityLevel.SuggestOnly,
                        SuggestedAction = "Mettre à jour le pilote via Windows Update ou le site du fabricant",
                        IsSafe = false
                    });
                }
            }
            
            // === DISK SPACE ===
            foreach (var section in report.Sections.Where(s => s.Domain == HealthDomain.Storage))
            {
                foreach (var finding in section.Findings.Where(f => 
                    f.Description?.Contains("espace", StringComparison.OrdinalIgnoreCase) == true ||
                    f.Description?.Contains("space", StringComparison.OrdinalIgnoreCase) == true))
                {
                    result.Fixable.Add(new RemediationItem
                    {
                        IssueId = "DISK_SPACE",
                        Description = finding.Description ?? "Espace disque faible",
                        Category = "Storage",
                        Actionability = ActionabilityLevel.Fixable,
                        SuggestedAction = "Clear-RecycleBin; Remove-Item $env:TEMP\\* -Recurse; cleanmgr /sagerun:1",
                        SafetyNote = "Nettoyage fichiers temporaires et corbeille est safe",
                        IsSafe = true,
                        ConfidenceRequired = 40
                    });
                }
            }
        }

        #endregion

        #region Helpers

        private static bool HasOnlySafeErrors(CollectorDiagnosticsService.CollectorDiagnosticsResult diagnostics)
        {
            // Les erreurs WMI de température sont considérées "safe" pour AutoFix
            return diagnostics.Errors.All(e => 
                e.Code.Contains("TEMP", StringComparison.OrdinalIgnoreCase) ||
                e.Code.Contains("WARN", StringComparison.OrdinalIgnoreCase));
        }

        private static int CalculateReadinessScore(RemediationReadiness result, int confidenceScore)
        {
            int score = confidenceScore;
            
            // Bonus pour items fixables
            score += result.Fixable.Count(f => f.IsSafe) * 5;
            
            // Malus pour NotEnoughData
            score -= result.NotEnoughData.Count * 10;
            
            // Malus si AutoFix bloqué
            if (!result.AutoFixAllowed)
                score = Math.Min(score, 40);
            
            return Math.Max(0, Math.Min(100, score));
        }

        #endregion

        #region TXT Output

        /// <summary>
        /// Génère le bloc TXT pour le rapport unifié
        /// </summary>
        public static void WriteTxtSection(System.Text.StringBuilder sb, RemediationReadiness readiness)
        {
            sb.AppendLine("  ┌─ LLM AUTOFIX READINESS ──────────────────────────────────────────────────┐");
            sb.AppendLine($"  │  Score Readiness    : {readiness.ReadinessScore}/100");
            sb.AppendLine($"  │  AutoFix            : {(readiness.AutoFixAllowed ? "✅ AUTORISÉ" : "❌ BLOQUÉ")}");
            
            if (!readiness.AutoFixAllowed && !string.IsNullOrEmpty(readiness.BlockReason))
            {
                sb.AppendLine($"  │  Raison blocage     : {readiness.BlockReason}");
            }
            
            sb.AppendLine("  │");
            sb.AppendLine($"  │  📗 Fixable (auto)   : {readiness.Fixable.Count} item(s)");
            foreach (var item in readiness.Fixable.Take(3))
            {
                sb.AppendLine($"  │    • {item.Description}");
            }
            
            sb.AppendLine($"  │  📙 Suggest-only     : {readiness.SuggestOnly.Count} item(s)");
            foreach (var item in readiness.SuggestOnly.Take(3))
            {
                sb.AppendLine($"  │    • {item.Description}");
            }
            
            sb.AppendLine($"  │  📕 Not enough data  : {readiness.NotEnoughData.Count} item(s)");
            foreach (var item in readiness.NotEnoughData.Take(3))
            {
                sb.AppendLine($"  │    • {item.Description}");
            }
            
            sb.AppendLine("  │");
            sb.AppendLine("  │  🔒 RÈGLES SAFE (AutoFix autorisé sans confirmation):");
            foreach (var rule in readiness.SafeRules.Take(4))
            {
                sb.AppendLine($"  │    ✓ {rule}");
            }
            
            sb.AppendLine("  └─────────────────────────────────────────────────────────────────────────────┘");
        }

        #endregion
    }
}
