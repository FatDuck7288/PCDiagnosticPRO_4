using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Service pour parser les rapports de scan générés par le script PowerShell
    /// </summary>
    public class ReportParserService
    {
        public string ReportDirectory { get; set; } = @"C:\";
        
        private static readonly string[] ReportPatterns = new[]
        {
            "scan_result.json",
            "*_Scan_*.txt",
            "*_Report_*.txt",
            "*_Diagnostic_*.txt",
            "Total_PS_PC_Scan_*.txt",
            "PC_Scan_*.txt"
        };

        /// <summary>
        /// Trouve le rapport le plus récent
        /// </summary>
        public string? FindLatestReport()
        {
            try
            {
                var reports = new List<FileInfo>();

                foreach (var pattern in ReportPatterns)
                {
                    try
                    {
                        var files = Directory.GetFiles(ReportDirectory, pattern, SearchOption.TopDirectoryOnly);
                        reports.AddRange(files.Select(f => new FileInfo(f)));
                    }
                    catch { }
                }

                // Chercher aussi tous les fichiers .txt/.json récents (moins de 24h)
                try
                {
                    var recentTxtFiles = Directory.GetFiles(ReportDirectory, "*.txt", SearchOption.TopDirectoryOnly)
                        .Select(f => new FileInfo(f))
                        .Where(f => f.LastWriteTime > DateTime.Now.AddHours(-24));
                    reports.AddRange(recentTxtFiles);
                }
                catch { }

                try
                {
                    var recentJsonFiles = Directory.GetFiles(ReportDirectory, "*.json", SearchOption.TopDirectoryOnly)
                        .Select(f => new FileInfo(f))
                        .Where(f => f.LastWriteTime > DateTime.Now.AddHours(-24));
                    reports.AddRange(recentJsonFiles);
                }
                catch { }

                // Trouver le plus récent
                var latestReport = reports
                    .Distinct(new FileInfoComparer())
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();

                if (latestReport != null)
                {
                    App.LogMessage($"Rapport trouvé: {latestReport.FullName}");
                    return latestReport.FullName;
                }

                App.LogMessage($"Aucun rapport trouvé dans {ReportDirectory}");
                return null;
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur recherche rapport: {ex.Message}");
                return null;
            }
        }

        public string? FindLatestJsonReport()
        {
            try
            {
                var reports = new List<FileInfo>();

                foreach (var pattern in new[] { "scan_result.json", "Scan_*.json", "*.json" })
                {
                    try
                    {
                        var files = Directory.GetFiles(ReportDirectory, pattern, SearchOption.TopDirectoryOnly);
                        reports.AddRange(files.Select(f => new FileInfo(f)));
                    }
                    catch { }
                }

                var latestReport = reports
                    .Distinct(new FileInfoComparer())
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();

                if (latestReport != null)
                {
                    App.LogMessage($"Rapport JSON trouvé: {latestReport.FullName}");
                    return latestReport.FullName;
                }

                App.LogMessage($"Aucun rapport JSON trouvé dans {ReportDirectory}");
                return null;
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur recherche rapport JSON: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Parse un fichier de rapport et retourne les résultats structurés
        /// </summary>
        public ScanResult ParseReport(string filePath)
        {
            var result = new ScanResult
            {
                ReportFilePath = filePath
            };

            try
            {
                if (!File.Exists(filePath))
                {
                    result.ErrorMessage = $"Le fichier rapport n'existe pas: {filePath}";
                    return result;
                }

                if (Path.GetExtension(filePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    ParseJsonReport(filePath, result);
                    return result;
                }

                result.RawReport = ReadReportText(filePath);
                result.IsValid = true;

                // Parser les différentes sections
                ParseSystemInfo(result);
                ParseMemoryInfo(result);
                ParseDiskInfo(result);
                ParseServicesInfo(result);
                ParseNetworkInfo(result);
                ParseSecurityInfo(result);
                ParseWindowsUpdateInfo(result);
                ParseApplicationsInfo(result);

                // Calculer le score et le grade
                CalculateScoreAndGrade(result);

                App.LogMessage($"Rapport parsé: {result.Items.Count} éléments, Score: {result.Summary.Score}");
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Erreur lors du parsing: {ex.Message}";
                result.IsValid = false;
                App.LogMessage($"Erreur parsing: {ex.Message}");
            }

            return result;
        }

        private static void ParseJsonReport(string filePath, ScanResult result)
        {
            var jsonContent = File.ReadAllText(filePath, Encoding.UTF8);
            result.RawReport = jsonContent;
            result.ReportFilePath = filePath;

            using var jsonDoc = JsonDocument.Parse(jsonContent);
            var root = jsonDoc.RootElement;

            result.IsValid = true;

            if (root.TryGetProperty("summary", out var summaryEl))
            {
                result.Summary.Score = summaryEl.TryGetProperty("score", out var scoreEl) ? scoreEl.GetInt32() : 0;
                result.Summary.Grade = summaryEl.TryGetProperty("grade", out var gradeEl) ? gradeEl.GetString() ?? "N/A" : "N/A";
                result.Summary.CriticalCount = summaryEl.TryGetProperty("criticalCount", out var critEl) ? critEl.GetInt32() : 0;
                result.Summary.ErrorCount = summaryEl.TryGetProperty("errorCount", out var errEl) ? errEl.GetInt32() : 0;
                result.Summary.WarningCount = summaryEl.TryGetProperty("warningCount", out var warnEl) ? warnEl.GetInt32() : 0;

                if (summaryEl.TryGetProperty("scanDate", out var dateEl))
                {
                    if (DateTimeOffset.TryParse(dateEl.GetString(), out var parsedDate))
                    {
                        result.Summary.ScanDate = parsedDate.LocalDateTime;
                    }
                }
            }

            if (root.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var itemEl in itemsEl.EnumerateArray())
                {
                    var severityStr = itemEl.TryGetProperty("severity", out var sevEl) ? sevEl.GetString() ?? "Info" : "Info";
                    var severity = severityStr switch
                    {
                        "Critical" => ScanSeverity.Critical,
                        "Major" => ScanSeverity.Error,
                        "Minor" => ScanSeverity.Warning,
                        _ => ScanSeverity.Info
                    };

                    var status = severity switch
                    {
                        ScanSeverity.Critical => "CRITIQUE",
                        ScanSeverity.Error => "ERREUR",
                        ScanSeverity.Warning => "AVERTISSEMENT",
                        _ => "INFO"
                    };

                    result.Items.Add(new ScanItem
                    {
                        Category = itemEl.TryGetProperty("category", out var catEl) ? catEl.GetString() ?? "" : "",
                        Name = itemEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                        Severity = severity,
                        Status = status,
                        Detail = itemEl.TryGetProperty("detail", out var detEl) ? detEl.GetString() ?? "" : "",
                        Recommendation = itemEl.TryGetProperty("recommendation", out var recEl) ? recEl.GetString() ?? "" : ""
                    });
                }
            }

            result.Summary.TotalItems = result.Items.Count;
            result.Summary.OkCount = result.Items.Count(item => item.Severity == ScanSeverity.Info);
        }

        private static string ReadReportText(string filePath)
        {
            var bytes = File.ReadAllBytes(filePath);
            var utf8Text = Encoding.UTF8.GetString(bytes);

            if (!utf8Text.Contains('\uFFFD'))
            {
                return utf8Text;
            }

            var windows1252 = Encoding.GetEncoding(1252);
            return windows1252.GetString(bytes);
        }

        private void ParseSystemInfo(ScanResult result)
        {
            try
            {
                // Nom du système
                var systemNameMatch = Regex.Match(result.RawReport, @"Computer\s*Name[:\s]+([^\r\n]+)", RegexOptions.IgnoreCase);
                if (systemNameMatch.Success)
                {
                    result.Summary.SystemName = systemNameMatch.Groups[1].Value.Trim();
                }

                // Version OS
                var osMatch = Regex.Match(result.RawReport, @"OS\s*(Version|Name)?[:\s]+([^\r\n]+)", RegexOptions.IgnoreCase);
                if (osMatch.Success)
                {
                    result.Summary.OsVersion = osMatch.Groups[2].Value.Trim();
                }

                // CPU
                var cpuMatch = Regex.Match(result.RawReport, @"(CPU|Processor)[:\s]+([^\r\n]+)", RegexOptions.IgnoreCase);
                if (cpuMatch.Success)
                {
                    result.Items.Add(new ScanItem
                    {
                        Category = "Système",
                        Name = "Processeur",
                        Status = "Détecté",
                        Detail = cpuMatch.Groups[2].Value.Trim(),
                        Severity = ScanSeverity.Info
                    });
                }

                // Uptime
                var uptimeMatch = Regex.Match(result.RawReport, @"(Uptime|Up\s*Time)[:\s]+([^\r\n]+)", RegexOptions.IgnoreCase);
                if (uptimeMatch.Success)
                {
                    var uptimeValue = uptimeMatch.Groups[2].Value.Trim();
                    var severity = ScanSeverity.OK;
                    var recommendation = string.Empty;

                    if (Regex.IsMatch(uptimeValue, @"(\d+)\s*day", RegexOptions.IgnoreCase))
                    {
                        var daysMatch = Regex.Match(uptimeValue, @"(\d+)\s*day");
                        if (int.TryParse(daysMatch.Groups[1].Value, out int days) && days > 30)
                        {
                            severity = ScanSeverity.Warning;
                            recommendation = "Redémarrage recommandé (uptime > 30 jours)";
                            result.Summary.RebootRequired = true;
                        }
                    }

                    result.Items.Add(new ScanItem
                    {
                        Category = "Système",
                        Name = "Temps de fonctionnement",
                        Status = severity == ScanSeverity.OK ? "OK" : "Attention",
                        Detail = uptimeValue,
                        Severity = severity,
                        Recommendation = recommendation
                    });
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur parsing système: {ex.Message}");
            }
        }

        private void ParseMemoryInfo(ScanResult result)
        {
            try
            {
                var memMatch = Regex.Match(result.RawReport, @"(Memory|RAM|Mémoire)\s*(Usage|Used|Utilisée)?[:\s]*(\d+(?:[.,]\d+)?)\s*%", RegexOptions.IgnoreCase);
                if (memMatch.Success)
                {
                    if (double.TryParse(memMatch.Groups[3].Value.Replace(',', '.'), 
                        NumberStyles.Float, CultureInfo.InvariantCulture, out double memPercent))
                    {
                        result.Summary.RamUsagePercent = memPercent;

                        var severity = memPercent switch
                        {
                            < 70 => ScanSeverity.OK,
                            < 85 => ScanSeverity.Warning,
                            _ => ScanSeverity.Error
                        };

                        result.Items.Add(new ScanItem
                        {
                            Category = "Mémoire",
                            Name = "Utilisation RAM",
                            Status = severity == ScanSeverity.OK ? "OK" : (severity == ScanSeverity.Warning ? "Élevée" : "Critique"),
                            Detail = $"{memPercent:F1}%",
                            Severity = severity,
                            Recommendation = severity != ScanSeverity.OK ? "Fermer les applications non utilisées" : string.Empty
                        });
                    }
                }

                var totalMemMatch = Regex.Match(result.RawReport, @"Total\s*(Memory|RAM)[:\s]*(\d+(?:[.,]\d+)?)\s*(GB|MB|Go|Mo)", RegexOptions.IgnoreCase);
                if (totalMemMatch.Success)
                {
                    result.Items.Add(new ScanItem
                    {
                        Category = "Mémoire",
                        Name = "RAM Totale",
                        Status = "Info",
                        Detail = $"{totalMemMatch.Groups[2].Value} {totalMemMatch.Groups[3].Value}",
                        Severity = ScanSeverity.Info
                    });
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur parsing mémoire: {ex.Message}");
            }
        }

        private void ParseDiskInfo(ScanResult result)
        {
            try
            {
                var diskPattern = @"([A-Z]:)[^\r\n]*?(\d+(?:[.,]\d+)?)\s*%\s*(free|libre|used|utilisé)?";
                var diskMatches = Regex.Matches(result.RawReport, diskPattern, RegexOptions.IgnoreCase);

                foreach (Match match in diskMatches)
                {
                    var drive = match.Groups[1].Value;
                    if (double.TryParse(match.Groups[2].Value.Replace(',', '.'),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
                    {
                        var isUsed = match.Groups[3].Value.ToLower().Contains("used") ||
                                    match.Groups[3].Value.ToLower().Contains("utilisé");
                        
                        var freePercent = isUsed ? (100 - percent) : percent;

                        var severity = freePercent switch
                        {
                            > 20 => ScanSeverity.OK,
                            > 10 => ScanSeverity.Warning,
                            _ => ScanSeverity.Error
                        };

                        result.Items.Add(new ScanItem
                        {
                            Category = "Stockage",
                            Name = $"Disque {drive}",
                            Status = severity == ScanSeverity.OK ? "OK" : (severity == ScanSeverity.Warning ? "Attention" : "Critique"),
                            Detail = $"{freePercent:F1}% libre",
                            Severity = severity,
                            Recommendation = severity != ScanSeverity.OK ? "Libérer de l'espace disque" : string.Empty
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur parsing disque: {ex.Message}");
            }
        }

        private void ParseServicesInfo(ScanResult result)
        {
            try
            {
                var stoppedServicePattern = @"(Service|Stopped)[:\s]*([^\r\n]+?)\s*\[(Stopped|Arrêté|Not Running)\]";
                var stoppedMatches = Regex.Matches(result.RawReport, stoppedServicePattern, RegexOptions.IgnoreCase);

                foreach (Match match in stoppedMatches)
                {
                    var serviceName = match.Groups[2].Value.Trim();
                    var severity = IsImportantService(serviceName) ? ScanSeverity.Warning : ScanSeverity.Info;

                    result.Items.Add(new ScanItem
                    {
                        Category = "Services",
                        Name = serviceName,
                        Status = "Arrêté",
                        Detail = "Service non actif",
                        Severity = severity,
                        Recommendation = severity == ScanSeverity.Warning ? "Vérifier si ce service est nécessaire" : string.Empty
                    });
                }

                var failedPattern = @"(Failed|Échec|Error)[:\s]*Service[:\s]*([^\r\n]+)";
                var failedMatches = Regex.Matches(result.RawReport, failedPattern, RegexOptions.IgnoreCase);

                foreach (Match match in failedMatches)
                {
                    result.Items.Add(new ScanItem
                    {
                        Category = "Services",
                        Name = match.Groups[2].Value.Trim(),
                        Status = "Échec",
                        Detail = "Service en erreur",
                        Severity = ScanSeverity.Error,
                        Recommendation = "Redémarrer le service ou vérifier les logs"
                    });
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur parsing services: {ex.Message}");
            }
        }

        private void ParseNetworkInfo(ScanResult result)
        {
            try
            {
                var internetPattern = @"Internet\s*(Connectivity|Connection)?[:\s]*(Yes|No|OK|Connected|Disconnected|Connecté|Déconnecté)";
                var internetMatch = Regex.Match(result.RawReport, internetPattern, RegexOptions.IgnoreCase);

                if (internetMatch.Success)
                {
                    var isConnected = Regex.IsMatch(internetMatch.Groups[2].Value, @"(Yes|OK|Connected|Connecté)", RegexOptions.IgnoreCase);
                    
                    result.Items.Add(new ScanItem
                    {
                        Category = "Réseau",
                        Name = "Connectivité Internet",
                        Status = isConnected ? "Connecté" : "Déconnecté",
                        Detail = isConnected ? "Connexion active" : "Pas de connexion",
                        Severity = isConnected ? ScanSeverity.OK : ScanSeverity.Error,
                        Recommendation = isConnected ? string.Empty : "Vérifier la connexion réseau"
                    });
                }

                var ipPattern = @"IP\s*Address[:\s]*(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})";
                var ipMatch = Regex.Match(result.RawReport, ipPattern, RegexOptions.IgnoreCase);

                if (ipMatch.Success)
                {
                    result.Items.Add(new ScanItem
                    {
                        Category = "Réseau",
                        Name = "Adresse IP",
                        Status = "Info",
                        Detail = ipMatch.Groups[1].Value,
                        Severity = ScanSeverity.Info
                    });
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur parsing réseau: {ex.Message}");
            }
        }

        private void ParseSecurityInfo(ScanResult result)
        {
            try
            {
                var avPattern = @"(Antivirus|Windows\s*Defender|Security)[:\s]*(Enabled|Disabled|Active|Inactive|OK|À jour|Outdated)";
                var avMatch = Regex.Match(result.RawReport, avPattern, RegexOptions.IgnoreCase);

                if (avMatch.Success)
                {
                    var isActive = Regex.IsMatch(avMatch.Groups[2].Value, @"(Enabled|Active|OK|À jour)", RegexOptions.IgnoreCase);
                    
                    result.Items.Add(new ScanItem
                    {
                        Category = "Sécurité",
                        Name = "Protection antivirus",
                        Status = isActive ? "Actif" : "Inactif",
                        Detail = avMatch.Groups[2].Value,
                        Severity = isActive ? ScanSeverity.OK : ScanSeverity.Critical,
                        Recommendation = isActive ? string.Empty : "Activer la protection antivirus"
                    });
                }

                var fwPattern = @"(Firewall|Pare-feu)[:\s]*(Enabled|Disabled|Active|Inactive|On|Off)";
                var fwMatch = Regex.Match(result.RawReport, fwPattern, RegexOptions.IgnoreCase);

                if (fwMatch.Success)
                {
                    var isActive = Regex.IsMatch(fwMatch.Groups[2].Value, @"(Enabled|Active|On)", RegexOptions.IgnoreCase);
                    
                    result.Items.Add(new ScanItem
                    {
                        Category = "Sécurité",
                        Name = "Pare-feu",
                        Status = isActive ? "Actif" : "Inactif",
                        Detail = isActive ? "Protection active" : "Protection désactivée",
                        Severity = isActive ? ScanSeverity.OK : ScanSeverity.Critical,
                        Recommendation = isActive ? string.Empty : "Activer le pare-feu Windows"
                    });
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur parsing sécurité: {ex.Message}");
            }
        }

        private void ParseWindowsUpdateInfo(ScanResult result)
        {
            try
            {
                var wuPattern = @"Windows\s*Update[:\s]*(Up\s*to\s*date|À jour|Pending|En attente|Updates available|Mises à jour disponibles|(\d+)\s*update)";
                var wuMatch = Regex.Match(result.RawReport, wuPattern, RegexOptions.IgnoreCase);

                if (wuMatch.Success)
                {
                    var statusText = wuMatch.Groups[1].Value;
                    var isUpToDate = Regex.IsMatch(statusText, @"(Up\s*to\s*date|À jour)", RegexOptions.IgnoreCase);
                    
                    result.Summary.WindowsUpdateStatus = isUpToDate ? "À jour" : "Mises à jour disponibles";

                    result.Items.Add(new ScanItem
                    {
                        Category = "Mises à jour",
                        Name = "Windows Update",
                        Status = isUpToDate ? "À jour" : "Mises à jour en attente",
                        Detail = statusText,
                        Severity = isUpToDate ? ScanSeverity.OK : ScanSeverity.Warning,
                        Recommendation = isUpToDate ? string.Empty : "Installer les mises à jour Windows"
                    });

                    if (!isUpToDate)
                    {
                        result.Summary.RebootRequired = true;
                    }
                }

                // Windows Update cassé = Critique
                var wuBrokenPattern = @"Windows\s*Update[:\s]*(Broken|Cassé|Error|Failed|Échec)";
                var wuBrokenMatch = Regex.Match(result.RawReport, wuBrokenPattern, RegexOptions.IgnoreCase);
                if (wuBrokenMatch.Success)
                {
                    result.Items.Add(new ScanItem
                    {
                        Category = "Mises à jour",
                        Name = "Windows Update",
                        Status = "CASSÉ",
                        Detail = "Service Windows Update défaillant",
                        Severity = ScanSeverity.Critical,
                        Recommendation = "Réparer Windows Update immédiatement"
                    });
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur parsing Windows Update: {ex.Message}");
            }
        }

        private void ParseApplicationsInfo(ScanResult result)
        {
            try
            {
                var startupPattern = @"Startup\s*(Programs|Apps|Applications)[:\s]*(\d+)";
                var startupMatch = Regex.Match(result.RawReport, startupPattern, RegexOptions.IgnoreCase);

                if (startupMatch.Success)
                {
                    if (int.TryParse(startupMatch.Groups[2].Value, out int count))
                    {
                        var severity = count switch
                        {
                            < 10 => ScanSeverity.OK,
                            < 20 => ScanSeverity.Info,
                            < 30 => ScanSeverity.Warning,
                            _ => ScanSeverity.Error
                        };

                        result.Items.Add(new ScanItem
                        {
                            Category = "Applications",
                            Name = "Programmes au démarrage",
                            Status = count < 20 ? "OK" : "Élevé",
                            Detail = $"{count} programmes",
                            Severity = severity,
                            Recommendation = count >= 20 ? "Désactiver les programmes inutiles au démarrage" : string.Empty
                        });
                    }
                }

                var tempPattern = @"Temp\s*(Files|Folder|Size)[:\s]*(\d+(?:[.,]\d+)?)\s*(GB|MB|Ko|Mo)";
                var tempMatch = Regex.Match(result.RawReport, tempPattern, RegexOptions.IgnoreCase);

                if (tempMatch.Success)
                {
                    result.Items.Add(new ScanItem
                    {
                        Category = "Applications",
                        Name = "Fichiers temporaires",
                        Status = "Info",
                        Detail = $"{tempMatch.Groups[2].Value} {tempMatch.Groups[3].Value}",
                        Severity = ScanSeverity.Info,
                        Recommendation = "Nettoyage des fichiers temporaires recommandé"
                    });
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur parsing applications: {ex.Message}");
            }
        }

        private void CalculateScoreAndGrade(ScanResult result)
        {
            if (!result.Items.Any())
            {
                result.Summary.Score = 0;
                result.Summary.Grade = "N/A";
                return;
            }

            // Compter par sévérité
            result.Summary.OkCount = result.Items.Count(i => i.Severity == ScanSeverity.OK);
            result.Summary.WarningCount = result.Items.Count(i => i.Severity == ScanSeverity.Warning);
            result.Summary.ErrorCount = result.Items.Count(i => i.Severity == ScanSeverity.Error);
            result.Summary.CriticalCount = result.Items.Count(i => i.Severity == ScanSeverity.Critical);
            result.Summary.TotalItems = result.Items.Count;

            // Calculer le score selon la légende:
            // Critique = -25, Majeur (Error) = -10, Mineur (Warning) = -5
            double score = 100.0;
            score -= result.Summary.CriticalCount * 25; // Critique: -25
            score -= result.Summary.ErrorCount * 10;    // Majeur: -10
            score -= result.Summary.WarningCount * 5;   // Mineur: -5

            result.Summary.Score = Math.Max(0, Math.Min(100, (int)Math.Round(score)));

            // Grade selon le barème:
            // 90-100 = A, 75-89 = B, 60-74 = C, <60 = D
            result.Summary.Grade = result.Summary.Score switch
            {
                >= 90 => "A",
                >= 75 => "B",
                >= 60 => "C",
                _ => "D"
            };

            result.Summary.ScanDate = DateTime.Now;
        }

        private bool IsImportantService(string serviceName)
        {
            var importantServices = new[]
            {
                "Windows Update", "wuauserv",
                "Windows Defender", "WinDefend",
                "Security Center", "wscsvc",
                "Firewall", "MpsSvc",
                "BITS", "Background Intelligent",
                "Cryptographic", "CryptSvc"
            };

            return importantServices.Any(s => 
                serviceName.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        private class FileInfoComparer : IEqualityComparer<FileInfo>
        {
            public bool Equals(FileInfo? x, FileInfo? y) => x?.FullName == y?.FullName;
            public int GetHashCode(FileInfo obj) => obj.FullName.GetHashCode();
        }
    }
}
