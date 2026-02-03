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
    /// Génère 24+ types de findings automatiques pour permettre l'auto-remédiation.
    /// </summary>
    public static class DiagnosticFindingsBuilder
    {
        #region JSON Helpers
        
        /// <summary>
        /// Helper pour extraire un entier du JSON de façon sécurisée
        /// </summary>
        private static bool TryGetInt(JsonElement root, out int value, params string[] path)
        {
            value = 0;
            JsonElement current = root;
            
            foreach (var key in path)
            {
                if (!current.TryGetProperty(key, out current))
                    return false;
            }
            
            if (current.ValueKind == JsonValueKind.Number)
            {
                value = current.GetInt32();
                return true;
            }
            else if (current.ValueKind == JsonValueKind.String)
            {
                return int.TryParse(current.GetString(), out value);
            }
            
            return false;
        }

        /// <summary>
        /// Helper pour extraire un double du JSON de façon sécurisée
        /// </summary>
        private static bool TryGetDouble(JsonElement root, out double value, params string[] path)
        {
            value = 0.0;
            JsonElement current = root;
            
            foreach (var key in path)
            {
                if (!current.TryGetProperty(key, out current))
                    return false;
            }
            
            if (current.ValueKind == JsonValueKind.Number)
            {
                value = current.GetDouble();
                return true;
            }
            else if (current.ValueKind == JsonValueKind.String)
            {
                return double.TryParse(current.GetString(), out value);
            }
            
            return false;
        }

        /// <summary>
        /// Helper pour extraire un booléen du JSON de façon sécurisée
        /// </summary>
        private static bool TryGetBool(JsonElement root, out bool value, params string[] path)
        {
            value = false;
            JsonElement current = root;
            
            foreach (var key in path)
            {
                if (!current.TryGetProperty(key, out current))
                    return false;
            }
            
            if (current.ValueKind == JsonValueKind.True || current.ValueKind == JsonValueKind.False)
            {
                value = current.GetBoolean();
                return true;
            }
            
            return false;
        }

        /// <summary>
        /// Helper pour extraire une string du JSON de façon sécurisée
        /// </summary>
        private static bool TryGetString(JsonElement root, out string value, params string[] path)
        {
            value = string.Empty;
            JsonElement current = root;
            
            foreach (var key in path)
            {
                if (!current.TryGetProperty(key, out current))
                    return false;
            }
            
            if (current.ValueKind == JsonValueKind.String)
            {
                value = current.GetString() ?? string.Empty;
                return !string.IsNullOrEmpty(value);
            }
            
            return false;
        }
        
        #endregion
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
                // Appeler toutes les méthodes de détection (24+ types de findings)
                AddPerformanceFindings(root, findings, confidence);
                AddSecurityFindings(root, findings, confidence);
                AddStabilityFindings(root, findings, confidence);
                AddNetworkFindings(root, findings, confidence);
                AddStorageFindings(root, findings, confidence);
                AddTemperatureFindings(root, findings, confidence);
                AddDriverFindings(root, findings, confidence);
                
                // Garder la logique Windows Update existante (compatibilité)
                if (root.TryGetProperty("sections", out var sections) && 
                    sections.TryGetProperty("WindowsUpdate", out var wu) && 
                    wu.TryGetProperty("data", out var wuData))
                {
                    if (wuData.TryGetProperty("pendingCount", out var pending) && pending.GetInt32() > 0)
                    {
                        // Vérifier si déjà ajouté par AddSecurityFindings
                        if (!findings.Any(f => f.IssueType == "WindowsUpdate"))
                        {
                            findings.Add(new DiagnosticFinding
                            {
                                IssueType = "WindowsUpdate",
                                Severity = "Medium",
                                Confidence = confidence,
                                AutoFixPossible = true,
                                RiskLevel = "Low",
                                Description = "Mises à jour Windows en attente",
                                SuggestedAction = "Install-WindowsUpdate -AcceptAll -AutoReboot",
                                Source = "WindowsUpdate"
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[DiagnosticFindingsBuilder] Erreur AddFindingsFromPs: {ex.Message}");
            }
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

        #region Performance Findings

        /// <summary>
        /// Détecte les problèmes de performance (CPU, RAM, Disque, Services, Processus)
        /// </summary>
        private static void AddPerformanceFindings(JsonElement root, List<DiagnosticFinding> findings, int confidence)
        {
            try
            {
                if (!root.TryGetProperty("sections", out var sections))
                    return;

                // 1. CPU élevé (> 80%)
                if (TryGetDouble(root, out var cpuAvg, "sections", "DynamicSignals", "data", "cpu", "average"))
                {
                    if (cpuAvg > 80)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "HighCpuUsage",
                            Severity = "Medium",
                            Confidence = Math.Min(90, confidence),
                            AutoFixPossible = true,
                            RiskLevel = "Low",
                            Description = $"Utilisation CPU élevée: {cpuAvg:F1}% (normal < 70%)",
                            SuggestedAction = "Get-Process | Sort-Object CPU -Descending | Select-Object -First 10 Name,CPU,WorkingSet | Format-Table",
                            Source = "DynamicSignals"
                        });
                    }
                }

                // 2. RAM saturée (> 85%)
                if (TryGetDouble(root, out var memUsedPercent, "sections", "MemoryInfo", "data", "UsedPercent"))
                {
                    if (memUsedPercent > 85)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "HighMemoryUsage",
                            Severity = memUsedPercent > 95 ? "High" : "Medium",
                            Confidence = 95,
                            AutoFixPossible = true,
                            RiskLevel = "Low",
                            Description = $"Utilisation mémoire élevée: {memUsedPercent:F1}% (recommandé < 85%)",
                            SuggestedAction = "Get-Process | Sort-Object WorkingSet -Descending | Select-Object -First 10 | ForEach-Object { $_.CloseMainWindow(); Start-Sleep 1 }",
                            Source = "MemoryInfo"
                        });
                    }
                }

                // 3. Disque plein (> 90%)
                if (sections.TryGetProperty("Disks", out var disksSection) &&
                    disksSection.TryGetProperty("data", out var disksData) &&
                    disksData.TryGetProperty("disks", out var disksArray) &&
                    disksArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var disk in disksArray.EnumerateArray())
                    {
                        if (disk.TryGetProperty("DriveLetter", out var letterEl) &&
                            disk.TryGetProperty("UsedPercent", out var usedEl) &&
                            usedEl.ValueKind == JsonValueKind.Number)
                        {
                            var letter = letterEl.GetString();
                            var usedPercent = usedEl.GetDouble();

                            if (usedPercent > 90)
                            {
                                findings.Add(new DiagnosticFinding
                                {
                                    IssueType = "HighDiskUsage",
                                    Severity = usedPercent > 95 ? "Critical" : "High",
                                    Confidence = 98,
                                    AutoFixPossible = true,
                                    RiskLevel = "Low",
                                    Description = $"Disque {letter} à {usedPercent:F1}% de capacité",
                                    SuggestedAction = $"Remove-Item $env:TEMP\\* -Recurse -Force -ErrorAction SilentlyContinue; Clear-RecycleBin -DriveLetter {letter?.TrimEnd(':')} -Force -ErrorAction SilentlyContinue; cleanmgr /sagerun:1",
                                    Source = "Disks"
                                });
                            }
                        }
                    }
                }

                // 4. File d'attente disque élevée (> 2)
                if (TryGetDouble(root, out var queueLength, "sections", "DynamicSignals", "data", "disk", "queueLength"))
                {
                    if (queueLength > 2)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "HighDiskQueue",
                            Severity = "Medium",
                            Confidence = 85,
                            AutoFixPossible = false,
                            RiskLevel = "N/A",
                            Description = $"File d'attente disque élevée: {queueLength:F1} (normal < 2)",
                            SuggestedAction = "Vérifier santé SMART du disque, défragmenter si HDD, envisager upgrade vers SSD",
                            Source = "DynamicSignals"
                        });
                    }
                }

                // 5. Services critiques arrêtés
                var criticalServices = new[] { "wuauserv", "Winmgmt", "EventLog", "Dhcp", "Dnscache", "LanmanServer", "LanmanWorkstation" };
                
                if (sections.TryGetProperty("Services", out var servicesSection) &&
                    servicesSection.TryGetProperty("data", out var servicesData) &&
                    servicesData.TryGetProperty("services", out var servicesArray) &&
                    servicesArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var service in servicesArray.EnumerateArray())
                    {
                        if (service.TryGetProperty("Name", out var nameEl) &&
                            service.TryGetProperty("Status", out var statusEl))
                        {
                            var name = nameEl.GetString();
                            var status = statusEl.GetString();

                            if (criticalServices.Contains(name, StringComparer.OrdinalIgnoreCase) &&
                                status?.Equals("Stopped", StringComparison.OrdinalIgnoreCase) == true)
                            {
                                findings.Add(new DiagnosticFinding
                                {
                                    IssueType = "CriticalServiceStopped",
                                    Severity = "Critical",
                                    Confidence = 100,
                                    AutoFixPossible = true,
                                    RiskLevel = "Low",
                                    Description = $"Service critique arrêté: {name}",
                                    SuggestedAction = $"Start-Service {name} -ErrorAction SilentlyContinue; Set-Service {name} -StartupType Automatic",
                                    Source = "Services"
                                });
                            }
                        }
                    }
                }

                // 6. Trop de processus (> 200)
                if (TryGetInt(root, out var processCount, "sections", "Processes", "data", "totalCount"))
                {
                    if (processCount > 200)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "TooManyProcesses",
                            Severity = "Low",
                            Confidence = 70,
                            AutoFixPossible = true,
                            RiskLevel = "Medium",
                            Description = $"{processCount} processus en cours (normal: 80-150)",
                            SuggestedAction = "Désactiver programmes au démarrage inutiles via msconfig ou Gestionnaire des tâches > Démarrage",
                            Source = "Processes"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[DiagnosticFindingsBuilder] Erreur AddPerformanceFindings: {ex.Message}");
            }
        }

        #endregion

        #region Security Findings

        /// <summary>
        /// Détecte les problèmes de sécurité (Defender, UAC, Firewall, Updates, EventLogs)
        /// </summary>
        private static void AddSecurityFindings(JsonElement root, List<DiagnosticFinding> findings, int confidence)
        {
            try
            {
                if (!root.TryGetProperty("sections", out var sections))
                    return;

                // 7. Windows Defender désactivé
                if (TryGetBool(root, out var defenderEnabled, "sections", "Security", "data", "defenderEnabled"))
                {
                    if (!defenderEnabled)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "DefenderDisabled",
                            Severity = "Critical",
                            Confidence = 100,
                            AutoFixPossible = true,
                            RiskLevel = "Low",
                            Description = "Windows Defender est désactivé",
                            SuggestedAction = "Set-MpPreference -DisableRealtimeMonitoring $false; Update-MpSignature; Start-MpScan -ScanType QuickScan",
                            Source = "Security"
                        });
                    }
                }

                // 8. UAC désactivé
                if (TryGetBool(root, out var uacEnabled, "sections", "Security", "data", "uacEnabled"))
                {
                    if (!uacEnabled)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "UacDisabled",
                            Severity = "High",
                            Confidence = 100,
                            AutoFixPossible = true,
                            RiskLevel = "Medium",
                            Description = "UAC (Contrôle de compte utilisateur) désactivé",
                            SuggestedAction = "Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System' -Name 'EnableLUA' -Value 1; Write-Host 'Redémarrage requis'",
                            Source = "Security"
                        });
                    }
                }

                // 9. Firewall désactivé (vérifier chaque profil)
                if (sections.TryGetProperty("Security", out var secSection) &&
                    secSection.TryGetProperty("data", out var secData) &&
                    secData.TryGetProperty("firewallProfiles", out var profiles))
                {
                    var disabledProfiles = new List<string>();
                    
                    if (profiles.TryGetProperty("Domain", out var domain) && domain.ValueKind == JsonValueKind.False)
                        disabledProfiles.Add("Domain");
                    if (profiles.TryGetProperty("Private", out var priv) && priv.ValueKind == JsonValueKind.False)
                        disabledProfiles.Add("Private");
                    if (profiles.TryGetProperty("Public", out var pub) && pub.ValueKind == JsonValueKind.False)
                        disabledProfiles.Add("Public");

                    if (disabledProfiles.Count > 0)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "FirewallDisabled",
                            Severity = "Critical",
                            Confidence = 100,
                            AutoFixPossible = true,
                            RiskLevel = "Low",
                            Description = $"Pare-feu Windows désactivé pour: {string.Join(", ", disabledProfiles)}",
                            SuggestedAction = $"Set-NetFirewallProfile -Profile {string.Join(",", disabledProfiles)} -Enabled True",
                            Source = "Security"
                        });
                    }
                }

                // 10. Mises à jour critiques en attente (> 10)
                if (TryGetInt(root, out var pendingCount, "sections", "WindowsUpdate", "data", "pendingCount"))
                {
                    if (pendingCount > 10)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "CriticalUpdates",
                            Severity = "High",
                            Confidence = 95,
                            AutoFixPossible = true,
                            RiskLevel = "Low",
                            Description = $"{pendingCount} mises à jour Windows en attente",
                            SuggestedAction = "Install-Module PSWindowsUpdate -Force; Get-WindowsUpdate -Install -AcceptAll -AutoReboot",
                            Source = "WindowsUpdate"
                        });
                    }
                }

                // 11. Windows Update pas vérifié depuis longtemps (> 30 jours)
                if (TryGetInt(root, out var lastCheckDays, "sections", "WindowsUpdate", "data", "lastCheckDays"))
                {
                    if (lastCheckDays > 30)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "UpdateCheckOverdue",
                            Severity = "Medium",
                            Confidence = 90,
                            AutoFixPossible = true,
                            RiskLevel = "Low",
                            Description = $"Dernière vérification Windows Update: il y a {lastCheckDays} jours",
                            SuggestedAction = "Start-Service wuauserv; (New-Object -ComObject Microsoft.Update.AutoUpdate).DetectNow()",
                            Source = "WindowsUpdate"
                        });
                    }
                }

                // 12. Événements critiques système élevés (> 50 derniers 7 jours)
                int totalCritical = 0;
                
                if (sections.TryGetProperty("EventLogs", out var logsSection) &&
                    logsSection.TryGetProperty("data", out var logsData) &&
                    logsData.TryGetProperty("logs", out var logs))
                {
                    if (logs.TryGetProperty("System", out var systemLog) &&
                        systemLog.TryGetProperty("criticalCount", out var sysCrit))
                    {
                        totalCritical += sysCrit.GetInt32();
                    }
                    
                    if (logs.TryGetProperty("Application", out var appLog) &&
                        appLog.TryGetProperty("criticalCount", out var appCrit))
                    {
                        totalCritical += appCrit.GetInt32();
                    }

                    if (totalCritical > 50)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "HighCriticalEvents",
                            Severity = "Medium",
                            Confidence = 80,
                            AutoFixPossible = false,
                            RiskLevel = "N/A",
                            Description = $"{totalCritical} événements critiques détectés dans Event Viewer (7 derniers jours)",
                            SuggestedAction = "Ouvrir Event Viewer (eventvwr.msc) > Journaux Windows > Système et Application, analyser événements critiques récurrents",
                            Source = "EventLogs"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[DiagnosticFindingsBuilder] Erreur AddSecurityFindings: {ex.Message}");
            }
        }

        #endregion

        #region Stability Findings

        /// <summary>
        /// Détecte les problèmes de stabilité (Points restauration, Crashes, Blue Screens)
        /// </summary>
        private static void AddStabilityFindings(JsonElement root, List<DiagnosticFinding> findings, int confidence)
        {
            try
            {
                if (!root.TryGetProperty("sections", out var sections))
                    return;

                // 13. Pas de point de restauration récent (> 30 jours ou aucun)
                if (TryGetInt(root, out var rpCount, "sections", "RestorePoints", "data", "restorePointCount"))
                {
                    if (rpCount == 0)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "NoRecentRestorePoint",
                            Severity = "Medium",
                            Confidence = 100,
                            AutoFixPossible = true,
                            RiskLevel = "Low",
                            Description = "Aucun point de restauration système",
                            SuggestedAction = "Enable-ComputerRestore -Drive 'C:\\'; Checkpoint-Computer -Description 'PCDiagnostic_Manual_Checkpoint' -RestorePointType 'MODIFY_SETTINGS'",
                            Source = "RestorePoints"
                        });
                    }
                    else if (TryGetInt(root, out var lastRpDays, "sections", "RestorePoints", "data", "lastPointAgeDays"))
                    {
                        if (lastRpDays > 30)
                        {
                            findings.Add(new DiagnosticFinding
                            {
                                IssueType = "NoRecentRestorePoint",
                                Severity = "Medium",
                                Confidence = 100,
                                AutoFixPossible = true,
                                RiskLevel = "Low",
                                Description = $"Dernier point de restauration: il y a {lastRpDays} jours",
                                SuggestedAction = "Checkpoint-Computer -Description 'PCDiagnostic_Manual_Checkpoint' -RestorePointType 'MODIFY_SETTINGS'",
                                Source = "RestorePoints"
                            });
                        }
                    }
                }

                // 14. Crashes d'applications fréquents (> 10 derniers 30 jours)
                if (TryGetInt(root, out var appCrashes, "sections", "ReliabilityHistory", "data", "appCrashes"))
                {
                    if (appCrashes > 10)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "FrequentAppCrashes",
                            Severity = "Medium",
                            Confidence = 85,
                            AutoFixPossible = false,
                            RiskLevel = "N/A",
                            Description = $"{appCrashes} crashes d'applications détectés (30 derniers jours)",
                            SuggestedAction = "Ouvrir Reliability Monitor (perfmon /rel), identifier applications problématiques, mettre à jour ou désinstaller",
                            Source = "ReliabilityHistory"
                        });
                    }
                }

                // 15. Blue screens multiples (> 3 dumps mémoire)
                if (TryGetInt(root, out var minidumpCount, "sections", "MinidumpAnalysis", "data", "minidumpCount"))
                {
                    if (minidumpCount > 3)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "SystemCrashes",
                            Severity = "High",
                            Confidence = 90,
                            AutoFixPossible = false,
                            RiskLevel = "N/A",
                            Description = $"{minidumpCount} dumps mémoire détectés (Blue Screen of Death / BSOD)",
                            SuggestedAction = "Analyser dumps avec WinDbg, vérifier pilotes récemment installés, tester mémoire RAM avec Windows Memory Diagnostic (mdsched.exe)",
                            Source = "MinidumpAnalysis"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[DiagnosticFindingsBuilder] Erreur AddStabilityFindings: {ex.Message}");
            }
        }

        #endregion

        #region Network Findings

        /// <summary>
        /// Détecte les problèmes réseau (Latence, Perte paquets, DNS)
        /// </summary>
        private static void AddNetworkFindings(JsonElement root, List<DiagnosticFinding> findings, int confidence)
        {
            try
            {
                if (!root.TryGetProperty("sections", out var sections))
                    return;

                // 16. Latence réseau élevée (> 100ms)
                if (TryGetDouble(root, out var latencyP95, "sections", "Network", "data", "latencyP95"))
                {
                    if (latencyP95 > 100)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "HighNetworkLatency",
                            Severity = latencyP95 > 200 ? "High" : "Medium",
                            Confidence = 85,
                            AutoFixPossible = true,
                            RiskLevel = "Low",
                            Description = $"Latence réseau P95: {latencyP95:F1}ms (normal < 50ms)",
                            SuggestedAction = "ipconfig /flushdns; netsh winsock reset; netsh int ip reset; Test-Connection 8.8.8.8 -Count 10",
                            Source = "Network"
                        });
                    }
                }

                // 17. Perte de paquets (> 1%)
                if (TryGetDouble(root, out var packetLoss, "sections", "Network", "data", "packetLoss"))
                {
                    if (packetLoss > 1.0)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "PacketLoss",
                            Severity = packetLoss > 5.0 ? "High" : "Medium",
                            Confidence = 80,
                            AutoFixPossible = true,
                            RiskLevel = "Low",
                            Description = $"Perte de paquets réseau: {packetLoss:F1}%",
                            SuggestedAction = "Vérifier câble réseau Ethernet, redémarrer routeur/modem, mettre à jour pilote carte réseau via Gestionnaire de périphériques",
                            Source = "Network"
                        });
                    }
                }

                // 18. DNS lent (> 100ms)
                if (TryGetDouble(root, out var dnsP95, "sections", "Network", "data", "dnsDurationP95"))
                {
                    if (dnsP95 > 100)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "SlowDns",
                            Severity = "Low",
                            Confidence = 75,
                            AutoFixPossible = true,
                            RiskLevel = "Low",
                            Description = $"Résolution DNS lente: {dnsP95:F0}ms P95 (recommandé < 50ms)",
                            SuggestedAction = "Changer DNS vers Google (8.8.8.8 / 8.8.4.4) ou Cloudflare (1.1.1.1 / 1.0.0.1): Set-DnsClientServerAddress -InterfaceAlias 'Ethernet' -ServerAddresses ('8.8.8.8','8.8.4.4')",
                            Source = "Network"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[DiagnosticFindingsBuilder] Erreur AddNetworkFindings: {ex.Message}");
            }
        }

        #endregion

        #region Storage Findings

        /// <summary>
        /// Détecte les problèmes de stockage (SMART, Température disques)
        /// </summary>
        private static void AddStorageFindings(JsonElement root, List<DiagnosticFinding> findings, int confidence)
        {
            try
            {
                if (!root.TryGetProperty("sections", out var sections))
                    return;

                // 19 & 20. Disques SMART en avertissement ou température élevée
                if (sections.TryGetProperty("SmartDetails", out var smartSection) &&
                    smartSection.TryGetProperty("data", out var smartData) &&
                    smartData.TryGetProperty("disks", out var disksArray) &&
                    disksArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var disk in disksArray.EnumerateArray())
                    {
                        if (disk.TryGetProperty("model", out var modelEl))
                        {
                            var model = modelEl.GetString() ?? "Disque inconnu";
                            
                            // 19. SMART Warning
                            if (disk.TryGetProperty("health", out var healthEl))
                            {
                                var health = healthEl.GetString();
                                if (health?.Equals("Warning", StringComparison.OrdinalIgnoreCase) == true ||
                                    health?.Equals("Caution", StringComparison.OrdinalIgnoreCase) == true ||
                                    health?.Equals("Bad", StringComparison.OrdinalIgnoreCase) == true)
                                {
                                    findings.Add(new DiagnosticFinding
                                    {
                                        IssueType = "SmartWarning",
                                        Severity = "Critical",
                                        Confidence = 95,
                                        AutoFixPossible = false,
                                        RiskLevel = "N/A",
                                        Description = $"Disque {model}: SMART indique problème ({health})",
                                        SuggestedAction = "URGENT: Sauvegarder immédiatement toutes données importantes, planifier remplacement disque dès que possible",
                                        Source = "SmartDetails"
                                    });
                                }
                            }
                            
                            // 20. Température disque élevée
                            if (disk.TryGetProperty("temperature", out var tempEl) &&
                                tempEl.ValueKind == JsonValueKind.Number)
                            {
                                var temp = tempEl.GetInt32();
                                if (temp > 50)
                                {
                                    findings.Add(new DiagnosticFinding
                                    {
                                        IssueType = "HighDiskTemperature",
                                        Severity = temp > 60 ? "High" : "Medium",
                                        Confidence = 90,
                                        AutoFixPossible = false,
                                        RiskLevel = "N/A",
                                        Description = $"Disque {model}: Température élevée {temp}°C (recommandé < 45°C)",
                                        SuggestedAction = "Améliorer ventilation boîtier, vérifier circulation d'air, nettoyer ventilateurs",
                                        Source = "SmartDetails"
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[DiagnosticFindingsBuilder] Erreur AddStorageFindings: {ex.Message}");
            }
        }

        #endregion

        #region Temperature Findings

        /// <summary>
        /// Détecte les problèmes de température (CPU, GPU) depuis scan_csharp.metrics
        /// </summary>
        private static void AddTemperatureFindings(JsonElement root, List<DiagnosticFinding> findings, int confidence)
        {
            try
            {
                // Note: Les températures viennent de scan_csharp.metrics dans le JSON combiné
                // ou directement de la racine selon la structure
                
                // Essayer d'accéder aux métriques C#
                JsonElement metrics = default;
                bool hasMetrics = false;
                
                if (root.TryGetProperty("scan_csharp", out var csharp) &&
                    csharp.TryGetProperty("metrics", out metrics))
                {
                    hasMetrics = true;
                }
                else if (root.TryGetProperty("metrics", out metrics))
                {
                    hasMetrics = true;
                }
                
                if (!hasMetrics) return;

                // 21. CPU chaud (> 80°C)
                if (metrics.TryGetProperty("cpu", out var cpuMetrics) &&
                    cpuMetrics.TryGetProperty("cpuTempC", out var cpuTempMetric))
                {
                    if (cpuTempMetric.TryGetProperty("available", out var availEl) &&
                        availEl.GetBoolean() &&
                        cpuTempMetric.TryGetProperty("value", out var valueEl))
                    {
                        var cpuTemp = valueEl.GetDouble();
                        if (cpuTemp > 80)
                        {
                            findings.Add(new DiagnosticFinding
                            {
                                IssueType = "HighCpuTemperature",
                                Severity = cpuTemp > 90 ? "Critical" : "High",
                                Confidence = 95,
                                AutoFixPossible = false,
                                RiskLevel = "N/A",
                                Description = $"Température CPU: {cpuTemp:F1}°C (danger > 85°C, critique > 90°C)",
                                SuggestedAction = "Nettoyer ventilateurs CPU, remplacer pâte thermique, vérifier fonctionnement refroidissement, réduire overclock si applicable",
                                Source = "Sensors"
                            });
                        }
                    }
                }

                // 22. GPU chaud (> 85°C)
                if (metrics.TryGetProperty("gpu", out var gpuMetrics) &&
                    gpuMetrics.TryGetProperty("gpuTempC", out var gpuTempMetric))
                {
                    if (gpuTempMetric.TryGetProperty("available", out var availEl2) &&
                        availEl2.GetBoolean() &&
                        gpuTempMetric.TryGetProperty("value", out var valueEl2))
                    {
                        var gpuTemp = valueEl2.GetDouble();
                        if (gpuTemp > 85)
                        {
                            findings.Add(new DiagnosticFinding
                            {
                                IssueType = "HighGpuTemperature",
                                Severity = gpuTemp > 95 ? "Critical" : "High",
                                Confidence = 95,
                                AutoFixPossible = false,
                                RiskLevel = "N/A",
                                Description = $"Température GPU: {gpuTemp:F1}°C (danger > 90°C, critique > 95°C)",
                                SuggestedAction = "Améliorer ventilation boîtier, nettoyer GPU et ventilateurs, limiter overclock, réduire limites puissance si applicable",
                                Source = "Sensors"
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[DiagnosticFindingsBuilder] Erreur AddTemperatureFindings: {ex.Message}");
            }
        }

        #endregion

        #region Driver Findings

        /// <summary>
        /// Détecte les problèmes de pilotes (Erreurs, Non signés)
        /// </summary>
        private static void AddDriverFindings(JsonElement root, List<DiagnosticFinding> findings, int confidence)
        {
            try
            {
                if (!root.TryGetProperty("sections", out var sections))
                    return;

                // 23. Pilotes en erreur
                if (TryGetInt(root, out var errorDevices, "sections", "DevicesDrivers", "data", "errorDevices"))
                {
                    if (errorDevices > 0)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "DriversInError",
                            Severity = errorDevices > 3 ? "High" : "Medium",
                            Confidence = 100,
                            AutoFixPossible = true,
                            RiskLevel = "Medium",
                            Description = $"{errorDevices} périphériques en erreur détectés",
                            SuggestedAction = "Ouvrir Gestionnaire de périphériques (devmgmt.msc), identifier périphériques avec icône jaune/rouge, clic droit > Mettre à jour le pilote ou Désinstaller puis redémarrer",
                            Source = "DevicesDrivers"
                        });
                    }
                }

                // 24. Pilotes non signés
                if (TryGetInt(root, out var totalDrivers, "sections", "DevicesDrivers", "data", "totalDrivers") &&
                    TryGetInt(root, out var signedDrivers, "sections", "DevicesDrivers", "data", "signedDrivers"))
                {
                    var unsignedDrivers = totalDrivers - signedDrivers;
                    
                    if (unsignedDrivers > 0)
                    {
                        findings.Add(new DiagnosticFinding
                        {
                            IssueType = "UnsignedDrivers",
                            Severity = "Medium",
                            Confidence = 95,
                            AutoFixPossible = false,
                            RiskLevel = "N/A",
                            Description = $"{unsignedDrivers} pilotes non signés détectés (risque sécurité)",
                            SuggestedAction = "Identifier pilotes non signés dans Gestionnaire de périphériques, remplacer par versions officielles signées du fabricant",
                            Source = "DevicesDrivers"
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[DiagnosticFindingsBuilder] Erreur AddDriverFindings: {ex.Message}");
            }
        }

        #endregion
    }
}
