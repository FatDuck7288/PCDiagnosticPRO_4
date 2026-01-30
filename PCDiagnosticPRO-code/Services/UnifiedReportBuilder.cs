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
        /// <param name="healthReport">HealthReport avec scores UDIS</param>
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

                // 1. Lire le JSON combiné (RÉTROCOMPATIBLE: accepte camelCase ET snake_case)
                if (File.Exists(combinedJsonPath))
                {
                    var jsonContent = await File.ReadAllTextAsync(combinedJsonPath, Encoding.UTF8);
                    using var doc = JsonDocument.Parse(jsonContent);
                    var root = doc.RootElement;
                    
                    // ROBUSTE: Chercher capteurs C# avec fallback snake_case → camelCase
                    JsonElement sensorsElement = default;
                    bool foundSensors = TryGetPropertyRobust(root, out sensorsElement, "sensors_csharp", "sensorsCsharp");
                    
                    if (foundSensors && sensorsElement.ValueKind == JsonValueKind.Object)
                    {
                        try
                        {
                            sensors = JsonSerializer.Deserialize<HardwareSensorsResult>(sensorsElement.GetRawText());
                            App.LogMessage($"[UnifiedReport] Capteurs C# chargés depuis JSON combiné (clés trouvées)");
                        }
                        catch (Exception ex)
                        {
                            App.LogMessage($"[UnifiedReport] ERREUR désérialisation capteurs: {ex.Message}");
                        }
                    }
                    else
                    {
                        App.LogMessage($"[UnifiedReport] ATTENTION: Aucune clé capteurs trouvée (sensors_csharp/sensorsCsharp)");
                    }
                    
                    // ROBUSTE: Chercher données PS avec fallback snake_case → camelCase
                    JsonElement psElement = default;
                    bool foundPs = TryGetPropertyRobust(root, out psElement, "scan_powershell", "scanPowershell");
                    
                    if (foundPs)
                    {
                        psData = psElement.Clone();
                        App.LogMessage($"[UnifiedReport] Données PS chargées depuis JSON combiné");
                    }
                    else
                    {
                        App.LogMessage($"[UnifiedReport] ATTENTION: Aucune clé PS trouvée (scan_powershell/scanPowershell)");
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

                // 6. Section COLLECTE: ERREURS ET LIMITATIONS (BLOC 3)
                BuildCollectionDiagnosticsSection(sb, healthReport, sensors, psData);

                // 7. Section SCORE & GRADE ENGINE
                BuildScoreSection(sb, healthReport);

                // 8. Footer avec signature
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
                sb.AppendLine("     Raison: Objet HardwareSensorsResult null (JSON combiné mal lu ou capteurs non collectés)");
                sb.AppendLine();
                App.LogMessage("[UnifiedReport] Section capteurs: sensors == null");
                return;
            }
            
            // Vérifier si les capteurs ont été réellement collectés
            var (available, total) = sensors.GetAvailabilitySummary();
            App.LogMessage($"[UnifiedReport] Section capteurs: {available}/{total} capteurs disponibles");

            sb.AppendLine($"  Collecté à : {sensors.CollectedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            // CPU avec VALIDATION (BLOC 2: règle P1)
            sb.AppendLine("  ┌─ CPU ─────────────────────────────────────────────────────────────────────┐");
            var cpuTempValidation = MetricValidation.ValidateCpuTemp(sensors.Cpu.CpuTempC);
            WriteValidatedMetric(sb, "Temperature", cpuTempValidation, "°C", sensors.Cpu.CpuTempSource);
            sb.AppendLine("  └─────────────────────────────────────────────────────────────────────────────┘");
            sb.AppendLine();

            // GPU avec VALIDATION
            sb.AppendLine("  ┌─ GPU ─────────────────────────────────────────────────────────────────────┐");
            WriteMetricString(sb, "Nom", sensors.Gpu.Name, "HardwareSensorsCollector");
            var gpuTempValidation = MetricValidation.ValidateGpuTemp(sensors.Gpu.GpuTempC);
            WriteValidatedMetric(sb, "Temperature", gpuTempValidation, "°C", "HardwareSensorsCollector");
            WriteMetric(sb, "Charge GPU", sensors.Gpu.GpuLoadPercent, "%", "HardwareSensorsCollector");
            WriteMetric(sb, "VRAM Total", sensors.Gpu.VramTotalMB, "MB", "HardwareSensorsCollector");
            WriteMetric(sb, "VRAM Utilisée", sensors.Gpu.VramUsedMB, "MB", "HardwareSensorsCollector");
            
            // Validation VRAM (règle P1: used > total = invalide)
            var vramValidation = MetricValidation.ValidateVram(sensors.Gpu.VramTotalMB, sensors.Gpu.VramUsedMB);
            if (vramValidation.Validity == MetricValidity.Valid)
            {
                var vramPct = (vramValidation.Value.used / vramValidation.Value.total) * 100;
                sb.AppendLine($"  │  VRAM Usage %       : {vramPct:F1}%");
                sb.AppendLine($"  │    Source           : Derived (VramUsed/VramTotal)");
                sb.AppendLine($"  │    Validity         : ✓ Valid");
            }
            else if (vramValidation.Validity == MetricValidity.Invalid)
            {
                sb.AppendLine($"  │  VRAM Usage %       : N/A");
                sb.AppendLine($"  │    Source           : Derived");
                sb.AppendLine($"  │    Validity         : ✗ Invalid ({vramValidation.Reason})");
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
                
                var validDiskTemps = sensors.Disks
                    .Where(d => d.TempC.Available && !MetricValidation.IsSentinelValue(d.TempC.Value))
                    .Select(d => d.TempC.Value)
                    .ToList();
                    
                if (validDiskTemps.Any())
                {
                    var maxTemp = validDiskTemps.Max();
                    sb.AppendLine($"  │  ──────────────────────────────────────────────────────────────────────");
                    sb.AppendLine($"  │  TEMP MAX DISQUES   : {maxTemp:F0}°C");
                    sb.AppendLine($"  │    Source           : Derived (Max of {validDiskTemps.Count} disks)");
                    sb.AppendLine($"  │    Validity         : ✓ Valid");
                    
                    if (maxTemp > 60)
                        sb.AppendLine($"  │    ⚠️ ATTENTION    : Température élevée (>60°C)");
                    else if (maxTemp > 50)
                        sb.AppendLine($"  │    ℹ️ INFO         : Température à surveiller (>50°C)");
                }
            }
            sb.AppendLine("  └─────────────────────────────────────────────────────────────────────────────┘");
            sb.AppendLine();
        }
        
        /// <summary>
        /// Écrit une métrique validée avec son statut de validité
        /// </summary>
        private static void WriteValidatedMetric(StringBuilder sb, string label, ValidatedMetric<double> metric, string unit, string source)
        {
            var padLabel = label.PadRight(18);
            
            switch (metric.Validity)
            {
                case MetricValidity.Valid:
                    sb.AppendLine($"  │  {padLabel} : {metric.Value:F1}{unit}");
                    sb.AppendLine($"  │    Source           : {source}");
                    sb.AppendLine($"  │    Validity         : ✓ Valid");
                    break;
                    
                case MetricValidity.Invalid:
                    sb.AppendLine($"  │  {padLabel} : Non disponible (capteur invalide)");
                    sb.AppendLine($"  │    Source           : {source}");
                    sb.AppendLine($"  │    Validity         : ✗ Invalid");
                    sb.AppendLine($"  │    Raison           : {metric.Reason ?? "Valeur hors plage"}");
                    break;
                    
                case MetricValidity.Missing:
                default:
                    sb.AppendLine($"  │  {padLabel} : N/A");
                    sb.AppendLine($"  │    Source           : {source}");
                    sb.AppendLine($"  │    Validity         : ○ Missing");
                    sb.AppendLine($"  │    Raison           : {metric.Reason ?? "Capteur indisponible"}");
                    break;
            }
        }

        private static void WriteMetric(StringBuilder sb, string label, MetricValue<double> metric, string unit, string source)
        {
            var padLabel = label.PadRight(18);
            var metricSource = string.IsNullOrWhiteSpace(metric.Source) ? source : metric.Source;
            if (metric.Available)
            {
                sb.AppendLine($"  │  {padLabel} : {metric.Value:F1}{unit}");
                sb.AppendLine($"  │    Source           : {metricSource}");
                sb.AppendLine($"  │    Confidence       : High");
            }
            else
            {
                sb.AppendLine($"  │  {padLabel} : N/A");
                sb.AppendLine($"  │    Source           : {metricSource}");
                sb.AppendLine($"  │    Reason           : {metric.Reason ?? "Indisponible"}");
                sb.AppendLine($"  │    Confidence       : Low");
            }
        }

        private static void WriteMetricString(StringBuilder sb, string label, MetricValue<string> metric, string source)
        {
            var padLabel = label.PadRight(18);
            var metricSource = string.IsNullOrWhiteSpace(metric.Source) ? source : metric.Source;
            if (metric.Available)
            {
                sb.AppendLine($"  │  {padLabel} : {metric.Value}");
                sb.AppendLine($"  │    Source           : {metricSource}");
                sb.AppendLine($"  │    Confidence       : High");
            }
            else
            {
                sb.AppendLine($"  │  {padLabel} : N/A");
                sb.AppendLine($"  │    Source           : {metricSource}");
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

        /// <summary>
        /// BLOC 3: Section "Collecte : erreurs et limitations"
        /// Expose transparentement tous les problèmes de collecte
        /// </summary>
        private static void BuildCollectionDiagnosticsSection(StringBuilder sb, HealthReport? healthReport, HardwareSensorsResult? sensors, JsonElement? psData)
        {
            sb.AppendLine(SEPARATOR);
            sb.AppendLine("  [COLLECTE : ERREURS ET LIMITATIONS]");
            sb.AppendLine(SEPARATOR);
            sb.AppendLine();

            var diagnostics = new CollectionDiagnostics();
            
            // 1. Erreurs PowerShell (WMI_ERROR, TEMP_WARN, etc.)
            if (healthReport?.Errors != null && healthReport.Errors.Count > 0)
            {
                diagnostics.AddFromPsErrors(healthReport.Errors);
            }
            
            // 2. MissingData PowerShell
            if (healthReport?.MissingData != null && healthReport.MissingData.Count > 0)
            {
                diagnostics.AddFromPsMissingData(healthReport.MissingData);
            }
            
            // 3. Validation capteurs C# (détection valeurs invalides)
            if (sensors != null)
            {
                var cpuValid = MetricValidation.ValidateCpuTemp(sensors.Cpu.CpuTempC);
                if (cpuValid.Validity == MetricValidity.Invalid)
                    diagnostics.AddInvalidMetric("CPU Temperature", cpuValid.Reason ?? "valeur invalide");
                else if (cpuValid.Validity == MetricValidity.Missing)
                    diagnostics.MissingData.Add($"CPU Temperature: {cpuValid.Reason}");
                    
                var gpuValid = MetricValidation.ValidateGpuTemp(sensors.Gpu.GpuTempC);
                if (gpuValid.Validity == MetricValidity.Invalid)
                    diagnostics.AddInvalidMetric("GPU Temperature", gpuValid.Reason ?? "valeur invalide");
                    
                var vramValid = MetricValidation.ValidateVram(sensors.Gpu.VramTotalMB, sensors.Gpu.VramUsedMB);
                if (vramValid.Validity == MetricValidity.Invalid)
                    diagnostics.AddInvalidMetric("VRAM", vramValid.Reason ?? "valeur incohérente");
            }
            else
            {
                diagnostics.Warnings.Add("Capteurs hardware C# non disponibles");
            }
            
            // 4. Vérifier PerfCounters pour sentinelles (BLOC 4)
            if (psData.HasValue)
            {
                ExtractPerfCounterDiagnostics(psData.Value, diagnostics);
            }
            
            // === AFFICHAGE ===
            
            // Statut global : priorité au HealthReport (collectorErrorsLogical, CollectionStatus) pour cohérence JSON↔TXT
            string statusLabel = diagnostics.CollectionStatus;
            if (healthReport != null)
            {
                if (healthReport.CollectionStatus == "FAILED") statusLabel = "ÉCHOUÉE";
                else if (healthReport.CollectionStatus == "PARTIAL") statusLabel = "PARTIELLE";
                else if (healthReport.CollectionStatus == "OK") statusLabel = "COMPLÈTE";
                sb.AppendLine($"  Erreurs collecteur (logique): {healthReport.CollectorErrorsLogical}");
            }
            var statusIcon = statusLabel switch
            {
                "COMPLÈTE" => "✅",
                "PARTIELLE" => "⚠️",
                "ÉCHOUÉE" => "❌",
                _ => "❓"
            };
            sb.AppendLine($"  STATUT COLLECTE: {statusIcon} {statusLabel}");
            sb.AppendLine();
            
            // Erreurs collecteur (WMI_ERROR, TEMP_WARN, etc.)
            if (diagnostics.Errors.Count > 0)
            {
                sb.AppendLine("  ┌─ ERREURS COLLECTEUR ──────────────────────────────────────────────────────┐");
                foreach (var err in diagnostics.Errors)
                {
                    sb.AppendLine($"  │  ❌ {err}");
                }
                sb.AppendLine("  └─────────────────────────────────────────────────────────────────────────────┘");
                sb.AppendLine();
            }
            
            // Métriques invalides
            if (diagnostics.InvalidMetrics.Count > 0)
            {
                sb.AppendLine("  ┌─ MÉTRIQUES INVALIDES (valeurs hors plage/sentinelles) ────────────────────┐");
                foreach (var inv in diagnostics.InvalidMetrics)
                {
                    sb.AppendLine($"  │  ⚠️ {inv}");
                }
                sb.AppendLine("  └─────────────────────────────────────────────────────────────────────────────┘");
                sb.AppendLine();
            }
            
            // Données manquantes
            if (diagnostics.MissingData.Count > 0)
            {
                sb.AppendLine("  ┌─ DONNÉES MANQUANTES ──────────────────────────────────────────────────────┐");
                foreach (var miss in diagnostics.MissingData.Take(15))
                {
                    sb.AppendLine($"  │  ○ {miss}");
                }
                if (diagnostics.MissingData.Count > 15)
                    sb.AppendLine($"  │  ... et {diagnostics.MissingData.Count - 15} autres");
                sb.AppendLine("  └─────────────────────────────────────────────────────────────────────────────┘");
                sb.AppendLine();
            }
            
            // Avertissements
            if (diagnostics.Warnings.Count > 0)
            {
                sb.AppendLine("  ┌─ LIMITATIONS CONNUES ─────────────────────────────────────────────────────┐");
                foreach (var warn in diagnostics.Warnings)
                {
                    sb.AppendLine($"  │  ℹ️ {warn}");
                }
                sb.AppendLine("  └─────────────────────────────────────────────────────────────────────────────┘");
                sb.AppendLine();
            }
            
            // Si tout va bien
            if (diagnostics.Errors.Count == 0 && diagnostics.InvalidMetrics.Count == 0 && diagnostics.MissingData.Count == 0)
            {
                sb.AppendLine("  ✅ Aucune erreur de collecte détectée");
                sb.AppendLine();
            }
        }
        
        /// <summary>
        /// BLOC 4: Extrait les diagnostics des PerfCounters (sentinelles)
        /// </summary>
        private static void ExtractPerfCounterDiagnostics(JsonElement psData, CollectionDiagnostics diagnostics)
        {
            try
            {
                // Chercher dans sections.PerformanceCounters ou PerformanceCounters direct
                JsonElement perfCounters = default;
                bool found = false;
                
                if (psData.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("PerformanceCounters", out var pc))
                {
                    perfCounters = pc;
                    found = true;
                }
                else if (psData.TryGetProperty("PerformanceCounters", out var pcDirect))
                {
                    perfCounters = pcDirect;
                    found = true;
                }
                
                if (!found) return;
                
                // Vérifier status
                if (perfCounters.TryGetProperty("status", out var status))
                {
                    var s = status.GetString();
                    if (s == "FAILED" || s == "ERROR")
                    {
                        diagnostics.Errors.Add("PerformanceCounters: Collecte échouée");
                        return;
                    }
                }
                
                // Chercher les données
                JsonElement data = perfCounters;
                if (perfCounters.TryGetProperty("data", out var dataElem))
                    data = dataElem;
                
                // Parcourir et détecter sentinelles (-1, NaN)
                foreach (var prop in data.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        var val = prop.Value.GetDouble();
                        var validation = MetricValidation.ValidatePerfCounter(val, prop.Name);
                        
                        if (validation.Validity == MetricValidity.Invalid)
                        {
                            diagnostics.AddInvalidMetric($"PerfCounter.{prop.Name}", validation.Reason ?? "sentinelle");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[UnifiedReport] Erreur extraction PerfCounters: {ex.Message}");
            }
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
            sb.AppendLine("  [SCORE ENGINE — UDIS]");
            sb.AppendLine(SEPARATOR);
            sb.AppendLine();

            sb.AppendLine("  UDIS — UNIFIED DIAGNOSTIC INTELLIGENCE SCORING");
            sb.AppendLine($"  Score global (UDIS): {healthReport.GlobalScore}/100");
            sb.AppendLine($"  GRADE: {healthReport.Grade}");
            sb.AppendLine($"  SÉVÉRITÉ: {healthReport.GlobalSeverity}");
            sb.AppendLine($"  Verdict: {healthReport.GlobalMessage}");
            sb.AppendLine();
            sb.AppendLine("  AFFICHAGE MODE INDUSTRIE (séparé):");
            sb.AppendLine($"    Machine Health Score  : {healthReport.MachineHealthScore}/100 (70% du total)");
            sb.AppendLine($"    Data Reliability Score: {healthReport.DataReliabilityScore}/100 (20% du total)");
            sb.AppendLine($"    Diagnostic Clarity    : {healthReport.DiagnosticClarityScore}/100 (10% du total)");
            sb.AppendLine($"    Source de vérité      : {healthReport.Divergence?.SourceOfTruth ?? "UDIS"}");
            sb.AppendLine($"    AutoFix autorisé      : {(healthReport.AutoFixAllowed ? "Oui" : "Non")}");
            if (healthReport.UdisReport != null)
            {
                sb.AppendLine($"    Profil CPU            : {healthReport.UdisReport.CpuPerformanceTier}");
                sb.AppendLine($"    SystemStabilityIndex  : {healthReport.UdisReport.SystemStabilityIndex}/100");
                sb.AppendLine();
                sb.AppendLine("  MÉTRIQUES ADDITIONNELLES:");
                sb.AppendLine($"    Thermal Score         : {healthReport.UdisReport.ThermalScore}/100 ({healthReport.UdisReport.ThermalStatus})");
                sb.AppendLine($"    Boot Health Score     : {healthReport.UdisReport.BootHealthScore}/100 ({healthReport.UdisReport.BootHealthTier})");
                sb.AppendLine($"    Storage IO Health     : {healthReport.UdisReport.StorageIoHealthScore}/100 ({healthReport.UdisReport.StorageIoStatus})");
                if (healthReport.UdisReport.DownloadMbps.HasValue)
                {
                    sb.AppendLine($"    Network Speed         : {healthReport.UdisReport.DownloadMbps:F1} Mbps ({healthReport.UdisReport.NetworkSpeedTier})");
                    if (healthReport.UdisReport.LatencyMs.HasValue)
                        sb.AppendLine($"    Network Latency       : {healthReport.UdisReport.LatencyMs:F0} ms");
                    if (!string.IsNullOrWhiteSpace(healthReport.UdisReport.NetworkRecommendation))
                        sb.AppendLine($"    Network Advice        : {healthReport.UdisReport.NetworkRecommendation}");
                }
                else
                {
                    sb.AppendLine($"    Network Speed         : Non mesuré");
                }
            }
            sb.AppendLine();

            // Référence PS (lecture seule) vs UDIS
            if (healthReport.Divergence != null && healthReport.Divergence.Delta > 0)
            {
                sb.AppendLine("  RÉFÉRENCE (lecture JSON):");
                sb.AppendLine($"    Score PS (legacy) : {healthReport.Divergence.PowerShellScore}");
                sb.AppendLine($"    Score UDIS        : {healthReport.Divergence.GradeEngineScore}");
                sb.AppendLine($"    Delta             : {healthReport.Divergence.Delta}");
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
            sb.AppendLine("    ✓ UDIS — Unified Diagnostic Intelligence Scoring");
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

        /// <summary>
        /// Recherche robuste de propriété JSON avec fallback sur plusieurs noms de clés.
        /// Permet rétrocompatibilité snake_case / camelCase.
        /// </summary>
        private static bool TryGetPropertyRobust(JsonElement element, out JsonElement value, params string[] propertyNames)
        {
            value = default;
            
            if (element.ValueKind != JsonValueKind.Object)
                return false;
                
            foreach (var name in propertyNames)
            {
                if (element.TryGetProperty(name, out value))
                {
                    App.LogMessage($"[UnifiedReport] Clé JSON trouvée: '{name}'");
                    return true;
                }
            }
            
            // Log des clés disponibles pour debug
            var availableKeys = new List<string>();
            foreach (var prop in element.EnumerateObject())
            {
                availableKeys.Add(prop.Name);
            }
            App.LogMessage($"[UnifiedReport] Clés cherchées: [{string.Join(", ", propertyNames)}] | Clés disponibles: [{string.Join(", ", availableKeys)}]");
            
            return false;
        }
    }
}
