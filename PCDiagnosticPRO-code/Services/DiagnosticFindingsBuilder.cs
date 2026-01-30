using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Findings normalisés pour LLM AutoFix : IssueType, Severity, Confidence, AutoFixPossible, RiskLevel.
    /// Safety Gate : bloquer auto fix si Confidence &lt; 60, collector errors &gt; 5, security data missing, SMART suspect + missing.
    /// </summary>
    public static class DiagnosticFindingsBuilder
    {
        /// <summary>
        /// Construit la liste de findings depuis le rapport et le JSON PS.
        /// </summary>
        public static List<DiagnosticFinding> Build(
            HealthReport report,
            JsonElement? psRoot,
            int dataReliabilityScore,
            int collectorErrorsLogical,
            IReadOnlyList<string> missingData)
        {
            var findings = new List<DiagnosticFinding>();

            try
            {
                bool hasSecurityData = !missingData.Any(m => m.Contains("Security", StringComparison.OrdinalIgnoreCase) || m.Contains("Defender", StringComparison.OrdinalIgnoreCase));
                bool hasSmartData = report.Sections.Any(s => s.Domain == HealthDomain.Storage && s.HasData);
                bool smartSuspect = report.Errors.Any(e => e.Code.Contains("SMART", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("SMART", StringComparison.OrdinalIgnoreCase));

                foreach (var section in report.Sections)
                {
                    foreach (var finding in section.Findings)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = section.Domain.ToString(),
                            Severity = finding.Severity.ToString(),
                            Confidence = Math.Min(100, dataReliabilityScore),
                            AutoFixPossible = false,
                            RiskLevel = finding.Severity >= HealthSeverity.Critical ? "High" : finding.Severity >= HealthSeverity.Degraded ? "Medium" : "Low",
                            Description = finding.Description,
                            Source = finding.Source
                        });
                    }
                }

                if (psRoot.HasValue)
                {
                    AddFindingsFromPs(psRoot.Value, findings, dataReliabilityScore);
                }

                foreach (var err in report.Errors)
                {
                    findings.Add(new DiagnosticFinding
                    {
                        IssueType = "CollectorError",
                        Severity = "High",
                        Confidence = 90,
                        AutoFixPossible = false,
                        RiskLevel = "Medium",
                        Description = $"[{err.Code}] {err.Message}",
                        Source = err.Section
                    });
                }

                AddWindowsUpdateFinding(report, findings, dataReliabilityScore);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[DiagnosticFindingsBuilder] Erreur: {ex.Message}");
            }
            return findings;
        }

        /// <summary>
        /// Safety Gate : bloquer auto fix si Confidence &lt; 60, collector errors &gt; 5, security data missing, SMART suspect + missing.
        /// </summary>
        public static (bool Allowed, string? BlockReason) EvaluateSafetyGate(
            int confidenceScore,
            int collectorErrorsLogical,
            IReadOnlyList<string> missingData,
            bool hasSecurityData,
            bool smartSuspectAndMissingData)
        {
            if (confidenceScore < 60)
                return (false, $"Confiance trop faible ({confidenceScore}/100 < 60)");
            if (collectorErrorsLogical > 5)
                return (false, $"Erreurs collecteur trop nombreuses ({collectorErrorsLogical} > 5)");
            if (!hasSecurityData)
                return (false, "Données sécurité manquantes");
            if (smartSuspectAndMissingData)
                return (false, "SMART suspect et données manquantes");
            return (true, null);
        }

        private static void AddFindingsFromPs(JsonElement root, List<DiagnosticFinding> findings, int confidence)
        {
            try
            {
                if (root.TryGetProperty("sections", out var sections) && sections.TryGetProperty("WindowsUpdate", out var wu) && wu.TryGetProperty("data", out var wuData))
                {
                    if (wuData.TryGetProperty("pendingCount", out var pending) && pending.GetInt32() > 0)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "WindowsUpdate",
                            Severity = "Medium",
                            Confidence = confidence,
                            AutoFixPossible = true,
                            RiskLevel = "Low",
                            Description = "Mises à jour Windows en attente",
                            SuggestedAction = "Start-Service wuauserv; Installer les mises à jour",
                            Source = "WindowsUpdate"
                        });
                    }
                }
            }
            catch { /* ignore */ }
        }

        private static void AddWindowsUpdateFinding(HealthReport report, List<DiagnosticFinding> findings, int confidence)
        {
            var osSection = report.Sections.FirstOrDefault(s => s.Domain == HealthDomain.OS);
            if (osSection?.EvidenceData != null && osSection.EvidenceData.TryGetValue("UpdateStatus", out var status) &&
                (status.Contains("obsolète", StringComparison.OrdinalIgnoreCase) || status.Contains("pending", StringComparison.OrdinalIgnoreCase)))
            {
                if (findings.All(f => f.IssueType != "WindowsUpdate"))
                {
                    findings.Add(new DiagnosticFinding
                    {
                        IssueType = "WindowsUpdate",
                        Severity = "Medium",
                        Confidence = confidence,
                        AutoFixPossible = true,
                        RiskLevel = "Low",
                        Description = "Windows : mises à jour en attente ou système obsolète",
                        SuggestedAction = "Démarrer le service Windows Update et installer les mises à jour",
                        Source = "OS"
                    });
                }
            }
        }
    }
}
