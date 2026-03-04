using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCDiagnosticPro.AI.Models;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.AI
{
    /// <summary>
    /// Builds compact run context and deterministic fallback action plans.
    /// </summary>
    public class ContextPackBuilder
    {
        private const int CharsPerToken = 4;

        // Perf: JsonSerializerOptions construction is expensive — cache the instance.
        private static readonly JsonSerializerOptions _caseInsensitiveOptions = new() { PropertyNameCaseInsensitive = true };

        // F10: Regex to erase the "integrity" JSON block for raw-hash verification (avoids re-serialization mismatch).
        private static readonly Regex IntegrityJsonBlockRegex = new(
            @"""integrity""\s*:\s*\{[^}]*\}",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private readonly int _maxContextTokens;
        private readonly int _reservedTokens;

        public ContextPackBuilder(AiSettings settings)
        {
            _maxContextTokens = settings.ContextWindow;
            _reservedTokens = settings.MaxTokens + 600;
        }

        public ContextPack Build(CombinedScanResult combined)
        {
            var pack = new ContextPack
            {
                RunId = combined.Metadata?.RunId ?? combined.Trace?.RunId ?? "unknown",
                ScanDate = combined.Metadata?.Timestamp ?? string.Empty
            };

            var sources = new List<string>();
            var findings = new List<string>();
            var tables = new List<string>();

            pack.Summary = BuildSummary(combined, sources);

            AppendFindings(combined, findings, sources);
            AppendStabilitySignals(combined, findings, sources);
            AppendSecurity(combined, tables, sources);
            AppendHardware(combined, tables, sources);
            AppendOperationalTelemetry(combined, tables, sources);

            var budgetChars = (_maxContextTokens - _reservedTokens) * CharsPerToken;
            var findingsBudget = Math.Max(500, (int)Math.Round(budgetChars * 0.55));
            var tablesBudget = Math.Max(500, budgetChars - findingsBudget);
            pack.BudgetBySection["findings"] = findingsBudget;
            pack.BudgetBySection["tables"] = tablesBudget;
            pack.CoverageSummary = $"findings:{findings.Count} tables:{tables.Count} sources:{sources.Distinct(StringComparer.OrdinalIgnoreCase).Count()}";

            // Record pre-truncation counts for transparency banner in UI
            pack.TotalFindingsCount = findings.Count;
            pack.TotalTablesCount = tables.Count;

            pack.KeyFindings = TruncateList(findings, findingsBudget);
            pack.TablesCompact = TruncateList(tables, tablesBudget);
            pack.SourcesUsed = sources.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            pack.EstimatedTokens = pack.ToPromptText().Length / CharsPerToken;

            // Set truncation flag: findings or tables were dropped
            pack.Truncated = pack.KeyFindings.Count < pack.TotalFindingsCount
                          || pack.TablesCompact.Count < pack.TotalTablesCount;

            if (pack.KeyFindings.Count < pack.TotalFindingsCount)
                pack.ExcludedSections.Add("findings");
            if (pack.TablesCompact.Count < pack.TotalTablesCount)
                pack.ExcludedSections.Add("tables");

            if (pack.Truncated)
            {
                App.LogMessage(
                    $"[AI][ContextPack] Truncated: findings={pack.KeyFindings.Count}/{pack.TotalFindingsCount} " +
                    $"tables={pack.TablesCompact.Count}/{pack.TotalTablesCount} " +
                    $"budget={budgetChars} chars");
            }

            return pack;
        }

        public RunAnalysisHeader BuildRunHeader(CombinedScanResult combined)
        {
            var header = new RunAnalysisHeader
            {
                RunId = combined.Metadata?.RunId ?? combined.Trace?.RunId ?? "unknown",
                ErrorCount = combined.Errors?.Count ?? 0,
                MissingDataCount = Math.Max(combined.MissingData?.Count ?? 0, combined.MissingDataStructured?.Count ?? 0)
            };

            if (DateTime.TryParse(
                    combined.Metadata?.Timestamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var parsed))
            {
                header.TimestampUtc = parsed.ToUniversalTime();
            }

            if (combined.DiagnosticSnapshot?.CollectionQuality != null)
            {
                header.CollectionPercent = combined.DiagnosticSnapshot.CollectionQuality.CoveragePercent;
                header.ErrorCount = Math.Max(header.ErrorCount, combined.DiagnosticSnapshot.CollectionQuality.Errors?.Count ?? 0);
            }
            else if (combined.DiagnosticsQuality != null)
            {
                header.CollectionPercent = combined.DiagnosticsQuality.CoverageScore;
            }

            var criticalSignals = CountCriticalAnomalies(combined);
            header.CriticalAnomalyCount = criticalSignals;

            var summary = new StringBuilder();
            summary.Append($"Coverage: {header.CollectionPercent:F0}%");
            summary.Append($" | Errors: {header.ErrorCount}");
            summary.Append($" | Missing: {header.MissingDataCount}");
            summary.Append($" | Critical anomalies: {header.CriticalAnomalyCount}");
            header.Summary = summary.ToString();

            return header;
        }

        public List<ActionPlanItem> BuildDeterministicPlan(CombinedScanResult combined)
        {
            var items = new List<ActionPlanItem>();

            if (combined.DiagnosticSnapshot?.Findings?.Count > 0)
            {
                foreach (var finding in combined.DiagnosticSnapshot.Findings.Take(18))
                {
                    var category = finding.AutoFixPossible && !IsHighRisk(finding.RiskLevel)
                        ? ActionPlanCategory.AutoFix
                        : ActionPlanCategory.Manual;

                    items.Add(new ActionPlanItem
                    {
                        Title = string.IsNullOrWhiteSpace(finding.IssueType) ? "Detected issue" : finding.IssueType,
                        Description = string.IsNullOrWhiteSpace(finding.SuggestedAction)
                            ? finding.Description
                            : finding.SuggestedAction!,
                        Reason = finding.Description,
                        Source = "diagnostic_snapshot.findings",
                        Severity = string.IsNullOrWhiteSpace(finding.Severity) ? "info" : finding.Severity,
                        Category = category,
                        RequiresAdmin = RequiresAdmin(finding)
                    });
                }
            }
            else
            {
                items.Add(new ActionPlanItem
                {
                    Title = "No normalized findings available",
                    Description = "Run analysis can proceed, but AI recommendations are limited until a full run is available.",
                    Reason = "diagnostic_snapshot.findings empty",
                    Source = "deterministic-fallback",
                    Severity = "low",
                    Category = ActionPlanCategory.Manual
                });
            }

            AppendStabilityManualActions(combined, items);
            return items
                .GroupBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Take(24)
                .ToList();
        }

        /// <summary>Load a CombinedScanResult from a combined JSON file path.</summary>
        public static CombinedScanResult? LoadFromFile(string jsonPath)
        {
            return LoadFromFile(jsonPath, out _, out _);
        }

        /// <summary>
        /// Load with parse metrics.
        /// </summary>
        public static CombinedScanResult? LoadFromFile(string jsonPath, out long jsonBytes, out long parseMs)
        {
            jsonBytes = 0;
            parseMs = 0;

            if (!File.Exists(jsonPath))
            {
                return null;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var rawJson = File.ReadAllText(jsonPath, System.Text.Encoding.UTF8);
                jsonBytes = System.Text.Encoding.UTF8.GetByteCount(rawJson);
                var result = JsonSerializer.Deserialize<CombinedScanResult>(rawJson, _caseInsensitiveOptions);
                sw.Stop();
                parseMs = sw.ElapsedMilliseconds;

                // JS-1: Verify integrity hash if present. Non-blocking — only logs a warning on mismatch.
                // Uses the raw JSON string with the integrity block erased (regex) to avoid
                // hash mismatch caused by re-serialization changing field order.
                if (result?.Integrity?.ContentSha256 != null)
                {
                    try
                    {
                        var rawJsonForHash = IntegrityJsonBlockRegex.Replace(rawJson, @"""integrity"":null");
                        var hashBytes = System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(rawJsonForHash));
                        var computedHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
                        if (!string.Equals(computedHash, result.Integrity.ContentSha256, StringComparison.OrdinalIgnoreCase))
                            App.LogMessage($"[JS-1] WARN: Integrity mismatch on {Path.GetFileName(jsonPath)}. " +
                                $"Expected={result.Integrity.ContentSha256} Got={computedHash}. File may be corrupted.");
                    }
                    catch (Exception hashEx)
                    {
                        App.LogMessage($"[JS-1] Integrity verification skipped: {hashEx.Message}");
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                parseMs = sw.ElapsedMilliseconds;
                App.LogMessage($"[AI] ContextPackBuilder.LoadFromFile error path={jsonPath} parseMs={parseMs} message={ex.Message}");
                return null;
            }
        }

        private static string BuildSummary(CombinedScanResult c, List<string> sources)
        {
            var sb = new StringBuilder();

            // Score officiel du scan (source de vérité) — doit être utilisé par le LLM tel quel pour éviter toute dissonance avec l'UI.
            if (c.TechnicalContract?.Score != null && c.TechnicalContract.Score.IsAvailable && c.TechnicalContract.Score.Value.HasValue)
            {
                var score = c.TechnicalContract.Score.Value.Value;
                var grade = string.IsNullOrWhiteSpace(c.TechnicalContract.Score.Grade) ? ScoreToGradeLegacy(score) : c.TechnicalContract.Score.Grade;
                sb.AppendLine($"Score officiel (à utiliser tel quel): {score}/100 | Grade: {grade}");
                sources.Add("TechnicalContract.Score");
            }
            else if (!string.IsNullOrWhiteSpace(c.TechnicalContract?.Score?.UnavailableReason))
            {
                sb.AppendLine($"Score: indisponible — {c.TechnicalContract.Score.UnavailableReason}");
                sources.Add("TechnicalContract.Score");
            }

            if (c.Metadata != null)
            {
                sb.AppendLine($"Run: {c.Metadata.RunId} | Date: {c.Metadata.Timestamp} | Admin: {(c.Metadata.IsAdmin ? "Yes" : "No")} | Duration: {c.Metadata.DurationSeconds:F0}s");
                sources.Add("Metadata");
            }

            if (c.QualityGateReport != null)
            {
                var qg = c.QualityGateReport;
                sb.AppendLine($"Quality gates: {(qg.Passed ? "PASS" : $"FAILED - {string.Join(", ", qg.FailedGates)}")}");
                sources.Add("QualityGateReport");
            }

            if (c.DiagnosticsQuality != null)
            {
                sb.AppendLine($"Coverage: {c.DiagnosticsQuality.CoverageScore}% | Reliability: {c.DiagnosticsQuality.ReliabilityScore}%");
                sources.Add("DiagnosticsQuality");
            }

            if (c.Errors?.Count > 0)
            {
                sb.AppendLine($"Collection errors: {c.Errors.Count} ({string.Join(", ", c.Errors.Take(3).Select(e => e.Source))})");
                sources.Add("Errors");
            }

            if (c.MissingData?.Count > 0 || c.MissingDataStructured?.Count > 0)
            {
                var missing = Math.Max(c.MissingData?.Count ?? 0, c.MissingDataStructured?.Count ?? 0);
                sb.AppendLine($"Missing data entries: {missing}");
                sources.Add("MissingData");
            }

            return sb.ToString().Trim();
        }

        /// <summary>Grade depuis SchemaRegistry (source unique UDIS). Utilisé si technical_contract n'a pas de grade.</summary>
        private static string ScoreToGradeLegacy(int score) => SchemaRegistry.ScoreToGrade(score);

        private static void AppendFindings(CombinedScanResult c, List<string> findings, List<string> sources)
        {
            if (c.Findings?.Count > 0)
            {
                foreach (var f in c.Findings.Take(20))
                {
                    findings.Add($"[{f.Severity}] {f.Type} - {f.Message}");
                }

                sources.Add("Findings");
            }

            if (c.DiagnosticSnapshot?.Findings?.Count > 0)
            {
                foreach (var f in c.DiagnosticSnapshot.Findings.Take(20))
                {
                    findings.Add($"[{f.Severity}] {f.IssueType} - {f.Description}");
                }

                sources.Add("DiagnosticSnapshot");
            }

            if (c.Errors?.Count > 0)
            {
                foreach (var err in c.Errors.Take(20))
                {
                    var code = string.IsNullOrWhiteSpace(err.Code) ? "UNKNOWN" : err.Code;
                    findings.Add($"[Error:{code}] {err.Message}");
                }

                sources.Add("Errors");
            }

            if (c.DiagnosticSnapshot?.CollectionQuality?.Errors?.Count > 0)
            {
                foreach (var err in c.DiagnosticSnapshot.CollectionQuality.Errors.Take(20))
                {
                    findings.Add($"[CollectionError:{err.Severity}] {err.Source} - {err.Message}");
                }

                sources.Add("DiagnosticSnapshot.CollectionQuality.Errors");
            }
        }

        private static void AppendStabilitySignals(CombinedScanResult c, List<string> findings, List<string> sources)
        {
            if (c.DiagnosticSignals != null)
            {
                foreach (var (key, sig) in c.DiagnosticSignals)
                {
                    if (sig == null)
                    {
                        continue;
                    }

                    var quality = sig.Quality ?? string.Empty;
                    if (quality.Equals("suspect", StringComparison.OrdinalIgnoreCase)
                        || quality.Equals("error", StringComparison.OrdinalIgnoreCase)
                        || !sig.Available)
                    {
                        findings.Add($"[Signal:{key}] quality={quality} {sig.Notes ?? sig.Reason ?? string.Empty}".Trim());
                    }
                }

                sources.Add("DiagnosticSignals");
            }

            if (c.EventLogsDetailed?.Count > 0)
            {
                var critical = c.EventLogsDetailed
                    .Where(e => e.Level is 1 or 2)
                    .OrderByDescending(e => e.TimeCreated)
                    .Take(10);
                foreach (var ev in critical)
                {
                    var firstLine = ev.Message?.Split('\n').FirstOrDefault() ?? string.Empty;
                    findings.Add($"[EventLog:{ev.EventId}] {ev.ProviderName} - {firstLine}");
                }

                sources.Add("EventLogs");
            }

            if (c.MinidumpsDetailed?.Count > 0)
            {
                sources.Add("Minidumps");
                findings.Add($"Detected minidumps: {c.MinidumpsDetailed.Count}");
            }
        }

        private static void AppendSecurity(CombinedScanResult c, List<string> tables, List<string> sources)
        {
            var sec = c.SecurityInfoCsharp;
            if (sec == null)
            {
                return;
            }

            var items = new List<string>
            {
                $"BitLocker={sec.BitLockerStatus}"
            };

            if (sec.RealTimeProtectionEnabled != null) items.Add($"DefenderRTP={sec.RealTimeProtectionEnabled}");
            if (sec.TamperProtectionEnabled != null) items.Add($"Tamper={sec.TamperProtectionEnabled}");
            if (sec.SmbV1Enabled != null) items.Add($"SMBv1={sec.SmbV1Enabled}");
            if (sec.VbsEnabled != null) items.Add($"VBS={sec.VbsEnabled}");

            tables.Add($"Security: {string.Join(" | ", items)}");
            sources.Add("SecurityInfo");
        }

        private static void AppendHardware(CombinedScanResult c, List<string> tables, List<string> sources)
        {
            if (c.Cpu?.TemperatureC != null)
            {
                tables.Add($"CPU: temp={c.Cpu.TemperatureC:F0}C (source={c.Cpu.TemperatureSource})");
                sources.Add("HardwareSensors");
            }

            var gpu = c.SensorsCsharp?.Gpu;
            if (gpu != null && (gpu.VramTotalMB?.Value ?? 0) > 0)
            {
                tables.Add($"GPU: {gpu.Name?.Value ?? "?"} | VRAM={gpu.VramUsedMB?.Value:F0}/{gpu.VramTotalMB?.Value:F0}MB | Temp={gpu.GpuTempC?.Value:F0}C");
                sources.Add("HardwareSensors");
            }

            if (c.SmartAttributes?.Count > 0)
            {
                foreach (var disk in c.SmartAttributes.Take(4))
                {
                    tables.Add($"Disk: {disk.InstanceName} | PredictFailure={disk.PredictFailure}");
                }

                sources.Add("SMART");
            }

            if (c.StorageReliability?.Disks?.Count > 0)
            {
                foreach (var disk in c.StorageReliability.Disks.Take(4))
                {
                    tables.Add(
                        $"NVMeReliability: {disk.FriendlyName ?? disk.SerialNumber ?? "Disk"} | " +
                        $"Health={disk.HealthStatus ?? "unknown"} | Wear={disk.WearPercent?.ToString() ?? "NA"} | " +
                        $"Temp={disk.TemperatureC?.ToString() ?? "NA"}C");
                }

                sources.Add("StorageReliability");
            }
        }

        private static void AppendOperationalTelemetry(CombinedScanResult c, List<string> tables, List<string> sources)
        {
            if (c.ProcessTelemetry?.Available == true)
            {
                var topCpu = c.ProcessTelemetry.TopByCpu.FirstOrDefault();
                if (topCpu != null)
                {
                    tables.Add(
                        $"ProcessTelemetry: total={c.ProcessTelemetry.TotalProcessCount} | " +
                        $"topCpu={topCpu.Name}({topCpu.CpuPercent:F1}%)");
                }
                else
                {
                    tables.Add($"ProcessTelemetry: total={c.ProcessTelemetry.TotalProcessCount} | no active CPU spike");
                }

                sources.Add("ProcessTelemetry");
            }

            if (c.NetworkDiagnostics?.Available == true)
            {
                tables.Add(
                    $"Network: p50={c.NetworkDiagnostics.OverallLatencyMsP50:F1}ms | " +
                    $"p95={c.NetworkDiagnostics.OverallLatencyMsP95:F1}ms | " +
                    $"loss={c.NetworkDiagnostics.OverallLossPercent:F1}% | " +
                    $"dnsP95={c.NetworkDiagnostics.DnsP95Ms:F1}ms");

                if (c.NetworkDiagnostics.Throughput?.DownloadMbpsMedian != null)
                {
                    tables.Add($"NetworkThroughput: down={c.NetworkDiagnostics.Throughput.DownloadMbpsMedian.Value:F1}Mbps");
                }

                if (c.NetworkDiagnostics.Throughput?.UploadMbpsMedian != null)
                {
                    tables.Add($"NetworkThroughput: up={c.NetworkDiagnostics.Throughput.UploadMbpsMedian.Value:F1}Mbps");
                }

                sources.Add("NetworkDiagnostics");
            }
        }

        private static List<string> TruncateList(List<string> items, int budgetChars)
        {
            var result = new List<string>();
            var used = 0;

            foreach (var item in items)
            {
                if (used + item.Length > budgetChars)
                {
                    break;
                }

                result.Add(item);
                used += item.Length;
            }

            return result;
        }

        private static bool IsHighRisk(string? riskLevel)
        {
            if (string.IsNullOrWhiteSpace(riskLevel))
            {
                return false;
            }

            return riskLevel.Equals("critical", StringComparison.OrdinalIgnoreCase)
                || riskLevel.Equals("high", StringComparison.OrdinalIgnoreCase);
        }

        private static bool RequiresAdmin(NormalizedFinding finding)
        {
            var action = finding.SuggestedAction ?? string.Empty;
            var issue = finding.IssueType ?? string.Empty;
            return action.Contains("Set-", StringComparison.OrdinalIgnoreCase)
                || action.Contains("Restart-Service", StringComparison.OrdinalIgnoreCase)
                || issue.Contains("Firewall", StringComparison.OrdinalIgnoreCase)
                || issue.Contains("Defender", StringComparison.OrdinalIgnoreCase)
                || issue.Contains("Uac", StringComparison.OrdinalIgnoreCase)
                || issue.Contains("BitLocker", StringComparison.OrdinalIgnoreCase);
        }

        private static int CountCriticalAnomalies(CombinedScanResult combined)
        {
            var count = 0;
            if (combined.DiagnosticSnapshot?.Findings != null)
            {
                count += combined.DiagnosticSnapshot.Findings.Count(f =>
                    f.Severity.Equals("critical", StringComparison.OrdinalIgnoreCase)
                    || f.Severity.Equals("high", StringComparison.OrdinalIgnoreCase));
            }

            if (combined.EventLogsDetailed != null)
            {
                count += combined.EventLogsDetailed.Count(e =>
                    e.EventId == 41
                    || e.ProviderName.Contains("WHEA", StringComparison.OrdinalIgnoreCase)
                    || e.ProviderName.Contains("BugCheck", StringComparison.OrdinalIgnoreCase));
            }

            return count;
        }

        private static void AppendStabilityManualActions(CombinedScanResult combined, List<ActionPlanItem> items)
        {
            if (combined.EventLogsDetailed == null)
            {
                return;
            }

            if (combined.EventLogsDetailed.Any(e => e.EventId == 41))
            {
                items.Add(new ActionPlanItem
                {
                    Title = "Investigate Kernel-Power 41 events",
                    Description = "Check PSU stability, overheating, BIOS settings, and recent driver changes.",
                    Reason = "Kernel-Power 41 indicates abrupt shutdowns/reboots.",
                    Source = "event_logs_detailed",
                    Severity = "high",
                    Category = ActionPlanCategory.Manual
                });
            }

            if (combined.EventLogsDetailed.Any(e => e.ProviderName.Contains("WHEA", StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(new ActionPlanItem
                {
                    Title = "Investigate WHEA hardware errors",
                    Description = "Run memory and CPU stability tests, and update BIOS/chipset drivers.",
                    Reason = "WHEA events indicate hardware-level instability.",
                    Source = "event_logs_detailed",
                    Severity = "high",
                    Category = ActionPlanCategory.Manual
                });
            }

            if (combined.SmartAttributes?.Any(d => d.PredictFailure) == true)
            {
                items.Add(new ActionPlanItem
                {
                    Title = "Backup and replace failing disk",
                    Description = "Immediate backup recommended; SMART predicts disk failure.",
                    Reason = "SMART PredictFailure=true.",
                    Source = "smart_attributes",
                    Severity = "critical",
                    Category = ActionPlanCategory.Manual
                });
            }

            if (combined.UpdatesCsharp?.RebootRequired == true)
            {
                items.Add(new ActionPlanItem
                {
                    Title = "Schedule system reboot",
                    Description = "Windows Update reports reboot required. Reboot when safe.",
                    Reason = "updates_csharp.rebootRequired=true",
                    Source = "updates_csharp",
                    Severity = "medium",
                    Category = ActionPlanCategory.Manual
                });
            }
        }
    }
}
