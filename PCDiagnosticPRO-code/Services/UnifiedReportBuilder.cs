using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Génère le rapport TXT UNIFIÉ final = PowerShell + Hardware Sensors + Metadata.
    /// Ce TXT devient la source humaine canonique complète de la machine.
    /// </summary>
    public static class UnifiedReportBuilder
    {
        private const string SEPARATOR = "════════════════════════════════════════════════════════════════════════════════";
        private const string SUBSEPARATOR = "────────────────────────────────────────────────────────────────────────────────";

        /// <summary>
        /// Génère le rapport TXT unifié depuis le JSON combiné.
        /// </summary>
        /// <param name="combinedJsonPath">Chemin vers scan_result_combined.json</param>
        /// <param name="originalTxtPath">Chemin vers le TXT PowerShell original (optionnel pour fallback)</param>
        /// <param name="outputPath">Chemin de sortie du TXT unifié</param>
        /// <param name="healthReport">HealthReport avec scores GradeEngine</param>
        public static async Task<bool> BuildUnifiedReportAsync(
            string combinedJsonPath,
            string? originalTxtPath,
            string outputPath,
            HealthReport? healthReport = null)
        {
            try
            {
                var sb = new StringBuilder();
                HardwareSensorsResult? sensors = null;
                JsonElement? psData = null;

                // 1. Lire le JSON combiné
                if (File.Exists(combinedJsonPath))
                {
                    var jsonContent = await File.ReadAllTextAsync(combinedJsonPath, Encoding.UTF8);
                    using var doc = JsonDocument.Parse(jsonContent);
                    
                    if (doc.RootElement.TryGetProperty("sensorsCsharp", out var sensorsElement))
                    {
                        sensors = JsonSerializer.Deserialize<HardwareSensorsResult>(sensorsElement.GetRawText());
                    }
                    
                    if (doc.RootElement.TryGetProperty("scanPowershell", out var psElement))
                    {
                        psData = psElement.Clone();
                    }
                }

                // 2. Générer l'en-tête unifié
                BuildHeader(sb, healthReport, sensors);

                // 3. Section METADATA & COVERAGE
                BuildMetadataSection(sb, healthReport, sensors);

                // 4. Section HARDWARE SENSORS (données C# live)
                BuildHardwareSensorsSection(sb, sensors);

                // 5. Ajouter le contenu PowerShell original
                await BuildPowerShellSection(sb, originalTxtPath, psData);

                // 6. Section SCORE & GRADE ENGINE
                BuildScoreSection(sb, healthReport);

                // 7. Footer avec signature
                BuildFooter(sb, sensors);

                // 8. Écrire le fichier
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
                App.LogMessage($"[UnifiedReport] TXT unifié généré: {outputPath}");
                
                return true;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[UnifiedReport] ERREUR: {ex.Message}");
                return false;
            }
        }

        private static void BuildHeader(StringBuilder sb, HealthReport? healthReport, HardwareSensorsResult? sensors)
        {
            sb.AppendLine(SEPARATOR);
            sb.AppendLine("                    PC DIAGNOSTIC PRO — RAPPORT UNIFIÉ");
            sb.AppendLine(SEPARATOR);
            sb.AppendLine();
            sb.AppendLine($"  Date de génération : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"  Machine            : {Environment.MachineName}");
            sb.AppendLine($"  Utilisateur        : {Environment.UserName}");
            sb.AppendLine($"  OS                 : {Environment.OSVersion}");
            sb.AppendLine($"  Mode Admin         : {(AdminHelper.IsRunningAsAdmin() ? "OUI" : "NON")}");
            sb.AppendLine();
            
            if (healthReport != null)
            {
                var emoji = healthReport.GlobalScore >= 90 ? "✅" :
                            healthReport.GlobalScore >= 70 ? "⚠️" :
                            healthReport.GlobalScore >= 50 ? "🔶" : "❌";
                            
                sb.AppendLine($"  {emoji} SCORE GLOBAL : {healthReport.GlobalScore}/100 (Grade {healthReport.Grade})");
                sb.AppendLine($"     Verdict : {healthReport.GlobalMessage}");
            }
            
            sb.AppendLine();
            sb.AppendLine(SEPARATOR);
        }

        private static void BuildMetadataSection(StringBuilder sb, HealthReport? healthReport, HardwareSensorsResult? sensors)
        {
            sb.AppendLine();
            sb.AppendLine("  [METADATA & DATA COVERAGE]");
            sb.AppendLine(SUBSEPARATOR);
            sb.AppendLine();

            // Sources de données
            sb.AppendLine("  SOURCES DE DONNÉES:");
            sb.AppendLine("    ├─ PowerShell Script    : Total_PS_PC_Scan_v7.0.ps1 (IMMUTABLE)");
            sb.AppendLine("    └─ Hardware Collector   : LibreHardwareMonitor (C#)");
            sb.AppendLine();

            // Coverage capteurs
            if (sensors != null)
            {
                var (available, total) = sensors.GetAvailabilitySummary();
                var pct = total > 0 ? (available * 100 / total) : 0;
                sb.AppendLine($"  SENSORS COVERAGE: {available}/{total} ({pct}%)");
                sb.AppendLine($"    ├─ CPU Temperature  : {(sensors.Cpu.CpuTempC.Available ? "✓" : "✗")}");
                sb.AppendLine($"    ├─ GPU Temperature  : {(sensors.Gpu.GpuTempC.Available ? "✓" : "✗")}");
                sb.AppendLine($"    ├─ GPU Load         : {(sensors.Gpu.GpuLoadPercent.Available ? "✓" : "✗")}");
                sb.AppendLine($"    ├─ VRAM Usage       : {(sensors.Gpu.VramUsedMB.Available ? "✓" : "✗")}");
                sb.AppendLine($"    └─ Disk Temps       : {sensors.Disks.Count(d => d.TempC.Available)}/{sensors.Disks.Count}");
            }
            else
            {
                sb.AppendLine("  SENSORS COVERAGE: N/A (données capteurs non disponibles)");
            }
            sb.AppendLine();

            // Confidence model
            if (healthReport?.ConfidenceModel != null)
            {
                var cm = healthReport.ConfidenceModel;
                sb.AppendLine($"  CONFIDENCE MODEL:");
                sb.AppendLine($"    ├─ Score Confiance  : {cm.ConfidenceScore}/100 ({cm.ConfidenceLevel})");
                sb.AppendLine($"    ├─ Sections PS      : {cm.SectionsCoverage:P0}");
                sb.AppendLine($"    └─ Capteurs HW      : {cm.SensorsCoverage:P0}");
                
                if (cm.Warnings.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("  AVERTISSEMENTS:");
                    foreach (var w in cm.Warnings)
                    {
                        sb.AppendLine($"    ⚠️ {w}");
                    }
                }
            }

            // Admin impact
            if (!AdminHelper.IsRunningAsAdmin())
            {
                sb.AppendLine();
                sb.AppendLine("  ⚠️ IMPACT MODE NON-ADMIN:");
                sb.AppendLine("    - Certains capteurs peuvent être indisponibles");
                sb.AppendLine("    - Données de performance limitées");
                sb.AppendLine("    - Journaux système partiellement accessibles");
            }

            sb.AppendLine();
        }

        private static void BuildHardwareSensorsSection(StringBuilder sb, HardwareSensorsResult? sensors)
        {
            sb.AppendLine(SEPARATOR);
            sb.AppendLine("  [HARDWARE SENSORS — DONNÉES TEMPS RÉEL C#]");
            sb.AppendLine(SEPARATOR);
            sb.AppendLine();

            if (sensors == null)
            {
                sb.AppendLine("  ❌ Données capteurs non disponibles");
                sb.AppendLine();
                return;
            }

            sb.AppendLine($"  Collecté à : {sensors.CollectedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            // CPU
            sb.AppendLine("  ┌─ CPU ─────────────────────────────────────────────────────────────────────┐");
            WriteMetric(sb, "Temperature", sensors.Cpu.CpuTempC, "°C", "HardwareSensorsCollector");
            sb.AppendLine("  └─────────────────────────────────────────────────────────────────────────────┘");
            sb.AppendLine();

            // GPU
            sb.AppendLine("  ┌─ GPU ─────────────────────────────────────────────────────────────────────┐");
            WriteMetricString(sb, "Nom", sensors.Gpu.Name, "HardwareSensorsCollector");
            WriteMetric(sb, "Temperature", sensors.Gpu.GpuTempC, "°C", "HardwareSensorsCollector");
            WriteMetric(sb, "Charge GPU", sensors.Gpu.GpuLoadPercent, "%", "HardwareSensorsCollector");
            WriteMetric(sb, "VRAM Total", sensors.Gpu.VramTotalMB, "MB", "HardwareSensorsCollector");
            WriteMetric(sb, "VRAM Utilisée", sensors.Gpu.VramUsedMB, "MB", "HardwareSensorsCollector");
            
            if (sensors.Gpu.VramTotalMB.Available && sensors.Gpu.VramUsedMB.Available && sensors.Gpu.VramTotalMB.Value > 0)
            {
                var vramPct = (sensors.Gpu.VramUsedMB.Value / sensors.Gpu.VramTotalMB.Value) * 100;
                sb.AppendLine($"  │  VRAM Usage %       : {vramPct:F1}%");
                sb.AppendLine($"  │    Source           : Derived (VramUsed/VramTotal)");
                sb.AppendLine($"  │    Confidence       : High");
            }
            sb.AppendLine("  └─────────────────────────────────────────────────────────────────────────────┘");
            sb.AppendLine();

            // Disques
            sb.AppendLine("  ┌─ STORAGE TEMPERATURES ────────────────────────────────────────────────────┐");
            if (sensors.Disks.Count == 0)
            {
                sb.AppendLine("  │  Aucun disque détecté");
            }
            else
            {
                foreach (var disk in sensors.Disks)
                {
                    var name = disk.Name.Available ? disk.Name.Value : "Disque inconnu";
                    WriteMetric(sb, $"  {name}", disk.TempC, "°C", "HardwareSensorsCollector");
                }
                
                var maxTemp = sensors.Disks.Where(d => d.TempC.Available).Select(d => d.TempC.Value).DefaultIfEmpty(0).Max();
                if (maxTemp > 0)
                {
                    sb.AppendLine($"  │  ──────────────────────────────────────────────────────────────────────");
                    sb.AppendLine($"  │  TEMP MAX DISQUES   : {maxTemp:F0}°C");
                    sb.AppendLine($"  │    Source           : Derived (Max of all disks)");
                    sb.AppendLine($"  │    Confidence       : High");
                    
                    if (maxTemp > 60)
                        sb.AppendLine($"  │    ⚠️ ATTENTION    : Température élevée (>60°C)");
                    else if (maxTemp > 50)
                        sb.AppendLine($"  │    ℹ️ INFO         : Température à surveiller (>50°C)");
                }
            }
            sb.AppendLine("  └─────────────────────────────────────────────────────────────────────────────┘");
            sb.AppendLine();
        }

        private static void WriteMetric(StringBuilder sb, string label, MetricValue<double> metric, string unit, string source)
        {
            var padLabel = label.PadRight(18);
            if (metric.Available)
            {
                sb.AppendLine($"  │  {padLabel} : {metric.Value:F1}{unit}");
                sb.AppendLine($"  │    Source           : {source}");
                sb.AppendLine($"  │    Confidence       : High");
            }
            else
            {
                sb.AppendLine($"  │  {padLabel} : N/A");
                sb.AppendLine($"  │    Source           : {source}");
                sb.AppendLine($"  │    Reason           : {metric.Reason ?? "Indisponible"}");
                sb.AppendLine($"  │    Confidence       : Low");
            }
        }

        private static void WriteMetricString(StringBuilder sb, string label, MetricValue<string> metric, string source)
        {
            var padLabel = label.PadRight(18);
            if (metric.Available)
            {
                sb.AppendLine($"  │  {padLabel} : {metric.Value}");
                sb.AppendLine($"  │    Source           : {source}");
                sb.AppendLine($"  │    Confidence       : High");
            }
            else
            {
                sb.AppendLine($"  │  {padLabel} : N/A");
                sb.AppendLine($"  │    Source           : {source}");
                sb.AppendLine($"  │    Reason           : {metric.Reason ?? "Indisponible"}");
            }
        }

        private static async Task BuildPowerShellSection(StringBuilder sb, string? originalTxtPath, JsonElement? psData)
        {
            sb.AppendLine(SEPARATOR);
            sb.AppendLine("  [POWERSHELL SCAN — DONNÉES SYSTÈME]");
            sb.AppendLine(SEPARATOR);
            sb.AppendLine();

            // Inclure le contenu du TXT PowerShell original
            if (!string.IsNullOrEmpty(originalTxtPath) && File.Exists(originalTxtPath))
            {
                sb.AppendLine($"  Source: {Path.GetFileName(originalTxtPath)}");
                sb.AppendLine(SUBSEPARATOR);
                sb.AppendLine();
                
                var psContent = await File.ReadAllTextAsync(originalTxtPath, Encoding.UTF8);
                
                // Nettoyer et indenter le contenu PS
                var lines = psContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                foreach (var line in lines)
                {
                    // Ne pas re-ajouter les headers si déjà présents
                    if (line.Contains("PC DIAGNOSTIC PRO") && line.Contains("RAPPORT"))
                        continue;
                    if (line.All(c => c == '═' || c == '─'))
                        continue;
                        
                    sb.AppendLine(line);
                }
            }
            else if (psData.HasValue)
            {
                sb.AppendLine("  (Données extraites du JSON PowerShell)");
                sb.AppendLine();
                
                // Extraire les sections clés du JSON PS
                ExtractPsJsonSections(sb, psData.Value);
            }
            else
            {
                sb.AppendLine("  ❌ Données PowerShell non disponibles");
            }

            sb.AppendLine();
        }

        private static void ExtractPsJsonSections(StringBuilder sb, JsonElement psData)
        {
            // Extraire metadata
            if (psData.TryGetProperty("metadata", out var metadata))
            {
                sb.AppendLine("  [Metadata]");
                if (metadata.TryGetProperty("hostName", out var host))
                    sb.AppendLine($"    Hostname: {host.GetString()}");
                if (metadata.TryGetProperty("scanDate", out var date))
                    sb.AppendLine($"    Scan Date: {date.GetString()}");
                if (metadata.TryGetProperty("isAdmin", out var admin))
                    sb.AppendLine($"    Admin: {admin.GetBoolean()}");
                sb.AppendLine();
            }

            // Extraire scoreV2
            if (psData.TryGetProperty("scoreV2", out var score))
            {
                sb.AppendLine("  [Score PowerShell]");
                if (score.TryGetProperty("score", out var s))
                    sb.AppendLine($"    Score: {s.GetInt32()}/100");
                if (score.TryGetProperty("grade", out var g))
                    sb.AppendLine($"    Grade: {g.GetString()}");
                sb.AppendLine();
            }

            // Lister les sections disponibles
            sb.AppendLine("  [Sections disponibles]");
            foreach (var prop in psData.EnumerateObject())
            {
                if (prop.Name != "metadata" && prop.Name != "scoreV2" && prop.Name != "errors")
                {
                    var status = "OK";
                    if (prop.Value.TryGetProperty("status", out var st))
                        status = st.GetString() ?? "OK";
                    sb.AppendLine($"    - {prop.Name}: {status}");
                }
            }
        }

        private static void BuildScoreSection(StringBuilder sb, HealthReport? healthReport)
        {
            if (healthReport == null) return;

            sb.AppendLine(SEPARATOR);
            sb.AppendLine("  [SCORE ENGINE — ANALYSE GRADEENGINE]");
            sb.AppendLine(SEPARATOR);
            sb.AppendLine();

            sb.AppendLine($"  SCORE GLOBAL: {healthReport.GlobalScore}/100");
            sb.AppendLine($"  GRADE: {healthReport.Grade}");
            sb.AppendLine($"  SÉVÉRITÉ: {healthReport.GlobalSeverity}");
            sb.AppendLine();

            // Divergence PS vs GradeEngine
            if (healthReport.Divergence != null && healthReport.Divergence.Delta > 0)
            {
                sb.AppendLine("  DIVERGENCE SCORE:");
                sb.AppendLine($"    PowerShell Score  : {healthReport.Divergence.PowerShellScore}");
                sb.AppendLine($"    GradeEngine Score : {healthReport.Divergence.GradeEngineScore}");
                sb.AppendLine($"    Delta             : {healthReport.Divergence.Delta}");
                sb.AppendLine($"    Source de vérité  : {healthReport.Divergence.SourceOfTruth}");
                sb.AppendLine();
            }

            // Scores par domaine
            sb.AppendLine("  SCORES PAR DOMAINE:");
            sb.AppendLine("  ┌────────────────────────┬───────┬────────────────────────────────────┐");
            sb.AppendLine("  │ Domaine                │ Score │ Status                             │");
            sb.AppendLine("  ├────────────────────────┼───────┼────────────────────────────────────┤");
            
            foreach (var section in healthReport.Sections)
            {
                var icon = section.Score >= 90 ? "✅" : section.Score >= 70 ? "⚠️" : section.Score >= 50 ? "🔶" : "❌";
                var name = $"{section.Icon} {section.DisplayName}".PadRight(22);
                var score = $"{section.Score}/100".PadRight(5);
                var status = section.StatusMessage.Length > 34 ? section.StatusMessage[..31] + "..." : section.StatusMessage;
                sb.AppendLine($"  │ {name} │ {score} │ {status.PadRight(34)} │");
            }
            
            sb.AppendLine("  └────────────────────────┴───────┴────────────────────────────────────┘");
            sb.AppendLine();

            // Recommandations
            if (healthReport.Recommendations.Count > 0)
            {
                sb.AppendLine("  RECOMMANDATIONS:");
                foreach (var rec in healthReport.Recommendations.Take(5))
                {
                    var priority = rec.Priority switch
                    {
                        HealthSeverity.Critical => "🔴",
                        HealthSeverity.Degraded => "🟠",
                        HealthSeverity.Warning => "🟡",
                        _ => "🟢"
                    };
                    var domain = rec.RelatedDomain?.ToString() ?? "Général";
                    sb.AppendLine($"    {priority} [{domain}] {rec.Title}");
                    sb.AppendLine($"       {rec.Description}");
                }
                sb.AppendLine();
            }
        }

        private static void BuildFooter(StringBuilder sb, HardwareSensorsResult? sensors)
        {
            sb.AppendLine(SEPARATOR);
            sb.AppendLine();
            sb.AppendLine("  RAPPORT UNIFIÉ GÉNÉRÉ PAR PC DIAGNOSTIC PRO");
            sb.AppendLine();
            sb.AppendLine("  Ce rapport combine:");
            sb.AppendLine("    ✓ Données système PowerShell (structure, config, events)");
            sb.AppendLine("    ✓ Données capteurs hardware C# (températures, charges, VRAM)");
            sb.AppendLine("    ✓ Analyse GradeEngine (scoring, recommandations)");
            sb.AppendLine();
            
            if (sensors != null)
            {
                var (avail, total) = sensors.GetAvailabilitySummary();
                sb.AppendLine($"  DATA COVERAGE: {avail}/{total} capteurs disponibles ({avail * 100 / Math.Max(1, total)}%)");
            }
            
            sb.AppendLine();
            sb.AppendLine("  ═══════════════════════════════════════════════════════════════════════════════");
            sb.AppendLine($"  Généré le {DateTime.Now:yyyy-MM-dd} à {DateTime.Now:HH:mm:ss}");
            sb.AppendLine("  PC Diagnostic PRO — Rapport Unifié v1.0");
            sb.AppendLine("  ═══════════════════════════════════════════════════════════════════════════════");
        }

        /// <summary>
        /// Trouve le fichier TXT PowerShell le plus récent dans le dossier de rapports.
        /// </summary>
        public static string? FindLatestPsTxtReport(string reportsDir)
        {
            if (string.IsNullOrEmpty(reportsDir) || !Directory.Exists(reportsDir))
                return null;

            var patterns = new[] { "Scan_*.txt", "Rapport*.txt", "*_report.txt" };
            
            foreach (var pattern in patterns)
            {
                var files = Directory.GetFiles(reportsDir, pattern, SearchOption.TopDirectoryOnly);
                if (files.Length > 0)
                {
                    return files.OrderByDescending(f => File.GetLastWriteTime(f)).First();
                }
            }

            return null;
        }
    }
}
