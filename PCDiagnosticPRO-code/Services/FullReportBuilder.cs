using System;
using System.Collections.Generic;
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
                BuildSystemSection(snapshot, metadata),
                BuildCpuSection(snapshot, combined.SensorsCsharp),
                BuildGpuSection(snapshot, combined.SensorsCsharp),
                BuildMemorySection(snapshot),
                BuildStorageSection(snapshot, combined.SensorsCsharp),
                BuildNetworkSection(snapshot, combined.NetworkDiagnostics),
                BuildStabilitySection(snapshot),
                BuildSecuritySection(snapshot, combined.SecurityInfoCsharp),
                BuildUpdatesSection(snapshot, combined.UpdatesCsharp),
                BuildDevicesSection(snapshot, combined.DriverInventory),
                BuildCollectorErrorsSection(errors, missingData, combined.CollectorDiagnostics, snapshot),
                BuildTechnicalLogSection(snapshot, combined)
            };

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

            vm.SelectFirstSection();
            return vm;
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

        private static ReportSectionViewModel BuildCpuSection(DiagnosticSnapshot? snapshot, HardwareSensorsResult? sensors)
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
                    AddKVIfNew(section, "Charge GPU", gpu.GpuLoadPercent.Value, "%", IssueLevel.Info, "C#");
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

        private static ReportSectionViewModel BuildStabilitySection(DiagnosticSnapshot? snapshot)
        {
            var section = new ReportSectionViewModel { Id = "Stability", Title = "Stabilité système", Level = IssueLevel.Info };

            // Stability metrics
            AddMetricsGroup(section, snapshot, "stability", "PS");

            // WHEA, power, performance groups if available
            AddMetricsGroup(section, snapshot, "whea", "PS");
            AddMetricsGroup(section, snapshot, "power", "PS");
            AddMetricsGroup(section, snapshot, "performance", "PS");

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

            section.SummaryLine1 = section.KeyValues.Count > 0
                ? "Erreurs système et application ci-dessous."
                : "Données de stabilité non disponibles.";
            section.SummaryLine2 = "Recommandation: inspecter EventId dominants dans l'Observateur d'événements.";
            section.EvidenceText = BuildEvidenceText(snapshot?.GeneratedAt, null, "DiagnosticSnapshot.Metrics[stability,whea,power,performance]");
            return section;
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
    }
}
