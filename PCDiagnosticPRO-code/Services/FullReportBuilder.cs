using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.ViewModels;
using PCDiagnosticPro.DiagnosticsSignals;
using PCDiagnosticPro.Services.NetworkDiagnostics;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Construit FullReportViewModel à partir du JSON combiné (scan_result_combined.json).
    /// B — Affiche 100% des données collectées, déduplique (C# prio capteurs, PS prio inventaire),
    /// ajoute Evidence/Source par section, et calcule un compteur de couverture UI.
    /// </summary>
    public static class FullReportBuilder
    {
        private const string Na = "Non disponible";

        // Compteurs de couverture UI (thread-local per build)
        [ThreadStatic] private static int _detected;
        [ThreadStatic] private static int _mapped;

        public static FullReportViewModel? BuildFromJson(string jsonContent)
        {
            if (string.IsNullOrWhiteSpace(jsonContent))
                return null;
            try
            {
                var combined = JsonSerializer.Deserialize<CombinedScanResult>(jsonContent, HardwareSensorsResult.JsonOptions);
                return combined != null ? BuildFromCombined(combined) : null;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[FullReportBuilder] Erreur désérialisation: {ex.Message}");
                return null;
            }
        }

        public static FullReportViewModel BuildFromCombined(CombinedScanResult combined)
        {
            _detected = 0;
            _mapped = 0;
            try
            {
            var vm = new FullReportViewModel();
            var snapshot = combined.DiagnosticSnapshot;
            var metadata = combined.Metadata;
            var errors = combined.Errors ?? new List<ErrorExtract>();
            var missingData = combined.MissingData ?? new List<string>();

            vm.RunId = metadata?.RunId ?? "";
            vm.ScanDate = ParseTimestamp(metadata?.Timestamp);
            vm.Status = DeriveStatus(metadata, errors, missingData);
            vm.CoveragePercent = snapshot?.CollectionQuality?.CoveragePercent ?? 0;
            if (combined.DiagnosticsQuality != null)
            {
                vm.ReliabilityPercent = combined.DiagnosticsQuality.ReliabilityScore;
                vm.ActionabilityPercent = combined.DiagnosticsQuality.ActionabilityScore;
            }
            else
            {
                vm.ReliabilityPercent = snapshot?.CollectionQuality?.CoveragePercent ?? 0;
                vm.ActionabilityPercent = 0;
            }

            var sections = new List<ReportSectionViewModel>
            {
                BuildPerformanceSection(snapshot, combined),
                BuildSystemSection(snapshot, metadata),
                BuildCpuSection(snapshot, combined.SensorsCsharp, combined),
                BuildGpuSection(snapshot, combined.SensorsCsharp),
                BuildMemorySection(snapshot),
                BuildStorageSection(snapshot, combined.SensorsCsharp),
                BuildNetworkSection(snapshot, combined.NetworkDiagnostics),
                BuildStabilitySection(snapshot, combined),
                BuildSecuritySection(snapshot, combined.SecurityInfoCsharp),
                BuildUpdatesSection(snapshot, combined.UpdatesCsharp),
                BuildDevicesSection(snapshot, combined.DriverInventory),
                BuildCollectorErrorsSection(errors, missingData, combined.CollectorDiagnostics, snapshot),
                BuildTechnicalLogSection(snapshot, combined)
            };

            // Fallback: when DiagnosticSnapshot is null or sections are empty, fill from scan_powershell so report is never blank
            if (snapshot == null || sections.Any(s => s.KeyValues.Count == 0))
                FillSectionsFromScanPowershell(combined, sections);

            // Ensure no section is completely empty (placeholder so UI always shows something)
            foreach (var s in sections)
            {
                if (s.KeyValues.Count == 0)
                {
                    AddKV(s, "Information", "Données non disponibles pour cette section.", "");
                    s.SummaryLine1 = "Aucune donnée collectée.";
                    s.SummaryLine2 = "Lancer un scan complet puis rouvrir le rapport intégral.";
                    if (s.SectionScore == 0) s.SectionScore = 50;
                }
            }

            foreach (var s in sections)
                vm.Sections.Add(s);

            vm.UiDetectedFields = _detected;
            vm.UiMappedFields = _mapped;
            App.LogMessage($"[FullReportBuilder] Couverture UI: {_mapped}/{_detected} champs ({(_detected > 0 ? 100.0 * _mapped / _detected : 0):F0}%)");

            // UI log: Non disponible count per section (pour diagnostic)
            foreach (var s in sections)
            {
                var naCount = s.KeyValues.Count(kv => string.Equals(kv.Value, Na, StringComparison.Ordinal));
                if (naCount > 0)
                    App.LogMessage($"[UI_NA] Section={s.Id} champs_NA={naCount} (total_lignes={s.KeyValues.Count})");
            }

            LogUnmappedFieldWarnings(combined, sections);
            vm.SelectFirstSection();
            return vm;
            }
            catch (Exception)
            {
                throw;
            }
        }

        /// <summary>When DiagnosticSnapshot is null or sections are empty, fill KeyValues from scan_powershell so the report is never blank.</summary>
        private static void FillSectionsFromScanPowershell(CombinedScanResult combined, List<ReportSectionViewModel> sections)
        {
            try
            {
                var ps = combined.ScanPowershell;
                if (ps.ValueKind != JsonValueKind.Object) return;
                // Support both root "sections" and "data.sections" (some PS outputs wrap under data)
                if (!ps.TryGetProperty("sections", out var sectionsEl) || sectionsEl.ValueKind != JsonValueKind.Object)
                {
                    if (!ps.TryGetProperty("data", out var data) || !data.TryGetProperty("sections", out sectionsEl) || sectionsEl.ValueKind != JsonValueKind.Object)
                        return;
                }

                var byId = sections.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

                // System / OS
                var sys = byId.GetValueOrDefault("System");
                if (sys != null && sys.KeyValues.Count == 0)
                {
                    if (combined.Metadata != null)
                    {
                        AddKV(sys, "Script version", combined.Metadata.Version, "");
                        AddKV(sys, "RunId", combined.Metadata.RunId, "");
                        AddKV(sys, "Durée scan", combined.Metadata.DurationSeconds > 0 ? $"{combined.Metadata.DurationSeconds:F1}" : null, "s");
                        AddKV(sys, "PartialFailure", combined.Metadata.PartialFailure ? "Oui" : "Non", "");
                    }
                    if (sectionsEl.TryGetProperty("OS", out var osSec) && osSec.TryGetProperty("data", out var osData))
                    {
                        AddKVIfNew(sys, "Caption", GetStringFromJe(osData, "caption"), "");
                        AddKVIfNew(sys, "Version", GetStringFromJe(osData, "version"), "");
                        AddKVIfNew(sys, "Architecture", GetStringFromJe(osData, "architecture"), "");
                        AddKVIfNew(sys, "Dernier démarrage", GetStringFromJe(osData, "lastBootUpTime"), "");
                    }
                    if (sectionsEl.TryGetProperty("MachineIdentity", out var mi) && mi.TryGetProperty("data", out var miData))
                    {
                        AddKVIfNew(sys, "Nom d'hôte", GetStringFromJe(miData, "hostname") ?? GetStringFromJe(miData, "computerName"), "");
                        AddKVIfNew(sys, "Utilisateur", GetStringFromJe(miData, "username"), "");
                    }
                    if (sys.KeyValues.Count > 0)
                    {
                        sys.SummaryLine1 = sys.KeyValues.FirstOrDefault(kv => kv.Key.Contains("Caption") || kv.Key.Contains("Version"))?.Value ?? "Données depuis scan PowerShell.";
                        sys.SummaryLine2 = "Source: scan_powershell (fallback)";
                        sys.SectionScore = 70;
                    }
                }

                // CPU (cpus can be Array or Object in PS output)
                var cpu = byId.GetValueOrDefault("CPU");
                if (cpu != null && cpu.KeyValues.Count == 0 && sectionsEl.TryGetProperty("CPU", out var cpuSec) && cpuSec.TryGetProperty("data", out var cpuData))
                {
                    if (cpuData.TryGetProperty("cpus", out var cpus))
                    {
                        var c0 = GetFirstElementFromArrayOrObject(cpus);
                        if (c0.HasValue)
                        {
                            AddKV(cpu, "Modèle", GetStringFromJe(c0.Value, "name"), "");
                            AddKV(cpu, "Cœurs", GetIntFromJe(c0.Value, "cores"), "");
                            AddKV(cpu, "Threads", GetIntFromJe(c0.Value, "threads"), "");
                            AddKV(cpu, "Charge", GetDoubleFromJe(c0.Value, "currentLoad"), "%");
                        }
                    }
                    AddKVIfNew(cpu, "RAM totale", GetDoubleFromJe(cpuData, "totalRamGB"), "GB");
                    if (cpu.KeyValues.Count > 0)
                    {
                        cpu.SummaryLine1 = cpu.KeyValues.FirstOrDefault(kv => string.Equals(kv.Key, "Modèle", StringComparison.OrdinalIgnoreCase))?.Value ?? "CPU (PS)";
                        cpu.SummaryLine2 = "Source: scan_powershell.sections.CPU";
                        cpu.SectionScore = 70;
                    }
                }

                // GPU (gpuList can be Array or Object)
                var gpu = byId.GetValueOrDefault("GPU");
                if (gpu != null && gpu.KeyValues.Count == 0 && sectionsEl.TryGetProperty("GPU", out var gpuSec) && gpuSec.TryGetProperty("data", out var gpuData))
                {
                    var gpuListEl = gpuData.TryGetProperty("gpuList", out var gl) ? gl : (gpuData.TryGetProperty("gpus", out var gpus) ? gpus : default);
                    if (gpuListEl.ValueKind != JsonValueKind.Undefined && gpuListEl.ValueKind != JsonValueKind.Null)
                    {
                        var g0 = GetFirstElementFromArrayOrObject(gpuListEl);
                        if (g0.HasValue)
                        {
                            AddKV(gpu, "Modèle", GetStringFromJe(g0.Value, "name"), "");
                            AddKV(gpu, "VRAM", GetDoubleFromJe(g0.Value, "vramTotalMB"), "MB");
                            AddKV(gpu, "Pilote", GetStringFromJe(g0.Value, "driverVersion"), "");
                        }
                    }
                    if (gpu.KeyValues.Count > 0)
                    {
                        gpu.SummaryLine1 = gpu.KeyValues.FirstOrDefault(kv => string.Equals(kv.Key, "Modèle", StringComparison.OrdinalIgnoreCase))?.Value ?? "GPU (PS)";
                        gpu.SummaryLine2 = "Source: scan_powershell.sections.GPU";
                        gpu.SectionScore = 70;
                    }
                }

                // Memory (section Id is "RAM" in FullReportBuilder)
                var mem = byId.GetValueOrDefault("RAM");
                if (mem != null && mem.KeyValues.Count == 0 && sectionsEl.TryGetProperty("Memory", out var memSec) && memSec.TryGetProperty("data", out var memData))
                {
                    AddKV(mem, "RAM totale", GetDoubleFromJe(memData, "totalGB"), "GB");
                    AddKV(mem, "RAM disponible", GetDoubleFromJe(memData, "availableGB"), "GB");
                    AddKV(mem, "RAM utilisée", GetDoubleFromJe(memData, "usedGB"), "GB");
                    if (mem.KeyValues.Count > 0)
                    {
                        mem.SummaryLine1 = $"RAM: {GetDoubleFromJe(memData, "totalGB")} GB total";
                        mem.SummaryLine2 = "Source: scan_powershell.sections.Memory";
                        mem.SectionScore = 70;
                    }
                }

                // Storage (volumes object; disks array or object)
                var storage = byId.GetValueOrDefault("Storage");
                if (storage != null && storage.KeyValues.Count == 0 && sectionsEl.TryGetProperty("Storage", out var storSec) && storSec.TryGetProperty("data", out var storData))
                {
                    if (storData.TryGetProperty("volumes", out var vols) && vols.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var v in vols.EnumerateObject())
                            AddKV(storage, $"Volume {v.Name}", v.Value.ValueKind == JsonValueKind.String ? v.Value.GetString() : v.Value.ToString(), "");
                    }
                    if (storage.KeyValues.Count == 0 && storData.TryGetProperty("disks", out var disks))
                    {
                        int idx = 0;
                        foreach (var d in EnumerateArrayOrObject(disks))
                        {
                            if (idx >= 5) break;
                            AddKV(storage, $"Disque {idx + 1}", GetStringFromJe(d, "model") ?? GetStringFromJe(d, "name"), "");
                            idx++;
                        }
                    }
                    if (storage.KeyValues.Count > 0)
                    {
                        storage.SummaryLine1 = $"Stockage: {storage.KeyValues.Count} élément(s)";
                        storage.SummaryLine2 = "Source: scan_powershell.sections.Storage";
                        storage.SectionScore = 70;
                    }
                }

                // Network (adapters can be Array or Object)
                var net = byId.GetValueOrDefault("Network");
                if (net != null && net.KeyValues.Count == 0 && sectionsEl.TryGetProperty("Network", out var netSec) && netSec.TryGetProperty("data", out var netData))
                {
                    if (netData.TryGetProperty("adapters", out var adapters))
                    {
                        int idx = 0;
                        foreach (var a in EnumerateArrayOrObject(adapters))
                        {
                            if (idx >= 3) break;
                            AddKV(net, $"Adaptateur {idx + 1}", GetStringFromJe(a, "name") ?? GetStringFromJe(a, "description"), "");
                            idx++;
                        }
                    }
                    if (net.KeyValues.Count > 0)
                    {
                        net.SummaryLine1 = $"Réseau: {net.KeyValues.Count} adaptateur(s)";
                        net.SummaryLine2 = "Source: scan_powershell.sections.Network";
                        net.SectionScore = 70;
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[FullReportBuilder] FillSectionsFromScanPowershell: {ex.Message}");
            }
        }

        private static string? GetStringFromJe(JsonElement je, string prop)
        {
            if (je.TryGetProperty(prop, out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i.ToString();
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                if (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) return v.GetBoolean() ? "Oui" : "Non";
            }
            return null;
        }

        private static object? GetIntFromJe(JsonElement je, string prop)
        {
            if (je.TryGetProperty(prop, out var v) && v.TryGetInt32(out var i)) return i;
            return null;
        }

        private static object? GetDoubleFromJe(JsonElement je, string prop)
        {
            if (je.TryGetProperty(prop, out var v) && v.TryGetDouble(out var d)) return d;
            if (je.TryGetProperty(prop, out var v2) && v2.TryGetInt32(out var i)) return (double)i;
            return null;
        }

        /// <summary>Get first element from JSON array or object (PS sometimes outputs cpus/gpuList as object).</summary>
        private static JsonElement? GetFirstElementFromArrayOrObject(JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array && je.GetArrayLength() > 0)
                return je[0];
            if (je.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in je.EnumerateObject())
                    return p.Value;
            }
            return null;
        }

        private static IEnumerable<JsonElement> EnumerateArrayOrObject(JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
            {
                for (int i = 0; i < je.GetArrayLength(); i++)
                    yield return je[i];
            }
            else if (je.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in je.EnumerateObject())
                    yield return p.Value;
            }
        }

        /// <summary>Audit: logue un warning si des données collectées ne sont pas rendues dans le rapport intégral.</summary>
        private static void LogUnmappedFieldWarnings(CombinedScanResult combined, List<ReportSectionViewModel> sections)
        {
            var allKeys = new HashSet<string>(sections.SelectMany(s => s.KeyValues.Select(kv => kv.Key)), StringComparer.OrdinalIgnoreCase);
            if (combined.DiagnosticSignals != null)
            {
                foreach (var kv in combined.DiagnosticSignals)
                {
                    if (!kv.Value.Available) continue;
                    var expected = kv.Key switch
                    {
                        "cpuThrottle" => "Throttling",
                        "whea" => "WHEA",
                        "driverStability" => "BSOD",
                        _ => null
                    };
                    if (expected != null && !allKeys.Any(k => k.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0))
                        App.LogMessage($"[FullReportBuilder] Champ collecté non rendu: diagnostic_signals.{kv.Key}");
                }
            }
            if (combined.EventLogsDetailed != null && combined.EventLogsDetailed.Count > 0)
            {
                if (!allKeys.Any(k => k.IndexOf("EventLog", StringComparison.OrdinalIgnoreCase) >= 0 || k.IndexOf("WHEA", StringComparison.OrdinalIgnoreCase) >= 0))
                    App.LogMessage("[FullReportBuilder] Champ collecté non rendu: event_logs_detailed");
            }
            if (combined.MinidumpsDetailed != null && combined.MinidumpsDetailed.Count > 0)
            {
                if (!allKeys.Any(k => k.IndexOf("Minidump", StringComparison.OrdinalIgnoreCase) >= 0 || k.IndexOf("BSOD", StringComparison.OrdinalIgnoreCase) >= 0))
                    App.LogMessage("[FullReportBuilder] Champ collecté non rendu: minidumps_detailed");
            }
        }

        // ===== HELPERS =====

        private static DateTime ParseTimestamp(string? ts)
        {
            if (string.IsNullOrWhiteSpace(ts)) return DateTime.Now;
            if (DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt;
            return DateTime.Now;
        }

        private static string DeriveStatus(ScanMetadataExtract? metadata, List<ErrorExtract> errors, List<string> missingData)
        {
            if (metadata?.PartialFailure == true || errors.Count > 0)
                return "Partiel";
            if (missingData.Count > 10)
                return "Partiel";
            return "OK";
        }

        /// <summary>Ajoute une ligne KV + incrémente le compteur de couverture.</summary>
        private static void AddKV(ReportSectionViewModel section, string key, object? value, string unit, IssueLevel level = IssueLevel.Info, string? source = null)
        {
            _detected++;
            var valueStr = FormatValue(value);
            if (valueStr != Na) _mapped++;
            var displayKey = source != null ? $"{key} [{source}]" : key;
            section.KeyValues.Add(new KeyValueRow { Key = displayKey, Value = valueStr, Unit = unit, Level = level });
        }

        /// <summary>Ajoute une ligne KV uniquement si la valeur n'est pas déjà présente (déduplication).</summary>
        private static void AddKVIfNew(ReportSectionViewModel section, string key, object? value, string unit, IssueLevel level = IssueLevel.Info, string? source = null)
        {
            // Skip if already present (dedup: first write wins = higher priority source)
            if (section.KeyValues.Any(kv => kv.Key.StartsWith(key, StringComparison.OrdinalIgnoreCase)))
                return;
            AddKV(section, key, value, unit, level, source);
        }

        private static string FormatValue(object? value)
        {
            if (value == null) return Na;
            if (value is double d) return d.ToString("F1");
            if (value is float f) return f.ToString("F1");
            var s = value.ToString();
            return string.IsNullOrWhiteSpace(s) ? Na : s;
        }

        /// <summary>Helper: extract all metrics from a snapshot group, adding them as KV rows.</summary>
        private static void AddMetricsGroup(ReportSectionViewModel section, DiagnosticSnapshot? snapshot, string groupKey, string source = "PS")
        {
            var metrics = snapshot?.Metrics?.GetValueOrDefault(groupKey);
            if (metrics == null) return;
            foreach (var kv in metrics)
            {
                var m = kv.Value;
                var val = m.Available ? m.Value : null;
                var src = !string.IsNullOrEmpty(m.Source) ? m.Source : source;
                AddKV(section, kv.Key, val, m.Unit ?? "", m.Available ? IssueLevel.Info : IssueLevel.Warning, src);
            }
        }

        /// <summary>Like AddMetricsGroup but skips keys in excludeKeys (e.g. vramUsedMB/vramTotalMB to avoid duplicate with C#).</summary>
        private static void AddMetricsGroupExcluding(ReportSectionViewModel section, DiagnosticSnapshot? snapshot, string groupKey, HashSet<string> excludeKeys, string source = "PS")
        {
            var metrics = snapshot?.Metrics?.GetValueOrDefault(groupKey);
            if (metrics == null) return;
            foreach (var kv in metrics)
            {
                if (excludeKeys.Contains(kv.Key)) continue;
                var m = kv.Value;
                var val = m.Available ? m.Value : null;
                var src = !string.IsNullOrEmpty(m.Source) ? m.Source : source;
                AddKV(section, kv.Key, val, m.Unit ?? "", m.Available ? IssueLevel.Info : IssueLevel.Warning, src);
            }
        }

        private static string BuildEvidenceText(string? generatedAt, string? runId, params string[] sources)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(generatedAt)) sb.AppendLine($"Généré: {generatedAt}");
            if (!string.IsNullOrEmpty(runId)) sb.AppendLine($"RunId: {runId}");
            foreach (var s in sources.Where(x => !string.IsNullOrEmpty(x)))
                sb.AppendLine($"Source: {s}");
            return sb.ToString().TrimEnd();
        }

        // ===== SECTION BUILDERS =====

        private static ReportSectionViewModel BuildSystemSection(DiagnosticSnapshot? snapshot, ScanMetadataExtract? metadata)
        {
            var section = new ReportSectionViewModel { Id = "System", Title = "Système", Level = IssueLevel.Info, SectionScore = 100 };
            var machine = snapshot?.Machine;
            if (machine != null)
            {
                AddKV(section, "Nom d'hôte", machine.Hostname, "");
                AddKV(section, "OS", machine.Os, "");
                AddKV(section, "Version", machine.OsVersion, "");
                AddKV(section, "Build", machine.OsBuild, "");
                AddKV(section, "Architecture", machine.Architecture, "");
                AddKV(section, "CPU (PS)", machine.CpuName, "");
                AddKV(section, "Dernier démarrage", machine.LastBootTime, "");
                AddKV(section, "Uptime", machine.Uptime, "");
                AddKV(section, "RAM totale", machine.TotalRamGB, "GB");
                AddKV(section, "Administrateur", machine.IsAdmin ? "Oui" : "Non", "");
                AddKV(section, "Date d'installation", machine.InstallDate, "");
                section.SummaryLine1 = $"{machine.Os ?? Na} — {machine.OsVersion ?? Na}";
                section.SummaryLine2 = $"Uptime: {machine.Uptime ?? Na}";
            }
            else
            {
                section.SummaryLine1 = "Données système non disponibles.";
                section.SummaryLine2 = "Lancer un scan pour collecter les informations.";
                section.SectionScore = 0;
            }
            // Metadata
            if (metadata != null)
            {
                AddKV(section, "Script version", metadata.Version, "");
                AddKV(section, "Durée scan", metadata.DurationSeconds > 0 ? $"{metadata.DurationSeconds:F1}" : null, "s");
                AddKV(section, "PartialFailure", metadata.PartialFailure ? "Oui" : "Non", "");
            }
            // Additional from snapshot os/boot groups if available
            AddMetricsGroup(section, snapshot, "os", "PS");
            AddMetricsGroup(section, snapshot, "boot", "PS");
            section.EvidenceText = BuildEvidenceText(snapshot?.GeneratedAt, metadata?.RunId, "DiagnosticSnapshot.Machine", "Metadata");
            return section;
        }

        private static ReportSectionViewModel BuildCpuSection(DiagnosticSnapshot? snapshot, HardwareSensorsResult? sensors, CombinedScanResult combined)
        {
            var section = new ReportSectionViewModel { Id = "CPU", Title = "CPU", Level = IssueLevel.Info };

            // C# temperature first (single source for real-time) to avoid "Non disponible" when C# has value
            double? csharpTemp = null;
            if (sensors?.Cpu?.CpuTempC?.Available == true)
            {
                csharpTemp = sensors.Cpu.CpuTempC.Value;
                AddKV(section, "temperature", csharpTemp, "°C", IssueLevel.Info, $"C# ({sensors.Cpu.CpuTempSource})");
            }
            if (sensors?.Cpu?.CpuLoadPercent?.Available == true)
                AddKVIfNew(section, "cpuLoadPercent", sensors.Cpu.CpuLoadPercent.Value, "%", IssueLevel.Info, "C#");

            // Source température / Méthode lecture (Rapport intégral)
            var tempSource = sensors?.Cpu?.CpuTempC?.Available == true
                ? (sensors.Cpu.CpuTempSource ?? "C#")
                : (sensors?.Cpu?.CpuTempC?.Reason ?? "Non disponible");
            AddKVIfNew(section, "Source température", tempSource, "", IssueLevel.Info, "C#");

            // Documentation : méthodes passives (aucun stress test, aucun signal)
            AddKVIfNew(section, "Méthodes de collecte température",
                "Lecture passive (capteurs LibreHardwareMonitor ou WMI Thermal Zone). Aucun stress test, aucun benchmark. Aucun code ne provoque de charge CPU pour révéler la température.",
                "", IssueLevel.Info, "C#");

            // Throttling from diagnostic signals (cpuThrottle) — detailed display
            AddCpuThrottleDetails(section, combined.DiagnosticSignals);

            // PS metrics (inventory); exclude "temperature" so we keep C# value and avoid duplicate/NA
            var cpuExclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "temperature" };
            AddMetricsGroupExcluding(section, snapshot, "cpu", cpuExclude, "PS");
            double? psTemp = null;
            var metrics = snapshot?.Metrics?.GetValueOrDefault("cpu");
            if (metrics != null && metrics.TryGetValue("temperature", out var tempMetric) && tempMetric.Available && tempMetric.Value is double t)
                psTemp = t;

            // Score & summary
            var temp = csharpTemp ?? psTemp;
            if (section.KeyValues.Count == 0)
            {
                section.SummaryLine1 = "Données CPU non disponibles.";
                section.SummaryLine2 = "Vérifier capteur ou permissions si nécessaire.";
                section.SectionScore = 50;
            }
            else
            {
                section.SectionScore = temp.HasValue && temp.Value > 80 ? 40 : (temp.HasValue ? 85 : 60);
                if (temp.HasValue && temp.Value > 80) { section.HasCritical = true; section.Level = IssueLevel.Critical; }
                section.SummaryLine1 = temp.HasValue ? $"Température CPU: {temp.Value:F0}°C" : "Température CPU non disponible.";
                section.SummaryLine2 = temp.HasValue && temp.Value > 70 ? "Recommandation: vérifier refroidissement." : "Charge et fréquence ci-dessous.";
            }
            section.EvidenceText = BuildEvidenceText(snapshot?.GeneratedAt, null, "DiagnosticSnapshot.Metrics[cpu]", sensors?.Cpu?.CpuTempSource ?? "");
            return section;
        }

        private static ReportSectionViewModel BuildGpuSection(DiagnosticSnapshot? snapshot, HardwareSensorsResult? sensors)
        {
            var section = new ReportSectionViewModel { Id = "GPU", Title = "GPU", Level = IssueLevel.Info };

            // Snapshot GPU metrics EXCEPT vramUsedMB/vramTotalMB — we use only C# for VRAM (single source, Task Manager–aligned)
            var gpuExclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vramUsedMB", "vramTotalMB" };
            AddMetricsGroupExcluding(section, snapshot, "gpu", gpuExclude, "LHM");

            // C# sensors: single source for VRAM Used/Total (D3D Dedicated Memory Used ≈ Task Manager)
            double? vramTotal = null;
            double? vramUsedRetained = null;
            string vramUsedSourceRetained = "N/A";
            if (sensors?.Gpu != null)
            {
                var gpu = sensors.Gpu;
                if (gpu.GpuTempC?.Available == true)
                    AddKVIfNew(section, "Température GPU", gpu.GpuTempC.Value, "°C", IssueLevel.Info, $"C# ({gpu.GpuTempSource})");
                if (gpu.GpuLoadPercent?.Available == true)
                    AddKVIfNew(section, "Charge GPU (3D)", gpu.GpuLoadPercent.Value, "%", IssueLevel.Info, "C# (aligné Gestionnaire des tâches)");
                if (gpu.VramTotalMB?.Available == true)
                {
                    vramTotal = gpu.VramTotalMB.Value;
                    AddKV(section, "VRAM Total", vramTotal, "MB", IssueLevel.Info, "C#");
                }
                if (gpu.VramUsedMB?.Available == true)
                {
                    var v = gpu.VramUsedMB.Value;
                    var used = v is double d ? d : Convert.ToDouble(v);
                    // Reject committed-style value: > 8 GB is not "Dedicated GPU memory" (Task Manager); do not display
                    if (used > 8000)
                    {
                        App.LogMessage($"[VRAM] UI: vramUsed ({used:F0} MB) > 8 GB — rejeté (committed, pas dedicated)");
                    }
                    else if (vramTotal.HasValue && used > vramTotal.Value)
                    {
                        App.LogMessage($"[VRAM] UI: vramUsed ({used:F0} MB) > vramTotal ({vramTotal.Value:F0} MB) — rejeté (invalide)");
                    }
                    else
                    {
                        if (vramTotal.HasValue && used >= vramTotal.Value * 0.95)
                            App.LogMessage($"[VRAM] UI: vramUsed ({used:F0} MB) proche du total — conservé. Source: {gpu.VramUsedSource}");
                        vramUsedRetained = used;
                        vramUsedSourceRetained = gpu.VramUsedSource ?? "C#";
                        AddKV(section, "VRAM Utilisée", used, "MB", IssueLevel.Info, $"C# ({gpu.VramUsedSource})");
                    }
                }
                if (gpu.Name?.Available == true)
                    AddKVIfNew(section, "Nom GPU (C#)", gpu.Name.Value, "", IssueLevel.Info, "C#");
            }
            App.LogMessage($"[VRAM] UI: source={vramUsedSourceRetained}, vramTotal={vramTotal?.ToString("F0") ?? "N/A"} MB, vramUsed(retained)={vramUsedRetained?.ToString("F0") ?? "N/A"} MB");

            double? temp = sensors?.Gpu?.GpuTempC?.Available == true ? sensors.Gpu.GpuTempC.Value : (double?)null;
            if (section.KeyValues.Count == 0)
            {
                section.SummaryLine1 = "Données GPU non disponibles.";
                section.SummaryLine2 = "Capteurs ou pilote non détectés.";
                section.SectionScore = 0;
            }
            else
            {
                section.SectionScore = temp.HasValue && temp.Value > 85 ? 40 : 70;
                if (temp.HasValue && temp.Value > 85) { section.HasCritical = true; section.Level = IssueLevel.Critical; }
                section.SummaryLine1 = temp.HasValue ? $"Température GPU: {temp.Value:F0}°C" : "Température GPU non disponible.";
                section.SummaryLine2 = sensors?.Gpu?.VramUsedMB?.Available == true
                    ? $"VRAM utilisée: {sensors.Gpu.VramUsedMB.Value:F0} MB"
                    : "Détails GPU ci-dessous.";
            }
            section.EvidenceText = BuildEvidenceText(snapshot?.GeneratedAt, null, "DiagnosticSnapshot.Metrics[gpu]", "HardwareSensorsResult.Gpu");
            return section;
        }

        private static ReportSectionViewModel BuildMemorySection(DiagnosticSnapshot? snapshot)
        {
            var section = new ReportSectionViewModel { Id = "RAM", Title = "Mémoire (RAM)", Level = IssueLevel.Info };
            var metrics = snapshot?.Metrics?.GetValueOrDefault("memory");
            double? usagePct = null;
            if (metrics != null)
            {
                foreach (var kv in metrics)
                {
                    var m = kv.Value;
                    var val = m.Available ? m.Value : null;
                    AddKV(section, kv.Key, val, m.Unit ?? "", m.Available ? IssueLevel.Info : IssueLevel.Warning, m.Source);
                    if (val is double pct && (kv.Key.Contains("usage", StringComparison.OrdinalIgnoreCase) || string.Equals(kv.Key, "usedPercent", StringComparison.OrdinalIgnoreCase)))
                        usagePct = pct;
                }
                section.SectionScore = metrics.Values.Count(m => m.Available) * 100 / Math.Max(1, metrics.Count);
            }

            // Process summary
            if (snapshot?.ProcessSummary?.Available == true)
            {
                var ps = snapshot.ProcessSummary;
                AddKV(section, "Processus actifs", ps.TotalProcessCount, "count", IssueLevel.Info, ps.Source);
                if (!string.IsNullOrEmpty(ps.TopCpuProcess))
                    AddKV(section, "Top CPU process", $"{ps.TopCpuProcess} ({ps.TopCpuPercent:F1}%)", "", IssueLevel.Info, ps.Source);
                if (!string.IsNullOrEmpty(ps.TopMemoryProcess))
                    AddKV(section, "Top Mémoire process", $"{ps.TopMemoryProcess} ({ps.TopMemoryMB:F0} MB)", "", IssueLevel.Info, ps.Source);
            }

            if (section.KeyValues.Count == 0)
            {
                section.SummaryLine1 = "Mémoire: données non disponibles.";
                section.SectionScore = 0;
            }
            else
            {
                section.SummaryLine1 = usagePct.HasValue ? $"Utilisation RAM: {usagePct.Value:F0}%" : "Utilisation mémoire disponible ci-dessous.";
                if (usagePct.HasValue && usagePct.Value > 85) { section.HasCritical = true; section.Level = IssueLevel.Warning; section.SectionScore = Math.Min(section.SectionScore, 50); }
            }
            section.SummaryLine2 = "Recommandation: garder au moins 15% libre.";
            section.EvidenceText = BuildEvidenceText(snapshot?.GeneratedAt, null, "DiagnosticSnapshot.Metrics[memory]", "ProcessSummary");
            return section;
        }

        private static ReportSectionViewModel BuildStorageSection(DiagnosticSnapshot? snapshot, HardwareSensorsResult? sensors)
        {
            var section = new ReportSectionViewModel { Id = "Storage", Title = "Stockage", Level = IssueLevel.Info };

            // PS metrics
            AddMetricsGroup(section, snapshot, "storage", "PS");

            // C# disk temperatures (priority for real-time temps)
            double? maxDiskTemp = null;
            if (sensors?.Disks != null)
            {
                foreach (var disk in sensors.Disks)
                {
                    var diskName = disk.Name?.Available == true ? disk.Name.Value : "Disque";
                    if (disk.TempC?.Available == true)
                    {
                        AddKV(section, $"Temp {diskName}", disk.TempC.Value, "°C", IssueLevel.Info, "C# (LHM)");
                        var diskTempVal = Convert.ToDouble(disk.TempC.Value);
                        maxDiskTemp = maxDiskTemp.HasValue ? Math.Max(maxDiskTemp.Value, diskTempVal) : diskTempVal;
                    }
                }
            }

            // Check snapshot metrics for temps too
            var storageMetrics = snapshot?.Metrics?.GetValueOrDefault("storage");
            if (storageMetrics != null)
            {
                foreach (var kv in storageMetrics.Where(m => m.Key.Contains("temp", StringComparison.OrdinalIgnoreCase) && m.Value.Available && m.Value.Value is double))
                {
                    var t = (double)kv.Value.Value!;
                    maxDiskTemp = maxDiskTemp.HasValue ? Math.Max(maxDiskTemp.Value, t) : t;
                }
            }

            if (maxDiskTemp.HasValue && maxDiskTemp.Value > 60) { section.HasCritical = true; section.Level = IssueLevel.Critical; }
            section.SectionScore = maxDiskTemp.HasValue && maxDiskTemp.Value > 60 ? 35 : (section.KeyValues.Count > 0 ? 75 : 0);
            section.SummaryLine1 = maxDiskTemp.HasValue ? $"Température disque max: {maxDiskTemp.Value:F0}°C" : "Données disques disponibles ci-dessous.";
            section.SummaryLine2 = maxDiskTemp.HasValue && maxDiskTemp.Value > 50 ? "Recommandation: améliorer ventilation." : "Vérifier espace disque et santé SMART.";
            section.EvidenceText = BuildEvidenceText(snapshot?.GeneratedAt, null, "DiagnosticSnapshot.Metrics[storage]", "HardwareSensorsResult.Disks");
            return section;
        }

        private static ReportSectionViewModel BuildNetworkSection(DiagnosticSnapshot? snapshot, NetworkDiagnosticsResult? netDiag)
        {
            var section = new ReportSectionViewModel { Id = "Network", Title = "Réseau", Level = IssueLevel.Info };

            // PS metrics
            AddMetricsGroup(section, snapshot, "network", "PS");

            // Network diagnostics C#
            if (netDiag?.Available == true)
            {
                AddKVIfNew(section, "Latence P50", netDiag.OverallLatencyMsP50, "ms", IssueLevel.Info, "C# (ping)");
                AddKVIfNew(section, "Latence P95", netDiag.OverallLatencyMsP95, "ms", IssueLevel.Info, "C# (ping)");
                AddKVIfNew(section, "Jitter P95", netDiag.OverallJitterMsP95, "ms", IssueLevel.Info, "C#");
                AddKVIfNew(section, "Perte paquets", netDiag.OverallLossPercent, "%", netDiag.OverallLossPercent > 2 ? IssueLevel.Warning : IssueLevel.Info, "C#");
                AddKVIfNew(section, "DNS P95", netDiag.DnsP95Ms, "ms", IssueLevel.Info, "C#");
                if (!string.IsNullOrEmpty(netDiag.Gateway))
                    AddKVIfNew(section, "Gateway", netDiag.Gateway, "", IssueLevel.Info, "C#");
                if (netDiag.Throughput?.Available == true && netDiag.Throughput.DownloadMbpsMedian > 0)
                    AddKVIfNew(section, "Débit descendant", netDiag.Throughput.DownloadMbpsMedian, "Mbps", IssueLevel.Info, "C#");
            }

            // Network summary from snapshot
            if (snapshot?.NetworkSummary?.Available == true)
            {
                var ns = snapshot.NetworkSummary;
                AddKVIfNew(section, "Gateway", ns.Gateway, "", IssueLevel.Info, ns.Source);
            }

            double? mbps = netDiag?.Throughput?.DownloadMbpsMedian;
            section.SectionScore = section.KeyValues.Count > 0 ? 75 : 0;
            section.SummaryLine1 = mbps.HasValue ? $"{mbps.Value:F0} Mbps — streaming HD ok, gaming variable" : "Latence et réseau ci-dessous.";
            section.SummaryLine2 = "Recommandation: Ethernet pour stabilité.";

            if (netDiag == null && (snapshot?.Metrics?.GetValueOrDefault("network")?.Count ?? 0) == 0)
            {
                section.Issues.Add(new ReportIssue { Level = IssueLevel.Warning, Message = "Tests réseau désactivés ou données indisponibles." });
                section.NotesText = "Les tests réseau externes peuvent être désactivés. Vérifier la configuration.";
            }

            section.EvidenceText = BuildEvidenceText(snapshot?.GeneratedAt, null, "DiagnosticSnapshot.Metrics[network]", "NetworkDiagnosticsResult");
            return section;
        }

        private static ReportSectionViewModel BuildStabilitySection(DiagnosticSnapshot? snapshot, CombinedScanResult combined)
        {
            var section = new ReportSectionViewModel { Id = "Stability", Title = "Stabilité système", Level = IssueLevel.Info };

            // Stability metrics from snapshot
            AddMetricsGroup(section, snapshot, "stability", "PS");
            AddMetricsGroup(section, snapshot, "whea", "PS");
            AddMetricsGroup(section, snapshot, "power", "PS");
            AddMetricsGroup(section, snapshot, "performance", "PS");

            // DiagnosticSignals: WHEA (7d/30d, dernier, gravité)
            var signals = combined.DiagnosticSignals;
            if (signals != null)
            {
                if (signals.TryGetValue("whea", out var wheaSig) && wheaSig.Available && wheaSig.Value is JsonElement wheaJe)
                {
                    var whea7 = GetIntFromJe(wheaJe, "last7dCount", "Last7dCount");
                    var whea30 = GetIntFromJe(wheaJe, "last30dCount", "Last30dCount");
                    var fatal = GetIntFromJe(wheaJe, "fatalCount", "FatalCount");
                    AddKVIfNew(section, "Erreurs WHEA (7j)", whea7, "count", (fatal ?? 0) > 0 ? IssueLevel.Critical : IssueLevel.Info, "diagnostic_signals");
                    AddKVIfNew(section, "Erreurs WHEA (30j)", whea30, "count", (fatal ?? 0) > 0 ? IssueLevel.Critical : IssueLevel.Info, "diagnostic_signals");
                    var lastWhea = GetLastEventFromJe(wheaJe, "lastEvents", "LastEvents");
                    if (lastWhea != null) AddKVIfNew(section, "Dernier WHEA", lastWhea, "", IssueLevel.Info, "diagnostic_signals");
                }
                if (signals.TryGetValue("driverStability", out var drvSig) && drvSig.Available && drvSig.Value is JsonElement drvJe)
                {
                    var bsod30 = GetIntFromJe(drvJe, "bugcheckCount30d", "BugcheckCount30d");
                    var kp41 = GetIntFromJe(drvJe, "kernelPower41Count30d", "KernelPower41Count30d");
                    var tdr30 = GetIntFromJe(drvJe, "tdrCount30d", "TdrCount30d");
                    if (bsod30.HasValue) AddKVIfNew(section, "BSOD (30j)", bsod30, "count", bsod30 > 0 ? IssueLevel.Critical : IssueLevel.Info, "diagnostic_signals");
                    if (kp41.HasValue) AddKVIfNew(section, "Kernel-Power 41 (30j)", kp41, "count", kp41 > 0 ? IssueLevel.Warning : IssueLevel.Info, "diagnostic_signals");
                    if (tdr30.HasValue) AddKVIfNew(section, "TDR (30j)", tdr30, "count", IssueLevel.Info, "diagnostic_signals");
                    var lastDrv = GetLastEventFromJe(drvJe, "lastEvents", "LastEvents");
                    if (lastDrv != null) AddKVIfNew(section, "Dernier événement stabilité", lastDrv, "", IssueLevel.Info, "diagnostic_signals");
                }
            }

            // EventLogsDetailed: comptages 7j/30j et dernier événement (WHEA, Kernel-Power, BugCheck)
            var eventLogs = combined.EventLogsDetailed;
            if (eventLogs != null && eventLogs.Count > 0)
            {
                var now = DateTime.UtcNow;
                var cutoff7 = now.AddDays(-7);
                var cutoff30 = now.AddDays(-30);
                int whea7 = 0, whea30 = 0, kp41_7 = 0, kp41_30 = 0, bug7 = 0, bug30 = 0;
                EventLogDetailedEntry? lastWheaEv = null, lastKpEv = null, lastBugEv = null;
                foreach (var e in eventLogs)
                {
                    var t = e.TimeCreated?.ToUniversalTime() ?? DateTime.MinValue;
                    if (e.ProviderName?.IndexOf("WHEA", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (t >= cutoff7) whea7++; if (t >= cutoff30) whea30++;
                        if (lastWheaEv == null || (e.TimeCreated > lastWheaEv.TimeCreated)) lastWheaEv = e;
                    }
                    if (e.ProviderName?.IndexOf("Kernel-Power", StringComparison.OrdinalIgnoreCase) >= 0 && e.EventId == 41)
                    {
                        if (t >= cutoff7) kp41_7++; if (t >= cutoff30) kp41_30++;
                        if (lastKpEv == null || (e.TimeCreated > lastKpEv.TimeCreated)) lastKpEv = e;
                    }
                    if (e.ProviderName?.IndexOf("BugCheck", StringComparison.OrdinalIgnoreCase) >= 0 || e.EventId == 1001)
                    {
                        if (t >= cutoff7) bug7++; if (t >= cutoff30) bug30++;
                        if (lastBugEv == null || (e.TimeCreated > lastBugEv.TimeCreated)) lastBugEv = e;
                    }
                }
                if (whea7 > 0 || whea30 > 0) { AddKVIfNew(section, "WHEA (EventLog 7j)", whea7, "count", IssueLevel.Info, "event_logs"); AddKVIfNew(section, "WHEA (EventLog 30j)", whea30, "count", IssueLevel.Info, "event_logs"); }
                if (kp41_7 > 0 || kp41_30 > 0) { AddKVIfNew(section, "Kernel-Power 41 (7j)", kp41_7, "count", IssueLevel.Info, "event_logs"); AddKVIfNew(section, "Kernel-Power 41 (30j)", kp41_30, "count", IssueLevel.Info, "event_logs"); }
                if (lastKpEv != null) AddKVIfNew(section, "Dernier Kernel-Power", lastKpEv.TimeCreated?.ToString("g") ?? lastKpEv.Message?.Substring(0, Math.Min(50, lastKpEv.Message?.Length ?? 0)), "", IssueLevel.Info, "event_logs");
            }

            // MinidumpsDetailed: nombre, dernier dump, BugCheck
            var minidumps = combined.MinidumpsDetailed;
            if (minidumps != null && minidumps.Count > 0)
            {
                AddKVIfNew(section, "Minidumps (BSOD)", minidumps.Count, "count", minidumps.Count > 0 ? IssueLevel.Warning : IssueLevel.Info, "minidumps");
                var last = minidumps.OrderByDescending(m => m.LastWriteTimeUtc ?? DateTime.MinValue).FirstOrDefault();
                if (last != null)
                {
                    AddKVIfNew(section, "Dernier minidump", last.LastWriteTimeUtc?.ToString("g") ?? last.FileName, "", IssueLevel.Info, "minidumps");
                    if (last.BugCheckCode.HasValue)
                        AddKVIfNew(section, "BugCheck (dernier)", $"0x{last.BugCheckCode.Value:X}", "", IssueLevel.Info, "minidumps");
                }
            }

            // Findings related to stability
            var findings = snapshot?.Findings?
                .Where(f => f.IssueType?.Contains("Stability", StringComparison.OrdinalIgnoreCase) == true
                         || f.IssueType?.Contains("BSOD", StringComparison.OrdinalIgnoreCase) == true
                         || f.IssueType?.Contains("EventLog", StringComparison.OrdinalIgnoreCase) == true
                         || f.Description?.Contains("BSOD", StringComparison.OrdinalIgnoreCase) == true)
                .ToList() ?? new List<NormalizedFinding>();

            foreach (var f in findings.Take(10))
                section.Issues.Add(new ReportIssue { Level = SeverityToLevel(f.Severity), Message = f.Description ?? "", Code = f.IssueType, Source = f.SuggestedAction });

            section.SectionScore = section.KeyValues.Count > 0 ? 70 : 50;
            if (findings.Any(f => f.Severity == "critical" || f.Severity == "high"))
            {
                section.HasCritical = true;
                section.Level = IssueLevel.Critical;
                section.SectionScore = 35;
            }

            var stabSummary = section.KeyValues.Count > 0
                ? "BSOD/WHEA/Kernel-Power: voir tableau ci-dessous."
                : "Données de stabilité non disponibles.";
            section.SummaryLine1 = stabSummary;
            section.SummaryLine2 = "Recommandation: inspecter EventId dominants dans l'Observateur d'événements.";
            section.EvidenceText = BuildEvidenceText(snapshot?.GeneratedAt, null, "DiagnosticSnapshot.Metrics[stability,whea,power]", "DiagnosticSignals", "EventLogsDetailed", "MinidumpsDetailed");
            return section;
        }

        private static int? GetIntFromJe(JsonElement je, string prop1, string prop2)
        {
            if (je.TryGetProperty(prop1, out var p)) { if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v)) return v; }
            if (je.TryGetProperty(prop2, out var p2)) { if (p2.ValueKind == JsonValueKind.Number && p2.TryGetInt32(out var v)) return v; }
            return null;
        }

        private static string? GetLastEventFromJe(JsonElement je, string arrayName1, string arrayName2)
        {
            if (!je.TryGetProperty(arrayName1, out var arr) && !je.TryGetProperty(arrayName2, out arr))
                return null;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return null;
            var first = arr[0];
            if (first.TryGetProperty("time", out var t)) return t.GetString();
            if (first.TryGetProperty("Time", out var t2)) return t2.GetString();
            return null;
        }

        private static ReportSectionViewModel BuildPerformanceSection(DiagnosticSnapshot? snapshot, CombinedScanResult combined)
        {
            var section = new ReportSectionViewModel { Id = "Performance", Title = "Performance", Level = IssueLevel.Info };

            var eval = PerformanceEvaluationEngine.Evaluate(combined.ScanPowershell, snapshot, combined.SensorsCsharp);
            var si = eval.SourceInfo;

            // ── Populate traceability properties (Requirement 3) ──
            section.PerformanceDataSource = si.DisplayLabel;
            section.PerformanceDatasetVersionDisplay = si.VersionDisplay;
            section.PerformancePublishedAt = si.PublishedAt ?? "";
            section.PerformanceUrlHost = si.UrlHost ?? "";
            section.PerformanceLastRefresh = si.LastRefresh ?? "";
            section.PerformanceIsUnavailable = eval.IsUnavailable;
            section.PerformanceUnavailableReason = eval.UnavailableReason ?? "";

            // Cache info line
            if (si.CacheHit)
            {
                var ageStr = si.CacheAgeDays.HasValue ? $"{si.CacheAgeDays.Value:F1}j" : "?";
                var stateStr = si.CacheExpired
                    ? (si.CacheInGracePeriod ? $"expiré (grace {ageStr}/30j)" : $"expiré ({ageStr})")
                    : $"frais ({ageStr})";
                section.PerformanceCacheInfo = $"Cache: {stateStr}";
            }
            else
            {
                section.PerformanceCacheInfo = si.RemoteFetchAttempted && si.RemoteFetchStatus == 200
                    ? "Cache: mis à jour (remote)"
                    : "Pas de cache";
            }

            // Fallback warning
            if (si.SourceKind == DatasetSourceKind.EmbeddedFallback)
                section.PerformanceFallbackWarning = $"Mode secours : règles internes — embedded ({PerformanceEvaluationEngine.TableVersion}). Raison : {si.FallbackReason ?? "inconnue"}";
            else if (si.SourceKind == DatasetSourceKind.Unavailable)
                section.PerformanceFallbackWarning = "";
            else if (si.CacheExpired && si.CacheInGracePeriod)
                section.PerformanceFallbackWarning = $"Dataset expiré — cache en période de grâce ({si.CacheAgeDays:F0}j/30j)";
            else
                section.PerformanceFallbackWarning = "";

            // ── Handle Unavailable state (Requirement 2A) ──
            if (eval.IsUnavailable)
            {
                section.SectionScore = -1;
                section.SummaryLine1 = "Évaluation indisponible (dataset externe requis)";
                section.SummaryLine2 = eval.UnavailableReason ?? "Le dataset externe n'a pas pu être chargé/validé.";
                section.PerformanceCategory = "Indisponible";
                section.PrimaryBottleneck = "N/A";
                section.Level = IssueLevel.Warning;

                section.EvidenceText = BuildEvidenceText(snapshot?.GeneratedAt, null,
                    si.SourceLine,
                    $"Mode: {si.Mode}",
                    $"Raison: {si.FallbackReason ?? "inconnue"}");

                AddKV(section, "Source", si.DisplayLabel, "", IssueLevel.Warning, "PerformanceEvaluationEngine");
                AddKV(section, "Mode", si.Mode.ToString(), "", IssueLevel.Info, "config");
                if (!string.IsNullOrEmpty(si.FallbackReason))
                    AddKV(section, "Raison indisponibilité", si.FallbackReason, "", IssueLevel.Warning, "");

                return section;
            }

            // ── Normal scoring display ──
            section.SectionScore = eval.Score;
            section.SummaryLine1 = $"Score: {eval.Score}/100 — {eval.Verdict.Category}";
            section.SummaryLine2 = eval.Verdict.RealisticExpectationSummary.Length > 120 ? eval.Verdict.RealisticExpectationSummary.Substring(0, 117) + "..." : eval.Verdict.RealisticExpectationSummary;
            section.PerformanceCategory = eval.Verdict.Category;
            section.PrimaryBottleneck = string.IsNullOrEmpty(eval.Bottleneck.PrimaryLimitingFactor) ? "Non déterminé" : eval.Bottleneck.PrimaryLimitingFactor;

            // Evidence text with source traceability
            section.EvidenceText = BuildEvidenceText(snapshot?.GeneratedAt, null,
                si.SourceLine,
                $"Mode: {si.Mode}",
                section.PerformanceCacheInfo);

            var p = eval.Profile;
            section.PerformanceCpuDisplay = !string.IsNullOrEmpty(p?.CpuModel) ? p.CpuModel : (p?.CpuTier ?? "Unknown");
            section.PerformanceGpuDisplay = !string.IsNullOrEmpty(p?.GpuModel) ? p.GpuModel : (p?.GpuTier ?? "Unknown");
            section.PerformanceVramDisplay = (p != null && p.GpuVramMb > 0) ? $"{p.GpuVramMb:F0} MB" : "Unknown";
            section.PerformanceRamDisplay = (p != null && p.RamGb > 0) ? $"{p.RamGb:F0} GB" : "Unknown";
            section.PerformanceStorageDisplay = !string.IsNullOrEmpty(p?.StorageKind) && p.StorageKind != "Unknown" ? p.StorageKind : "Unknown";

            // KV rows: source traceability first
            AddKV(section, "Source", si.DisplayLabel, "", IssueLevel.Info, "PerformanceEvaluationEngine");
            AddKV(section, "Version dataset", si.VersionDisplay, "", IssueLevel.Info, si.SourceKind == DatasetSourceKind.External ? si.UrlHost ?? "" : "embedded");
            if (!string.IsNullOrEmpty(si.PublishedAt))
                AddKV(section, "Dataset publié", si.PublishedAt, "", IssueLevel.Info, "");
            AddKV(section, "Mode", si.Mode.ToString(), "", IssueLevel.Info, "config");

            AddKV(section, "Score performance", $"{eval.Score}/100", "", IssueLevel.Info, "PerformanceEvaluationEngine");

            AddKV(section, "Performance Analysis", $"CPU: {eval.Profile.CpuTier} | GPU: {eval.Profile.GpuTier} | RAM: {eval.Profile.RamTier} | Storage: {eval.Profile.StorageTier}. System: {eval.Verdict.Category}.", "", IssueLevel.Info, "");
            AddKV(section, "Realistic summary", eval.Verdict.RealisticExpectationSummary, "", IssueLevel.Info, "");

            foreach (var s in eval.ScenarioScores)
                AddKV(section, $"Capability Matrix — {s.Name}", $"{s.Score}/100 — {s.Classification}", "", IssueLevel.Info, "");

            AddKV(section, "Primary limiting factor", eval.Bottleneck.PrimaryLimitingFactor, "", IssueLevel.Info, "");
            for (int i = 0; i < eval.Bottleneck.UpgradePriorityRank.Count && i < 3; i++)
            {
                var u = eval.Bottleneck.UpgradePriorityRank[i];
                AddKV(section, $"Upgrade Impact ({u.Rank})", $"{u.Component}: {u.Reason}", "", IssueLevel.Info, "");
            }

            foreach (var s in eval.ScenarioScores)
            {
                section.ScenarioScores.Add(new ViewModels.ScenarioScoreViewModel 
                { 
                    Name = s.Name, 
                    Score = s.PreciseScore, // Use precise decimal score
                    Classification = s.Classification,
                    Explanation = s.Explanation
                });
            }
            #region agent log
            AgentDebugLog(
                runId: "initial",
                hypothesisId: "H5",
                location: "FullReportBuilder.BuildPerformanceSection:1006",
                message: "ScenarioScores prepared for FullReportView",
                data: section.ScenarioScores.Select(s => new { s.Name, s.Score, s.Classification }).ToList());
            #endregion

            // ── Populate Task Capability Scores (Table A) ──
            foreach (var s in eval.ScenarioScores)
            {
                section.TaskCapabilityScores.Add(TaskCapabilityRow.Create(
                    scenarioId: s.ScenarioId,
                    taskName: s.Name,
                    score: s.Score,
                    limitingFactor: eval.Bottleneck?.PrimaryLimitingFactor
                ));
            }

            // ── Populate Market Position Scores (Table B) ──
            // Always populate this table, using profile data if available, snapshot fallback otherwise
            PopulateMarketPositionScores(section, p, snapshot);
            #region agent log
            AgentDebugLog(
                runId: "initial",
                hypothesisId: "H5",
                location: "FullReportBuilder.BuildPerformanceSection:1021",
                message: "MarketPositionScores count after population",
                data: section.MarketPositionScores.Select(m => new { m.Component, m.DetectedModel, m.BenchmarkScore, m.PercentileDisplay, m.RankDisplay, m.Source, m.ConfidenceDisplay }).ToList());
            #endregion

            return section;
        }

        /// <summary>
        /// Populates the Market Position table with component percentiles and tiers.
        /// Uses external benchmark data when available for precise percentiles and ranks.
        /// Falls back to tier-based estimates if benchmark data is unavailable.
        /// </summary>
        private static void PopulateMarketPositionScores(ReportSectionViewModel section, HardwareProfile? p, DiagnosticSnapshot? snapshot)
        {
            // Fetch benchmark dataset
            var benchmarkProvider = new GitHubBenchmarkDataProvider();
            var benchmarkResult = benchmarkProvider.GetDatasetAsync().GetAwaiter().GetResult();
            var benchmarkDs = benchmarkResult.Dataset;
            
            // FIXED: Consider benchmark data usable even if from embedded fallback (Error set but Dataset not null)
            bool hasUsableBenchmarkData = benchmarkDs != null &&
                ((benchmarkDs.CpuEntries?.Count ?? 0) > 0 || (benchmarkDs.GpuEntries?.Count ?? 0) > 0);
            
            // Build source display string from actual dataset source name (even for embedded)
            string sourceDisplay;
            if (hasUsableBenchmarkData)
            {
                var sourceName = !string.IsNullOrWhiteSpace(benchmarkDs!.SourceName) ? benchmarkDs.SourceName : "PCDiagnosticPRO";
                var version = !string.IsNullOrWhiteSpace(benchmarkDs.DatasetVersion) ? $" v{benchmarkDs.DatasetVersion}" : "";
                var date = !string.IsNullOrWhiteSpace(benchmarkDs.PublishedAt) && benchmarkDs.PublishedAt.Length >= 10
                    ? $" ({benchmarkDs.PublishedAt.Substring(0, 10)})"
                    : "";
                sourceDisplay = $"{sourceName}{version}{date}";
            }
            else
            {
                sourceDisplay = "Estimation interne (tier-based)";
            }
            
            // Try to get data from profile first, then from snapshot
            int cpuCores = p?.CpuCores ?? 0;
            int cpuThreads = p?.CpuThreads ?? 0;
            string cpuModel = p?.CpuModel ?? "";
            string cpuTier = p?.CpuTier ?? "";

            double gpuVramMb = p?.GpuVramMb ?? 0;
            string gpuModel = p?.GpuModel ?? "";
            string gpuTier = p?.GpuTier ?? "";

            double ramGb = p?.RamGb ?? 0;
            string storageKind = p?.StorageKind ?? "";

            // Try to extract from snapshot if profile data is incomplete
            if (cpuCores == 0 && snapshot?.Machine != null)
            {
                var cpuName = snapshot.Machine.CpuName ?? "";
                cpuModel = cpuName;
                if (cpuName.Contains("i9") || cpuName.Contains("Ryzen 9"))
                    cpuCores = 12;
                else if (cpuName.Contains("i7") || cpuName.Contains("Ryzen 7"))
                    cpuCores = 8;
                else if (cpuName.Contains("i5") || cpuName.Contains("Ryzen 5"))
                    cpuCores = 6;
                else
                    cpuCores = 4;
                cpuThreads = cpuCores * 2;
            }

            if (ramGb == 0 && snapshot?.Machine?.TotalRamGB != null && snapshot.Machine.TotalRamGB.Value > 0)
            {
                ramGb = snapshot.Machine.TotalRamGB.Value;
            }

            // ══ CPU Position ══
            MarketPositionScore cpuScore;
            if (hasUsableBenchmarkData && !string.IsNullOrEmpty(cpuModel))
            {
                var cpuMatch = BenchmarkMatcher.MatchCpu(cpuModel, benchmarkDs!.CpuEntries);
                if (cpuMatch.Entry != null)
                {
                    cpuScore = MarketPositionScore.CreateFromBenchmark(
                        component: "CPU",
                        detectedModel: cpuModel,
                        benchmarkScore: cpuMatch.Entry.RawScore,
                        percentile: cpuMatch.Entry.Percentile,
                        rank: cpuMatch.Entry.Rank,
                        totalInMarket: benchmarkDs.TotalCpusInMarket,
                        source: sourceDisplay,
                        confidence: cpuMatch.Confidence
                    );
                }
                else
                {
                    // No match - fall back to tier-based with low confidence
                    int cpuTierOrder = GetTierOrder(cpuTier);
                    if (cpuTierOrder == 0 && cpuCores > 0)
                        cpuTierOrder = cpuCores >= 12 ? 4 : (cpuCores >= 8 ? 3 : (cpuCores >= 6 ? 2 : 1));
                    
                    var cpuRawValue = !string.IsNullOrEmpty(cpuModel)
                        ? (cpuCores > 0 ? $"{cpuModel} ({cpuCores}c/{cpuThreads}t)" : cpuModel)
                        : (cpuCores > 0 ? $"{cpuCores} cœurs / {cpuThreads} threads" : "Non disponible");
                    
                    cpuScore = MarketPositionScore.CreateWithValue("CPU", Math.Max(cpuTierOrder, 1), cpuRawValue, cpuCores, 24);
                    cpuScore.DetectedModel = cpuModel;
                    cpuScore.Source = sourceDisplay + " (non trouvé)";
                    cpuScore.Confidence = MatchConfidence.Low;
                    cpuScore.TotalInMarket = benchmarkDs.TotalCpusInMarket;
                    cpuScore.Rank = MarketPositionScore.CalculateRankFromPercentile(cpuScore.Percentile, benchmarkDs.TotalCpusInMarket);
                    cpuScore.RankDisplay = MarketPositionScore.GetRankDisplay(cpuScore.Rank, benchmarkDs.TotalCpusInMarket);
                }
            }
            else
            {
                // No benchmark data - pure tier-based
                int cpuTierOrder = GetTierOrder(cpuTier);
                if (cpuTierOrder == 0 && cpuCores > 0)
                    cpuTierOrder = cpuCores >= 12 ? 4 : (cpuCores >= 8 ? 3 : (cpuCores >= 6 ? 2 : 1));
                
                var cpuRawValue = !string.IsNullOrEmpty(cpuModel)
                    ? (cpuCores > 0 ? $"{cpuModel} ({cpuCores}c/{cpuThreads}t)" : cpuModel)
                    : (cpuCores > 0 ? $"{cpuCores} cœurs / {cpuThreads} threads" : "Non disponible");
                
                cpuScore = MarketPositionScore.CreateWithValue("CPU", Math.Max(cpuTierOrder, 1), cpuRawValue, cpuCores, 24);
                cpuScore.DetectedModel = cpuModel;
                cpuScore.Source = "Estimation interne (tier-based)";
            }
            section.MarketPositionScores.Add(cpuScore);

            // ══ GPU Position ══
            MarketPositionScore gpuScore;
            if (hasUsableBenchmarkData && !string.IsNullOrEmpty(gpuModel))
            {
                var gpuMatch = BenchmarkMatcher.MatchGpu(gpuModel, benchmarkDs!.GpuEntries);
                if (gpuMatch.Entry != null)
                {
                    gpuScore = MarketPositionScore.CreateFromBenchmark(
                        component: "GPU",
                        detectedModel: gpuModel,
                        benchmarkScore: gpuMatch.Entry.RawScore,
                        percentile: gpuMatch.Entry.Percentile,
                        rank: gpuMatch.Entry.Rank,
                        totalInMarket: benchmarkDs.TotalGpusInMarket,
                        source: sourceDisplay,
                        confidence: gpuMatch.Confidence
                    );
                }
                else
                {
                    // No match - fall back to tier-based
                    int gpuTierOrder = GetTierOrder(gpuTier);
                    if (gpuTierOrder == 0 && gpuVramMb > 0)
                        gpuTierOrder = gpuVramMb >= 16384 ? 4 : (gpuVramMb >= 8192 ? 3 : (gpuVramMb >= 4096 ? 2 : 1));
                    
                    var gpuRawValue = !string.IsNullOrEmpty(gpuModel)
                        ? (gpuVramMb > 0 ? $"{gpuModel} ({gpuVramMb:F0} Mo)" : gpuModel)
                        : (gpuVramMb > 0 ? $"{gpuVramMb:F0} Mo VRAM" : "Non disponible");
                    
                    gpuScore = MarketPositionScore.CreateWithValue("GPU", Math.Max(gpuTierOrder, 1), gpuRawValue, gpuVramMb, 24576);
                    gpuScore.DetectedModel = gpuModel;
                    gpuScore.Source = sourceDisplay + " (non trouvé)";
                    gpuScore.Confidence = MatchConfidence.Low;
                    gpuScore.TotalInMarket = benchmarkDs.TotalGpusInMarket;
                    gpuScore.Rank = MarketPositionScore.CalculateRankFromPercentile(gpuScore.Percentile, benchmarkDs.TotalGpusInMarket);
                    gpuScore.RankDisplay = MarketPositionScore.GetRankDisplay(gpuScore.Rank, benchmarkDs.TotalGpusInMarket);
                }
            }
            else
            {
                int gpuTierOrder = GetTierOrder(gpuTier);
                if (gpuTierOrder == 0 && gpuVramMb > 0)
                    gpuTierOrder = gpuVramMb >= 16384 ? 4 : (gpuVramMb >= 8192 ? 3 : (gpuVramMb >= 4096 ? 2 : 1));
                
                var gpuRawValue = !string.IsNullOrEmpty(gpuModel)
                    ? (gpuVramMb > 0 ? $"{gpuModel} ({gpuVramMb:F0} Mo)" : gpuModel)
                    : (gpuVramMb > 0 ? $"{gpuVramMb:F0} Mo VRAM" : "Non disponible");
                
                gpuScore = MarketPositionScore.CreateWithValue("GPU", Math.Max(gpuTierOrder, 1), gpuRawValue, gpuVramMb, 24576);
                gpuScore.DetectedModel = gpuModel;
                gpuScore.Source = "Estimation interne (tier-based)";
            }
            section.MarketPositionScores.Add(gpuScore);

            // ══ RAM Position ══
            MarketPositionScore ramScore;
            if (hasUsableBenchmarkData && ramGb > 0)
            {
                var (ramPercentile, ramConfidence) = BenchmarkMatcher.CalculateRamPercentile(ramGb, benchmarkDs!.RamBaseline);
                int ramTierOrder = ramGb >= 64 ? 5 : (ramGb >= 32 ? 4 : (ramGb >= 16 ? 3 : (ramGb >= 8 ? 2 : 1)));
                int ramRank = MarketPositionScore.CalculateRankFromPercentile(ramPercentile, 10000);
                
                ramScore = MarketPositionScore.CreateFromBenchmark(
                    component: "RAM",
                    detectedModel: $"{ramGb:F0} Go",
                    benchmarkScore: ramGb,
                    percentile: ramPercentile,
                    rank: ramRank,
                    totalInMarket: 10000,
                    source: sourceDisplay,
                    confidence: ramConfidence
                );
            }
            else
            {
                int ramTierOrder = GetRamTierOrder(ramGb);
                var ramRawValue = ramGb > 0 ? $"{ramGb:F0} Go" : "Non disponible";
                ramScore = MarketPositionScore.CreateWithValue("RAM", Math.Max(ramTierOrder, 1), ramRawValue, ramGb, 128);
                ramScore.DetectedModel = ramRawValue;
                ramScore.Source = "Estimation interne (tier-based)";
            }
            section.MarketPositionScores.Add(ramScore);

            // ══ Storage Position ══
            MarketPositionScore storageScore;
            if (hasUsableBenchmarkData && !string.IsNullOrEmpty(storageKind))
            {
                var (storagePercentile, storageConfidence) = BenchmarkMatcher.CalculateStoragePercentile(storageKind, benchmarkDs!.StorageBaseline);
                int storageRank = MarketPositionScore.CalculateRankFromPercentile(storagePercentile, 5000);
                
                storageScore = MarketPositionScore.CreateFromBenchmark(
                    component: "Stockage",
                    detectedModel: storageKind,
                    benchmarkScore: storagePercentile,
                    percentile: storagePercentile,
                    rank: storageRank,
                    totalInMarket: 5000,
                    source: sourceDisplay,
                    confidence: storageConfidence
                );
            }
            else
            {
                int storageTierOrder = GetStorageTierOrder(storageKind);
                var storageRawValue = !string.IsNullOrEmpty(storageKind) && storageKind != "Unknown" 
                    ? storageKind 
                    : "Non disponible";
                storageScore = MarketPositionScore.Create("Stockage", Math.Max(storageTierOrder, 1), storageRawValue);
                storageScore.DetectedModel = storageRawValue;
                storageScore.Source = "Estimation interne (tier-based)";
            }
            section.MarketPositionScores.Add(storageScore);

            // ══ Global Position ══
            double avgPercentile = section.MarketPositionScores.Count > 0
                ? section.MarketPositionScores.Average(m => m.Percentile)
                : 50.0;
            
            // Weighted average for overall score
            double weightedPercentile = (cpuScore.Percentile * 0.25 + gpuScore.Percentile * 0.35 + 
                                         ramScore.Percentile * 0.25 + storageScore.Percentile * 0.15);
            
            int globalRank = MarketPositionScore.CalculateRankFromPercentile(weightedPercentile, 10000);
            var globalConfidence = (cpuScore.Confidence == MatchConfidence.High && gpuScore.Confidence == MatchConfidence.High)
                ? MatchConfidence.High
                : (cpuScore.Confidence == MatchConfidence.Low && gpuScore.Confidence == MatchConfidence.Low)
                    ? MatchConfidence.Low
                    : MatchConfidence.Medium;
            
            var globalScore = MarketPositionScore.CreateFromBenchmark(
                component: "Global",
                detectedModel: $"Score pondéré",
                benchmarkScore: weightedPercentile,
                percentile: weightedPercentile,
                rank: globalRank,
                totalInMarket: 10000,
                source: sourceDisplay,
                confidence: globalConfidence
            );
            globalScore.RawValue = $"Score global: {weightedPercentile:F1}%";
            section.MarketPositionScores.Add(globalScore);
        }

        /// <summary>Convert tier string to tier order (0-5). Returns 0 for unknown/empty to allow fallback logic.</summary>
        private static int GetTierOrder(string? tier)
        {
            if (string.IsNullOrEmpty(tier)) return 0;
            return tier.ToLowerInvariant() switch
            {
                "workstation" => 5,
                "high-end" => 4,
                "upper-mid" => 3,
                "mid-range" => 2,
                "entry-level" => 1,
                _ => 0 // Unknown - allow fallback logic
            };
        }

        /// <summary>Get RAM tier order based on GB.</summary>
        private static int GetRamTierOrder(double ramGb)
        {
            return ramGb switch
            {
                >= 64 => 5,  // Workstation
                >= 32 => 4,  // High-end
                >= 16 => 3,  // Upper-mid
                >= 8 => 2,   // Mid-range
                _ => 1       // Entry
            };
        }

        /// <summary>Get storage tier order based on type.</summary>
        private static int GetStorageTierOrder(string? storageKind)
        {
            if (string.IsNullOrEmpty(storageKind)) return 1;
            return storageKind.ToUpperInvariant() switch
            {
                "NVME" => 4,
                "SATA_SSD" => 3,
                "SSD" => 3,
                "HDD" => 1,
                _ => 2
            };
        }

        private static ReportSectionViewModel BuildSecuritySection(DiagnosticSnapshot? snapshot, SecurityInfoCollector.SecurityInfoResult? secInfo)
        {
            var section = new ReportSectionViewModel { Id = "Security", Title = "Sécurité", Level = IssueLevel.Info };

            // PS metrics
            AddMetricsGroup(section, snapshot, "security", "PS");

            // C# security info (BitLocker, RDP, SMBv1)
            if (secInfo?.Available == true)
            {
                AddKVIfNew(section, "BitLocker", secInfo.BitLockerStatus, "", IssueLevel.Info, secInfo.BitLockerSource);
                if (secInfo.IsWindowsHome && secInfo.DeviceEncryptionEnabled.HasValue)
                    AddKV(section, "Chiffrement appareil", secInfo.DeviceEncryptionEnabled.Value ? "Activé" : "Désactivé", "", IssueLevel.Info, "C#");
                AddKVIfNew(section, "RDP", secInfo.RdpStatus, "", secInfo.RdpEnabled == true ? IssueLevel.Warning : IssueLevel.Info, secInfo.RdpSource);
                AddKVIfNew(section, "SMBv1", secInfo.SmbV1Status, "", secInfo.SmbV1Enabled == true ? IssueLevel.Warning : IssueLevel.Info, secInfo.SmbV1Source);

                if (secInfo.RdpEnabled == true)
                    section.Issues.Add(new ReportIssue { Level = IssueLevel.Warning, Message = "RDP activé — risque de sécurité si non nécessaire.", Code = "RDP_ENABLED" });
                if (secInfo.SmbV1Enabled == true)
                    section.Issues.Add(new ReportIssue { Level = IssueLevel.Critical, Message = "SMBv1 activé — vulnérabilité critique (WannaCry/EternalBlue).", Code = "SMBV1_ENABLED" });
            }

            section.SectionScore = section.KeyValues.Count > 0 ? 80 : 0;
            if (secInfo?.SmbV1Enabled == true) { section.HasCritical = true; section.Level = IssueLevel.Critical; section.SectionScore = 30; }
            section.SummaryLine1 = "Antivirus, pare-feu, UAC, BitLocker ci-dessous.";
            section.SummaryLine2 = "Recommandation: garder Defender et pare-feu activés.";
            section.EvidenceText = BuildEvidenceText(snapshot?.GeneratedAt, null, "DiagnosticSnapshot.Metrics[security]", "SecurityInfoCollector");
            return section;
        }

        private static ReportSectionViewModel BuildUpdatesSection(DiagnosticSnapshot? snapshot, WindowsUpdateResult? updatesCsharp)
        {
            var section = new ReportSectionViewModel { Id = "Updates", Title = "Mises à jour", Level = IssueLevel.Info };

            // PS metrics
            AddMetricsGroup(section, snapshot, "updates", "PS");

            // PS summary
            if (snapshot?.PsSummary?.Updates != null)
            {
                var u = snapshot.PsSummary.Updates;
                if (u.PendingCount.HasValue)
                    AddKVIfNew(section, "Pending (PS)", u.PendingCount.Value, "count", IssueLevel.Info, "PS");
                if (u.RebootRequired.HasValue)
                    AddKVIfNew(section, "Reboot requis", u.RebootRequired.Value ? "Oui" : "Non", "", u.RebootRequired.Value ? IssueLevel.Warning : IssueLevel.Info, "PS");
                if (!string.IsNullOrEmpty(u.LastUpdate))
                    AddKVIfNew(section, "Dernière MàJ", u.LastUpdate, "", IssueLevel.Info, "PS");
            }

            // C# updates (priority for real-time pending count)
            if (updatesCsharp != null)
            {
                AddKVIfNew(section, "Pending (C#)", updatesCsharp.PendingCount, "count", IssueLevel.Info, "WindowsUpdateAgent");
                if (updatesCsharp.RebootRequired == true)
                    AddKVIfNew(section, "Reboot requis (C#)", "Oui", "", IssueLevel.Warning, "C#");
                if (!updatesCsharp.Available && !string.IsNullOrEmpty(updatesCsharp.Error))
                {
                    section.HasCritical = true;
                    section.Level = IssueLevel.Critical;
                    section.Issues.Add(new ReportIssue { Level = IssueLevel.Critical, Message = $"Erreur service updates: {updatesCsharp.Error}", Code = "UPDATE_ERROR" });
                }
                // List pending updates with KB details
                if (updatesCsharp.Updates?.Count > 0)
                {
                    foreach (var upd in updatesCsharp.Updates.Take(20))
                    {
                        var kb = !string.IsNullOrEmpty(upd.KB) ? $" ({upd.KB})" : "";
                        section.Issues.Add(new ReportIssue { Level = IssueLevel.Info, Message = $"{upd.Title}{kb}", Code = upd.Category });
                    }
                }
            }

            section.SectionScore = section.HasCritical ? 30 : (section.KeyValues.Count > 0 ? 80 : 50);
            var pendingCount = updatesCsharp?.PendingCount ?? 0;
            section.SummaryLine1 = pendingCount > 0 ? $"{pendingCount} mise(s) à jour en attente." : "Mises à jour Windows — aucune en attente ou vérification récente.";
            section.SummaryLine2 = section.HasCritical ? "Recommandation: démarrer le service wuauserv et installer les mises à jour." : "Système à jour ou vérification nécessaire.";
            section.EvidenceText = BuildEvidenceText(snapshot?.GeneratedAt, null, "DiagnosticSnapshot.Metrics[updates]", "WindowsUpdateResult");
            return section;
        }

        private static ReportSectionViewModel BuildDevicesSection(DiagnosticSnapshot? snapshot, DriverInventoryResult? driverInv)
        {
            var section = new ReportSectionViewModel { Id = "Devices", Title = "Périphériques et pilotes", Level = IssueLevel.Info };

            // PS metrics
            AddMetricsGroup(section, snapshot, "devices", "PS");
            AddMetricsGroup(section, snapshot, "drivers", "PS");

            // PS summary
            if (snapshot?.PsSummary?.Devices != null)
            {
                var d = snapshot.PsSummary.Devices;
                if (d.ProblemDeviceCount.HasValue)
                    AddKVIfNew(section, "Périphériques en erreur (PS)", d.ProblemDeviceCount.Value, "count", d.ProblemDeviceCount > 0 ? IssueLevel.Warning : IssueLevel.Info, "PS");
                if (d.TopProblemDevices?.Count > 0)
                    foreach (var pd in d.TopProblemDevices.Take(5))
                        section.Issues.Add(new ReportIssue { Level = IssueLevel.Warning, Message = pd, Code = "PROBLEM_DEVICE", Source = "PS" });
            }

            // Startup
            if (snapshot?.PsSummary?.Startup != null)
            {
                AddKVIfNew(section, "Programmes démarrage", snapshot.PsSummary.Startup.Count, "count", IssueLevel.Info, "PS");
            }

            // C# driver inventory
            if (driverInv?.Available == true)
            {
                AddKVIfNew(section, "Pilotes total (C#)", driverInv.TotalCount, "count", IssueLevel.Info, "DriverInventoryCollector");
                AddKVIfNew(section, "Pilotes signés", driverInv.SignedCount, "count", IssueLevel.Info, "C#");
                AddKVIfNew(section, "Pilotes non signés", driverInv.UnsignedCount, "count", driverInv.UnsignedCount > 0 ? IssueLevel.Warning : IssueLevel.Info, "C#");
                AddKVIfNew(section, "Pilotes en erreur (C#)", driverInv.ProblemCount, "count", driverInv.ProblemCount > 0 ? IssueLevel.Warning : IssueLevel.Info, "C#");

                if (driverInv.ProblemCount > 0)
                {
                    var problemDrivers = driverInv.Drivers?.Where(d => d.Status != null && d.Status != "OK").Take(10) ?? Enumerable.Empty<DriverInventoryItem>();
                    foreach (var pd in problemDrivers)
                        section.Issues.Add(new ReportIssue { Level = IssueLevel.Warning, Message = $"{pd.DeviceName} [{pd.Status}] — {pd.DeviceClass}", Code = pd.PnpDeviceId, Source = "C#" });
                }
            }

            section.SectionScore = section.KeyValues.Count > 0 ? 70 : 0;
            if ((driverInv?.ProblemCount ?? 0) > 3) { section.HasCritical = true; section.Level = IssueLevel.Warning; section.SectionScore = 50; }
            section.SummaryLine1 = driverInv?.Available == true ? $"{driverInv.TotalCount} pilotes, {driverInv.ProblemCount} en erreur." : "Inventaire pilotes ci-dessous.";
            section.SummaryLine2 = "Recommandation: Gestionnaire de périphériques pour mettre à jour les pilotes.";
            section.EvidenceText = BuildEvidenceText(snapshot?.GeneratedAt, null, "DiagnosticSnapshot.Metrics[devices,drivers]", "DriverInventoryResult");
            return section;
        }

        private static ReportSectionViewModel BuildCollectorErrorsSection(
            List<ErrorExtract> errors,
            List<string> missingData,
            CollectorDiagnostics? collectorDiagnostics,
            DiagnosticSnapshot? snapshot)
        {
            var section = new ReportSectionViewModel
            {
                Id = "CollectorErrors",
                Title = "Erreurs de collecte",
                Level = errors.Count > 0 || missingData.Count > 0 ? IssueLevel.Warning : IssueLevel.Info,
                HasCritical = errors.Count > 0,
                SectionScore = errors.Count == 0 && missingData.Count == 0 ? 100 : Math.Max(0, 100 - errors.Count * 10 - missingData.Count * 2)
            };

            // Errors
            foreach (var e in errors.Take(50))
                section.Issues.Add(new ReportIssue { Level = IssueLevel.Critical, Message = $"[{e.Code}] {e.Message}", Code = e.Code, Source = e.Section });

            // Missing data
            foreach (var m in missingData.Take(30))
                section.Issues.Add(new ReportIssue { Level = IssueLevel.Warning, Message = m, Code = "Missing" });

            // WMI errors
            if (collectorDiagnostics?.WmiErrors?.Count > 0)
            {
                foreach (var w in collectorDiagnostics.WmiErrors.Take(20))
                    section.Issues.Add(new ReportIssue { Level = IssueLevel.Warning, Message = $"WMI: {w.Namespace} — {w.Message}", Code = w.HResult.ToString(), Source = w.Method });
                section.NotesText = $"{collectorDiagnostics.WmiErrors.Count} erreur(s) WMI enregistrée(s).";
            }

            // Sentinel detection
            var sentinels = DetectSentinels(snapshot);
            foreach (var s in sentinels)
                section.Issues.Add(new ReportIssue { Level = IssueLevel.Warning, Message = s, Code = "SENTINEL" });

            // Collection quality
            if (snapshot?.CollectionQuality != null)
            {
                var cq = snapshot.CollectionQuality;
                AddKV(section, "Métriques totales", cq.TotalMetrics, "count");
                AddKV(section, "Métriques disponibles", cq.AvailableMetrics, "count");
                AddKV(section, "Métriques indisponibles", cq.UnavailableMetrics, "count");
                AddKV(section, "Couverture collecte", cq.CoveragePercent, "%");
                if (cq.Errors?.Count > 0)
                    foreach (var ce in cq.Errors.Take(10))
                        section.Issues.Add(new ReportIssue { Level = SeverityToLevel(ce.Severity), Message = $"[{ce.Code}] {ce.Message}", Code = ce.Code, Source = ce.Source });
            }

            // Sensor status
            if (snapshot?.SensorStatus != null)
            {
                if (snapshot.SensorStatus.BlockedByDefender)
                    section.Issues.Add(new ReportIssue { Level = IssueLevel.Warning, Message = $"Capteurs bloqués par Defender: {snapshot.SensorStatus.BlockReason}", Code = "SENSOR_BLOCKED" });
                if (snapshot.SensorStatus.Exceptions?.Count > 0)
                    foreach (var ex in snapshot.SensorStatus.Exceptions.Take(5))
                        section.Issues.Add(new ReportIssue { Level = IssueLevel.Warning, Message = $"Exception capteur: {ex}", Code = "SENSOR_EX" });
            }

            // PS Coverage
            if (snapshot?.PsCoverage != null)
            {
                var pc = snapshot.PsCoverage;
                AddKV(section, "Sections PS attendues", pc.TotalExpectedSections, "count");
                AddKV(section, "Sections PS mappées", pc.MappedSections, "count");
                AddKV(section, "Sections PS manquantes", pc.MissingSections, "count");
                AddKV(section, "Couverture PS", pc.CoveragePercent, "%");
                if (pc.MissingSectionNames?.Count > 0)
                    section.Issues.Add(new ReportIssue { Level = IssueLevel.Warning, Message = $"Sections PS manquantes: {string.Join(", ", pc.MissingSectionNames)}", Code = "PS_MISSING" });
                if (pc.UnmappedPsSections?.Count > 0)
                    section.Issues.Add(new ReportIssue { Level = IssueLevel.Info, Message = $"Sections PS non mappées: {string.Join(", ", pc.UnmappedPsSections)}", Code = "PS_UNMAPPED" });
            }

            section.SummaryLine1 = errors.Count > 0 ? $"{errors.Count} erreur(s) de collecte." : "Aucune erreur de collecte.";
            section.SummaryLine2 = missingData.Count > 0 ? $"{missingData.Count} donnée(s) manquante(s)." : "Toutes les données collectées.";
            section.EvidenceText = BuildEvidenceText(snapshot?.GeneratedAt, null, "Errors[]", "MissingData[]", "CollectorDiagnostics", "CollectionQuality");
            return section;
        }

        private static ReportSectionViewModel BuildTechnicalLogSection(DiagnosticSnapshot? snapshot, CombinedScanResult combined)
        {
            var section = new ReportSectionViewModel { Id = "TechnicalLog", Title = "Journal technique", Level = IssueLevel.Info, SectionScore = 100 };

            AddKV(section, "Schema version", snapshot?.SchemaVersion, "");
            AddKV(section, "Généré à", snapshot?.GeneratedAt, "");
            AddKV(section, "RunId", combined.Metadata?.RunId, "");
            AddKV(section, "Timestamp", combined.Metadata?.Timestamp, "");
            AddKV(section, "Durée scan", combined.Metadata?.DurationSeconds > 0 ? $"{combined.Metadata.DurationSeconds:F1}" : null, "s");
            AddKV(section, "JSON combiné", combined.Paths?.CombinedJson, "");
            AddKV(section, "Rapport TXT", combined.Paths?.UnifiedTxt, "");
            AddKV(section, "JSON PS brut", combined.Paths?.JsonOutput, "");
            AddKV(section, "TXT brut", combined.Paths?.TxtOutput, "");
            AddKV(section, "Sections PS", combined.Sections?.Count > 0 ? string.Join(", ", combined.Sections) : null, "");

            // Diagnostic signals summary
            if (combined.DiagnosticSignals?.Count > 0)
            {
                AddKV(section, "Signaux diagnostiques", combined.DiagnosticSignals.Count, "count");
                foreach (var sig in combined.DiagnosticSignals.Take(10))
                    section.Issues.Add(new ReportIssue { Level = IssueLevel.Info, Message = $"Signal: {sig.Key} — {(sig.Value?.Available == true ? sig.Value.Quality ?? "ok" : sig.Value?.Reason ?? "indisponible")}", Code = sig.Key });
            }

            // Findings summary
            if (combined.Findings?.Count > 0)
            {
                AddKV(section, "Findings totaux", combined.Findings.Count, "count");
                foreach (var f in combined.Findings.Take(10))
                    section.Issues.Add(new ReportIssue { Level = SeverityToLevel(f.Severity), Message = $"[{f.Type}] {f.Message}", Code = f.Type, Source = f.Source });
            }

            section.SummaryLine1 = "Horodatage, chemins des rapports, et signaux diagnostiques.";
            section.SummaryLine2 = "Données brutes du pipeline de scan.";
            section.EvidenceText = BuildEvidenceText(snapshot?.GeneratedAt, combined.Metadata?.RunId, "CombinedScanResult.Paths", "DiagnosticSignals", "Findings");
            return section;
        }

        // ===== SENTINELS =====

        private static List<string> DetectSentinels(DiagnosticSnapshot? snapshot)
        {
            var sentinels = new List<string>();
            if (snapshot?.Metrics == null) return sentinels;

            foreach (var group in snapshot.Metrics)
            {
                foreach (var kv in group.Value)
                {
                    var m = kv.Value;
                    if (!m.Available) continue;
                    if (m.Value is double d)
                    {
                        if (kv.Key.Contains("temp", StringComparison.OrdinalIgnoreCase) && d == 0)
                            sentinels.Add($"Sentinelle détectée: {group.Key}.{kv.Key} = 0 (capteur probablement absent)");
                        if (kv.Key.Contains("queue", StringComparison.OrdinalIgnoreCase) && d < 0)
                            sentinels.Add($"Sentinelle détectée: {group.Key}.{kv.Key} = {d} (valeur aberrante)");
                        if (kv.Key.Contains("temp", StringComparison.OrdinalIgnoreCase) && (d > 200 || d < -20))
                            sentinels.Add($"Sentinelle détectée: {group.Key}.{kv.Key} = {d}°C (hors plage raisonnable)");
                    }
                    if (m.Value is int i && i < 0 && kv.Key.Contains("count", StringComparison.OrdinalIgnoreCase))
                        sentinels.Add($"Sentinelle détectée: {group.Key}.{kv.Key} = {i} (valeur négative aberrante)");
                }
            }
            return sentinels;
        }

        /// <summary>
        /// Adds detailed CPU throttle information from DiagnosticSignals to the CPU section.
        /// Extracts: throttle suspected, event counts (7d/30d), thermal vs power, frequency %, etc.
        /// </summary>
        private static void AddCpuThrottleDetails(ReportSectionViewModel section, Dictionary<string, SignalResult>? signals)
        {
            if (signals == null) return;

            SignalResult? sig = null;
            foreach (var key in new[] { "cpuThrottle", "cpu_throttle", "CpuThrottle" })
            {
                if (signals.TryGetValue(key, out var s)) { sig = s; break; }
            }
            if (sig == null) return;

            if (!sig.Available)
            {
                AddKVIfNew(section, "Throttling détecté", "Non disponible", "", IssueLevel.Info, "diagnostic_signals");
                AddKVIfNew(section, "Throttling", sig.Reason ?? Na, "", IssueLevel.Info, "diagnostic_signals");
                return;
            }

            try
            {
                // Handle both JsonElement (from JSON deserialization) and CpuThrottleResult (in-memory)
                if (sig.Value is JsonElement je && je.ValueKind == JsonValueKind.Object)
                {
                    // Throttle suspected
                    var suspected = false;
                    if (je.TryGetProperty("ThrottleSuspected", out var ts) || je.TryGetProperty("throttleSuspected", out ts))
                        suspected = ts.ValueKind == JsonValueKind.True;

                    var level = suspected ? IssueLevel.Warning : IssueLevel.Info;
                    AddKVIfNew(section, "Throttling détecté", suspected ? "Oui" : "Non", "", level, "diagnostic_signals");
                    AddKVIfNew(section, "Throttling", suspected ? "Oui (suspecté)" : "Non détecté", "", level, "diagnostic_signals");

                    // Type : Thermique / Power limit / Current limit / Indéterminé
                    var thermal = GetIntFromJe(je, "ThermalThrottleCount", "thermalThrottleCount");
                    var powerLimit = GetIntFromJe(je, "PowerLimitCount", "powerLimitCount");
                    var typeStr = "Indéterminé";
                    if (thermal.HasValue && thermal > 0 && powerLimit.HasValue && powerLimit > 0)
                        typeStr = "Thermique, Power limit";
                    else if (thermal.HasValue && thermal > 0)
                        typeStr = "Thermique";
                    else if (powerLimit.HasValue && powerLimit > 0)
                        typeStr = "Power limit";
                    AddKVIfNew(section, "Type", typeStr, "", IssueLevel.Info, "diagnostic_signals");

                    // Preuves : résumé (événements, fréquence vs max)
                    var ev7 = GetIntFromJe(je, "ThrottlingEventCount7d", "throttlingEventCount7d");
                    var ev30 = GetIntFromJe(je, "ThrottlingEventCount30d", "throttlingEventCount30d");
                    var preuves = new List<string>();
                    if (ev7.HasValue && ev7.Value > 0) preuves.Add($"{ev7.Value} événement(s) throttle (7j)");
                    if (ev30.HasValue && ev30.Value > 0) preuves.Add($"{ev30.Value} événement(s) throttle (30j)");
                    if (thermal.HasValue && thermal > 0) preuves.Add($"Kernel-Processor-Power thermique (ID 34): {thermal.Value}");
                    if (powerLimit.HasValue && powerLimit > 0) preuves.Add($"Kernel-Processor-Power power limit (ID 37): {powerLimit.Value}");
                    double? freqAvg = null;
                    if (je.TryGetProperty("PercentOfMaxFreqAvg", out var fa) && fa.ValueKind == JsonValueKind.Number) freqAvg = fa.GetDouble();
                    if (freqAvg == null && je.TryGetProperty("percentOfMaxFreqAvg", out var fa2) && fa2.ValueKind == JsonValueKind.Number) freqAvg = fa2.GetDouble();
                    if (freqAvg.HasValue && freqAvg > 0 && freqAvg < 100)
                        preuves.Add($"Fréquence moy. {freqAvg.Value:F1}% du max");
                    var freqMhz = GetIntFromJe(je, "FreqMhzAvg", "freqMhzAvg");
                    if (freqMhz.HasValue && freqMhz > 0)
                        preuves.Add($"Fréquence actuelle {freqMhz.Value} MHz");
                    AddKVIfNew(section, "Preuves", preuves.Count > 0 ? string.Join(" ; ", preuves) : "Aucun événement throttle récent", "", IssueLevel.Info, "diagnostic_signals");

                    // Event counts 7d/30d
                    if (ev7.HasValue)
                        AddKVIfNew(section, "Événements throttle (7j)", ev7.Value, "count", ev7 > 0 ? IssueLevel.Warning : IssueLevel.Info, "diagnostic_signals");
                    if (ev30.HasValue)
                        AddKVIfNew(section, "Événements throttle (30j)", ev30.Value, "count", ev30 > 5 ? IssueLevel.Warning : IssueLevel.Info, "diagnostic_signals");

                    // Thermal vs power breakdown
                    if (thermal.HasValue && thermal > 0)
                        AddKVIfNew(section, "Throttle thermique", thermal.Value, "count", IssueLevel.Warning, "diagnostic_signals");
                    if (powerLimit.HasValue && powerLimit > 0)
                        AddKVIfNew(section, "Throttle alimentation", powerLimit.Value, "count", IssueLevel.Warning, "diagnostic_signals");

                    // Frequency % of max
                    double? freqMin = null;
                    if (je.TryGetProperty("PercentOfMaxFreqMin", out var fm) && fm.ValueKind == JsonValueKind.Number) freqMin = fm.GetDouble();
                    if (freqMin == null && je.TryGetProperty("percentOfMaxFreqMin", out var fm2) && fm2.ValueKind == JsonValueKind.Number) freqMin = fm2.GetDouble();

                    if (freqAvg.HasValue && freqAvg > 0)
                        AddKVIfNew(section, "Fréquence moy. (% max)", freqAvg.Value, "%", freqAvg < 85 ? IssueLevel.Warning : IssueLevel.Info, "diagnostic_signals");
                    if (freqMin.HasValue && freqMin > 0)
                        AddKVIfNew(section, "Fréquence min. (% max)", freqMin.Value, "%", freqMin < 70 ? IssueLevel.Warning : IssueLevel.Info, "diagnostic_signals");

                    // Actual frequency MHz
                    if (freqMhz.HasValue && freqMhz > 0)
                        AddKVIfNew(section, "Fréquence actuelle", freqMhz.Value, "MHz", IssueLevel.Info, "diagnostic_signals");
                }
                else
                {
                    // Fallback: use Notes field
                    AddKVIfNew(section, "Throttling détecté", "Non disponible", "", IssueLevel.Info, "diagnostic_signals");
                    AddKVIfNew(section, "Throttling", sig.Notes ?? "—", "", IssueLevel.Info, "diagnostic_signals");
                }
            }
            catch
            {
                AddKVIfNew(section, "Throttling détecté", "Non disponible", "", IssueLevel.Info, "diagnostic_signals");
                AddKVIfNew(section, "Throttling", sig.Notes ?? "—", "", IssueLevel.Info, "diagnostic_signals");
            }
        }

        private static IssueLevel SeverityToLevel(string? severity)
        {
            if (string.IsNullOrWhiteSpace(severity)) return IssueLevel.Info;
            return severity.ToLowerInvariant() switch
            {
                "critical" => IssueLevel.Critical,
                "high" => IssueLevel.Critical,
                "medium" => IssueLevel.Warning,
                "low" => IssueLevel.Info,
                _ => IssueLevel.Info
            };
        }

        private static void AgentDebugLog(string runId, string hypothesisId, string location, string message, object? data)
        {
            try
            {
                var payload = new Dictionary<string, object?>
                {
                    ["id"] = $"log_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}",
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["runId"] = runId,
                    ["hypothesisId"] = hypothesisId,
                    ["location"] = location,
                    ["message"] = message,
                    ["data"] = data
                };
                File.AppendAllText(
                    @"d:\Tennis\Os\Produits\PC_Repair\Test-codex-analyze-xaml-binding-exception-details\.cursor\debug.log",
                    JsonSerializer.Serialize(payload) + Environment.NewLine);
            }
            catch { }
        }
    }
}
