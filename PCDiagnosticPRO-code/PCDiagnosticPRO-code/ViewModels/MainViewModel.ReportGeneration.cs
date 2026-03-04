using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Data;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.Services;
using PCDiagnosticPro.Services.NetworkDiagnostics;
using PCDiagnosticPro.Themes;
using PCDiagnosticPro.Views;

namespace PCDiagnosticPro.ViewModels
{
    public partial class MainViewModel
    {
        private void OnScanPipelineCompleted(ScanResult? result, string resultsMessage, string statusMessage, bool forceCompletedStatus)
        {
            void RunOnUi()
            {
                App.LogMessage("Attempt build chart: démarrage");
                ResultsMessage = resultsMessage;
                StatusMessage = statusMessage;
                ScanHistoryItem? latestHistoryItem = null;

                if (result != null)
            {
                try
                {
                    result.Summary.TotalItems = result.Items.Count;
                    ScanResult = result;
                    UpdateScanItemsFromResult(result);
                    UpdateResultSectionsFromResult(result);
                    latestHistoryItem = AddToHistory(result);

                    var chartReady = TryBuildChartData(result, out var chartFailureReason);
                    if (!chartReady)
                    {
                        ResultsMessage = $"Graphique indisponible: {chartFailureReason}";
                        App.LogMessage($"Chart build KO: {chartFailureReason}");
                    }
                    else
                    {
                        App.LogMessage("Chart build OK");
                    }
                }
                catch (Exception ex)
                {
                    ResultsMessage = $"Graphique indisponible: {ex.Message}";
                    App.LogMessage($"Chart build exception: {ex.Message}");
                }
            }
            else
            {
                ScanResult = null;
                ScanItems.Clear();
                ResultSections.Clear();
                OnPropertyChanged(nameof(HasResultSections));
                ResultsMessage = resultsMessage;
                ErrorMessage = resultsMessage;
                App.LogMessage($"Chart build skipped: {resultsMessage}");
            }

            try
            {
                PersistFinalScanArtifacts(result, latestHistoryItem, ResultsMessage);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[History][RunId:{_activeRunId}] PersistFinalScanArtifacts failed: {ex.Message}");
            }

            // Navigate to Results FIRST (before tearing down scan UI) so view switch is clean
            NavigateToResults(latestHistoryItem);

            // Post-scan: Return to idle state (no grade display on button)
            ScanState = "Idle";
            App.LogMessage($"=== FIN SCAN ===");
            App.LogMessage($"IsScanning={IsScanning}, ScanState={ScanState} (reset to Idle)");
            if (forceCompletedStatus)
            {
                CurrentStep = GetString("ResultsCompletedTitle");
                StatusMessage = GetString("ResultsCompletedTitle");
            }
            else
            {
                CurrentStep = statusMessage;
            }
            AddLiveFeedItem(GetString("LiveFeed_PhaseEnd_Rapport"));
            UpdateProgress(100, GetString("PhaseLabel_Rapport"));
            CompleteScanProgressPhase(ScanProgressPhase.ReportBuild, "Rapport construit");
            BeginScanProgressPhase(ScanProgressPhase.UiFinalize, "Finalisation UI", "Finalisation de l'affichage");
            SetSectionPhase(6, "Done");
            CompleteScanProgressPhase(ScanProgressPhase.UiFinalize, "Affichage finalisé");
            StopScanProgressTimer();
            // FIX #6: Stop elapsed time timer ONLY when report generation is truly finished
            _liveFeedTimer.Stop();
            App.LogMessage("Progress=100 / IsScanning=false / LiveFeedTimer stopped");
            }
            if (Application.Current?.Dispatcher?.CheckAccess() == true)
                RunOnUi();
            else
                Application.Current?.Dispatcher?.Invoke(RunOnUi);
        }

        private bool TryBuildChartData(ScanResult result, out string reason)
        {
            reason = string.Empty;
            try
            {
                var summary = result.Summary;
                App.LogMessage($"Attempt build chart: total={summary.TotalItems} ok={summary.OkCount} warn={summary.WarningCount} err={summary.ErrorCount} crit={summary.CriticalCount}");

                if (summary.TotalItems <= 0)
                {
                    reason = "Aucune donnée disponible";
                    return false;
                }

                if (summary.OkCount + summary.WarningCount + summary.ErrorCount + summary.CriticalCount <= 0)
                {
                    reason = "Métriques de sévérité vides";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        private async Task LoadJsonResultAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_resultJsonPath))
                {
                    throw new FileNotFoundException("Chemin JSON introuvable.");
                }

                App.LogMessage($"Fichier JSON final choisi: {_resultJsonPath}");
                var jsonContent = await File.ReadAllTextAsync(_resultJsonPath, Encoding.UTF8);
                App.LogMessage($"Taille du fichier JSON: {jsonContent.Length} caractères");

                // Gate #1: validate PowerShell snapshot shape before mapping.
                try
                {
                    using var psDoc = JsonDocument.Parse(jsonContent);
                    var psRoot = psDoc.RootElement.TryGetProperty("scan_powershell", out var wrappedPs)
                        ? wrappedPs
                        : psDoc.RootElement;
                    var psGate = TechnicalContractValidator.ValidatePowerShellSnapshot(psRoot);
                    ApplyGateResult(psGate, "ps_parse_pre_mapping", null);
                }
                catch (Exception gateEx)
                {
                    App.LogMessage($"[ContractGate] PS pre-map gate error: {gateEx.Message}");
                }
                
                // Parse legacy pour compatibilité
                var result = _jsonMapper.Parse(jsonContent, _resultJsonPath, _scanStopwatch.Elapsed);
                result.Summary.TotalItems = result.Items.Count;
                HealthReport? healthReportForUi = null;
                
                // ===== CONSTRUCTION HEALTH REPORT INDUSTRIEL AVEC CAPTEURS =====
                try
                {
                    // FIX: Utiliser le JSON combiné (contient diagnostic_signals, network_diagnostics, sensors_csharp)
                    // au lieu du JSON PowerShell brut qui ne contient pas les données C#
                    var healthReportJsonContent = !string.IsNullOrEmpty(_lastCombinedJsonContent) 
                        ? _lastCombinedJsonContent 
                        : jsonContent;
                    
                    if (!string.IsNullOrEmpty(_lastCombinedJsonContent))
                    {
                        App.LogMessage("[HealthReport] Utilisation du JSON combiné (avec diagnostic_signals, network_diagnostics)");
                    }
                    else
                    {
                        App.LogMessage("[HealthReport] WARN: JSON combiné non disponible, fallback sur PS brut");
                        // Injecter event_logs_detailed si on a les données C# pour éviter "Inconnu" sur Erreurs critiques
                        if (_lastEventLogsDetailed != null && _lastEventLogsDetailed.Count >= 0 &&
                            !healthReportJsonContent.Contains("\"event_logs_detailed\"", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var node = JsonNode.Parse(healthReportJsonContent);
                                if (node is JsonObject obj)
                                {
                                    obj["event_logs_detailed"] = JsonSerializer.SerializeToNode(_lastEventLogsDetailed, HardwareSensorsResult.JsonOptions);
                                    healthReportJsonContent = node.ToJsonString(HardwareSensorsResult.JsonOptions);
                                    App.LogMessage("[HealthReport] event_logs_detailed injecté dans JSON PS pour section OS");
                                }
                            }
                            catch (Exception injEx)
                            {
                                App.LogMessage($"[HealthReport] Injection event_logs_detailed: {injEx.Message}");
                            }
                        }
                    }

                    // Passer les capteurs hardware pour injection dans EvidenceData
                    var healthReport = HealthReportBuilder.Build(
                        healthReportJsonContent,
                        _lastSensorsResult,
                        _lastDriverInventory,
                        _lastWindowsUpdateResult);
                    healthReportForUi = healthReport;
                    App.LogMessage($"[HealthReport] Construit: Score={healthReport.GlobalScore}, Grade={healthReport.Grade}, " +
                        $"Sections={healthReport.Sections.Count}, Confiance={healthReport.ConfidenceModel.ConfidenceLevel}");
                    App.LogMessage($"CollectionStatus={healthReport.CollectionStatus}; errors={healthReport.Errors?.Count ?? 0}; collectorErrorsLogical={healthReport.CollectorErrorsLogical}; missingDataCount={healthReport.MissingData?.Count ?? 0}");
                    App.LogMessage($"ScoreV2_PS={healthReport.ScoreV2?.Score ?? 0}; UDIS={healthReport.Divergence?.GradeEngineScore ?? 0}; FinalScore={healthReport.GlobalScore}; FinalGrade={healthReport.Grade}; ConfidenceScore={healthReport.ConfidenceModel?.ConfidenceScore ?? 0}");
                    
                    // SYNCHRONISER LE SCORE UNIFIÉ (FinalScore = source de vérité)
                    // On synchronise Summary.Score pour que TOUTE l'UI affiche le même score
                    var unifiedScore = healthReport.GlobalScore;
                    var unifiedGrade = healthReport.Grade;
                    
                    if (result.Summary.Score != unifiedScore)
                    {
                        App.LogMessage($"[ScoreUnifié] Synchronisation: Legacy={result.Summary.Score} -> UDIS={unifiedScore} ({unifiedGrade})");
                        App.LogMessage($"[ScoreUnifié] Divergence PS({healthReport.ScoreV2?.Score ?? 0}) vs App({unifiedScore}) = delta {healthReport.Divergence?.Delta ?? 0}");
                        result.Summary.Score = unifiedScore;
                        result.Summary.Grade = unifiedGrade;
                    }
                    
                    // P0 Bloc C: Qualité de collecte - calcul, log, injection dans JSON combiné
                    var quality = QualityScoreCalculator.Compute(healthReport, null);
                    QualityScoreCalculator.WriteQualityLog(quality);
                    if (!string.IsNullOrEmpty(_combinedJsonPath) && !string.IsNullOrEmpty(_lastCombinedJsonContent))
                    {
                        try
                        {
                            var combined = _lastCombinedResult ??
                                           JsonSerializer.Deserialize<CombinedScanResult>(_lastCombinedJsonContent, HardwareSensorsResult.JsonOptions);
                            if (combined != null)
                            {
                                combined.DiagnosticsQuality = quality;
                                combined.TechnicalContract = TechnicalContractBuilder.Build(combined, healthReport);
                                var combinedGate = TechnicalContractValidator.ValidateCombinedResult(
                                    combined,
                                    null,
                                    _contractGateOptions);
                                var mergedStatus = ApplyGateResult(combinedGate, "combined_pre_export", healthReportForUi ?? healthReport);
                                combined.RunStatus = mergedStatus;

                                var updatedJson = SerializeCombinedResult(combined);
                                await File.WriteAllTextAsync(_combinedJsonPath, updatedJson, Encoding.UTF8);
                                SetCombinedJsonContent(updatedJson, combined);
                                App.LogMessage($"[QualityScore] diagnostics_quality + technical_contract + run_status injectés dans {_combinedJsonPath}");
                            }
                        }
                        catch (Exception qex)
                        {
                            App.LogMessage($"[QualityScore] Erreur injection JSON: {qex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[HealthReport] ERREUR construction: {ex.Message}");
                    healthReportForUi = null;
                }
                // ===== FIN HEALTH REPORT =====
                
                // ===== GÉNÉRATION TXT UNIFIÉ (PS + SENSORS + SCORE) =====
                var outputDir = Path.GetDirectoryName(_resultJsonPath) ?? _reportsDir;
                await GenerateUnifiedTxtReportAsync(outputDir);
                // ===== FIN TXT UNIFIÉ =====

                // ===== VALIDATION COMPLÉTUDE UI (NON-BLOQUANT) =====
                try
                {
                    var validationJson = !string.IsNullOrWhiteSpace(_lastCombinedJsonContent)
                        ? _lastCombinedJsonContent
                        : jsonContent;
                    using var validationDoc = JsonDocument.Parse(validationJson);
                    var validationResult = UiCompletenessValidator.Validate(validationDoc.RootElement, healthReportForUi, _lastSensorsResult);
                    if (!validationResult.AllValid)
                    {
                        App.LogMessage($"[UiValidator] WARNINGS: {validationResult.CriticalWarnings.Count}");
                        // Log le rapport détaillé en mode debug
                        if (ComprehensiveEvidenceExtractor.DebugPathsEnabled)
                        {
                            App.LogMessage(UiCompletenessValidator.GenerateReport(validationResult));
                        }
                    }

                    var uiGate = TechnicalContractValidator.ValidateCombinedJsonRoot(
                        validationDoc.RootElement,
                        validationResult,
                        _contractGateOptions);
                    var status = ApplyGateResult(uiGate, "ui_pre_bind", healthReportForUi);
                    await PersistRunStatusAsync(status);
                }
                catch (Exception valEx)
                {
                    App.LogMessage($"[UiValidator] Erreur non-bloquante: {valEx.Message}");
                }
                // ===== FIN VALIDATION UI =====

                App.LogMessage($"Scan terminé: Score={result.Summary.Score} | JSON={_resultJsonPath}");
                App.LogMessage("Parse OK");
                var reportToSet = healthReportForUi;
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    InjectSpeedTestIntoNetworkSection(reportToSet);
                    HealthReport = reportToSet;
                    if (result.IsValid)
                        OnScanPipelineCompleted(result, string.Empty, GetString("ResultsCompletedTitle"), forceCompletedStatus: true);
                    else
                        OnScanPipelineCompleted(result, GetString("StatusParsingError"), GetString("StatusParsingError"), forceCompletedStatus: false);
                });
            }
            catch (JsonException ex)
            {
                var tempDump = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_LastBadJson.json");
                try
                {
                    if (!string.IsNullOrWhiteSpace(_resultJsonPath) && File.Exists(_resultJsonPath))
                    {
                        var raw = await File.ReadAllTextAsync(_resultJsonPath, Encoding.UTF8);
                        await File.WriteAllTextAsync(tempDump, raw, Encoding.UTF8);
                    }
                }
                catch
                {
                    // Ignorer
                }

                var jsonFailMsg = $"Rapport corrompu. Dump: {tempDump}";
                var jsonFailStatus = GetString("StatusParsingError");
                App.LogMessage($"Parse FAIL: {ex.Message} | Dump={tempDump}");
                Application.Current?.Dispatcher?.Invoke(() => OnScanPipelineCompleted(null, jsonFailMsg, jsonFailStatus, forceCompletedStatus: false));
            }
            catch (Exception ex)
            {
                var loadFailMsg = $"{GetString("StatusLoadReportError")} {ex.Message}";
                var loadFailStatus = GetString("StatusLoadReportError");
                App.LogMessage($"Parse FAIL: {ex.Message}");
                Application.Current?.Dispatcher?.Invoke(() => OnScanPipelineCompleted(null, loadFailMsg, loadFailStatus, forceCompletedStatus: false));
            }
        }

        private string SerializeCombinedResult(CombinedScanResult combined)
        {
            if (DeterministicMode.IsEnabled())
                DeterministicMode.NormalizeCombinedInPlace(combined);
            return JsonSerializer.Serialize(combined, HardwareSensorsResult.JsonOptions);
        }

        private RunStatusEnvelope ApplyGateResult(
            TechnicalContractValidator.GateValidationResult gate,
            string gatePhase,
            HealthReport? report)
        {
            var baselineState = report?.CollectionStatus;
            if (string.IsNullOrWhiteSpace(baselineState))
                baselineState = _lastRunStatus?.State ?? RunState.Ok;

            var status = gate.ToRunStatus(baselineState!);
            _lastRunStatus = status;

            if (!gate.IsValid && report != null)
            {
                if (!string.Equals(report.CollectionStatus, RunState.Failed, StringComparison.OrdinalIgnoreCase))
                    report.CollectionStatus = RunState.Incomplete;

                foreach (var reason in gate.ReasonCodes)
                {
                    if (!report.MissingData.Contains($"contract_gate:{reason}", StringComparer.OrdinalIgnoreCase))
                        report.MissingData.Add($"contract_gate:{reason}");
                }

                if (gate.ReasonCodes.Contains(TechnicalContractValidator.ReasonUiCoverageBelowThreshold, StringComparer.OrdinalIgnoreCase))
                {
                    report.DataReliabilityScore = Math.Min(report.DataReliabilityScore, 69);
                }
            }

            UpdateGateBanner(status, gatePhase);
            RefreshGateBoundProperties();

            if (!gate.IsValid)
            {
                App.LogMessage($"[ContractGate] {gatePhase}: INCOMPLETE ({string.Join(",", gate.ReasonCodes)})");
                foreach (var msg in gate.Messages)
                    App.LogMessage($"[ContractGate] {gatePhase}: {msg}");
            }

            return status;
        }

        private void RefreshGateBoundProperties()
        {
            void Refresh()
            {
                OnPropertyChanged(nameof(CollectionStatusBadgeText));
                OnPropertyChanged(nameof(IsCollectionPartialOrFailed));
                OnPropertyChanged(nameof(UnifiedReliabilityScore));
                OnPropertyChanged(nameof(UnifiedReliabilityLabel));
                OnPropertyChanged(nameof(UnifiedReliabilityDisplay));
                OnPropertyChanged(nameof(DataReliabilityScore));
                OnPropertyChanged(nameof(DataReliabilityDisplay));
            }

            if (Application.Current?.Dispatcher?.CheckAccess() == true)
                Refresh();
            else
                Application.Current?.Dispatcher?.Invoke(Refresh);
        }

        private void UpdateGateBanner(RunStatusEnvelope status, string gatePhase)
        {
            void Apply()
            {
                if (status.HasGateFailures)
                {
                    var reasons = status.ReasonCodes.Count > 0
                        ? string.Join(" | ", status.ReasonCodes)
                        : "TECHNICAL_CONTRACT";
                    ContractGateBannerText = $"INCOMPLETE: {reasons}";
                    ContractGateBannerDetails =
                        $"Gate={gatePhase} | Failed={string.Join(", ", status.FailedGates)}" +
                        (status.UiCoveragePercent.HasValue && status.Threshold.HasValue
                            ? $" | UI={status.UiCoveragePercent.Value:F0}%/{status.Threshold.Value:F0}%"
                            : string.Empty);
                }
                else
                {
                    ContractGateBannerText = string.Empty;
                    ContractGateBannerDetails = string.Empty;
                }
            }

            if (Application.Current?.Dispatcher?.CheckAccess() == true)
                Apply();
            else
                Application.Current?.Dispatcher?.Invoke(Apply);
        }

        private async Task PersistRunStatusAsync(RunStatusEnvelope status)
        {
            if (status == null || string.IsNullOrWhiteSpace(_combinedJsonPath) || !File.Exists(_combinedJsonPath))
                return;

            try
            {
                var combined = _lastCombinedResult;
                if (combined == null)
                {
                    if (string.IsNullOrWhiteSpace(_lastCombinedJsonContent))
                        return;
                    combined = JsonSerializer.Deserialize<CombinedScanResult>(_lastCombinedJsonContent, HardwareSensorsResult.JsonOptions);
                if (combined == null)
                    return;
                }

                if (AreRunStatusesEquivalent(combined.RunStatus, status))
                    return;

                combined.RunStatus = status;
                var updatedJson = SerializeCombinedResult(combined);
                await File.WriteAllTextAsync(_combinedJsonPath, updatedJson, Encoding.UTF8);
                SetCombinedJsonContent(updatedJson, combined);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ContractGate] PersistRunStatus failed: {ex.Message}");
            }
        }

        private static bool AreRunStatusesEquivalent(RunStatusEnvelope? left, RunStatusEnvelope? right)
        {
            if (left == null || right == null)
                return false;

            if (!string.Equals(left.State, right.State, StringComparison.OrdinalIgnoreCase))
                return false;

            if (left.HasGateFailures != right.HasGateFailures)
                return false;

            if (left.UiCoveragePercent != right.UiCoveragePercent)
                return false;

            if (left.Threshold != right.Threshold)
                return false;

            var leftReasons = left.ReasonCodes ?? new List<string>();
            var rightReasons = right.ReasonCodes ?? new List<string>();
            if (!leftReasons.SequenceEqual(rightReasons, StringComparer.OrdinalIgnoreCase))
                return false;

            var leftFailedGates = left.FailedGates ?? new List<string>();
            var rightFailedGates = right.FailedGates ?? new List<string>();
            if (!leftFailedGates.SequenceEqual(rightFailedGates, StringComparer.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private async Task<string> ResolveResultJsonPathAsync(string outputDir, DateTimeOffset scanStartTime, CancellationToken token)
        {
            var patterns = GetJsonSearchPatterns();
                        var candidateDirs = GetCandidateReportDirectories(outputDir);

            if (!string.IsNullOrWhiteSpace(_jsonCompletionMarkerPath))
            {
                var markerResolvedPath = await WaitForCompletionMarkerAsync(_jsonCompletionMarkerPath, token);
                if (!string.IsNullOrWhiteSpace(markerResolvedPath) &&
                    await WaitForJsonReadyAsync(markerResolvedPath, token))
                {
                    LogJsonFileDetails(markerResolvedPath);
                    return markerResolvedPath;
                }
            }

            App.LogMessage($"Dossier JSON détecté: {outputDir}");
            App.LogMessage($"Pattern JSON détecté: {string.Join(", ", patterns)}");

            // PRIORITÉ 1: JSON annoncé via stdout [OK] JSON: path
            if (!string.IsNullOrWhiteSpace(_jsonPathFromOutput))
            {
                App.LogMessage($"[JSON SOURCE] Priorité 1 - stdout: {_jsonPathFromOutput}");
                if (await WaitForJsonReadyAsync(_jsonPathFromOutput, token))
                {
                    App.LogMessage($"[JSON RÉSOLU] Via stdout: {_jsonPathFromOutput}");
                    LogJsonFileDetails(_jsonPathFromOutput);
                    return _jsonPathFromOutput;
                }
                App.LogMessage($"[JSON] stdout path non accessible, fallback suivant...");
            }

            // PRIORITÉ 2: Path attendu par défaut
            if (File.Exists(_resultJsonPath))
            {
                App.LogMessage($"[JSON SOURCE] Priorité 2 - path attendu: {_resultJsonPath}");
                if (await WaitForJsonReadyAsync(_resultJsonPath, token))
                {
                    App.LogMessage($"[JSON RÉSOLU] Via path attendu: {_resultJsonPath}");
                    LogJsonFileDetails(_resultJsonPath);
                    return _resultJsonPath;
                }
            }

            // PRIORITÉ 3: Scan récursif des dossiers candidats
            App.LogMessage($"[JSON SOURCE] Priorité 3 - scan récursif dans {candidateDirs.Count} dossiers");
            foreach (var dir in candidateDirs)
            {
                var latestJson = FindLatestJsonAfter(dir, patterns, scanStartTime);
                if (!string.IsNullOrWhiteSpace(latestJson))
                {
                    App.LogMessage($"[JSON] Candidat trouvé: {latestJson}");
                    if (await WaitForJsonReadyAsync(latestJson, token))
                    {
                        App.LogMessage($"[JSON RÉSOLU] Via scan récursif: {latestJson}");
                        LogJsonFileDetails(latestJson);
                        return latestJson;
                    }
                }
            }
            
            App.LogMessage("[JSON] Aucune source n'a retourné de fichier valide");
            return string.Empty;
        }
        private async Task<string?> WaitForCompletionMarkerAsync(string markerPath, CancellationToken token)
        {
            const int maxAttempts = 40;
            const int delayMs = 250;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    if (!File.Exists(markerPath))
                    {
                        await Task.Delay(delayMs, token);
                        continue;
                    }

                    var markerContent = await File.ReadAllTextAsync(markerPath, Encoding.UTF8, token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(markerContent))
                    {
                        await Task.Delay(delayMs, token);
                        continue;
                    }

                    using var markerDoc = JsonDocument.Parse(markerContent);
                    var root = markerDoc.RootElement;
                    if (!root.TryGetProperty("jsonPath", out var jsonPathEl))
                    {
                        await Task.Delay(delayMs, token);
                        continue;
                    }

                    var jsonPath = jsonPathEl.GetString();
                    if (!string.IsNullOrWhiteSpace(jsonPath))
                    {
                        App.LogMessage($"[Marker][RunId:{_activeRunId}] Completion marker detected at attempt {attempt}: {markerPath}");
                        try
                        {
                            File.Delete(markerPath);
                            App.LogMessage($"[Marker][RunId:{_activeRunId}] Marker consumed and removed: {markerPath}");
                        }
                        catch (Exception markerDeleteEx)
                        {
                            App.LogMessage($"[Marker][RunId:{_activeRunId}] Marker delete warning: {markerDeleteEx.Message}");
                        }
                        return jsonPath;
                    }
                }
                catch (IOException)
                {
                    // Marker is still being written.
                }
                catch (JsonException)
                {
                    // Marker content is not stable yet.
                }

                await Task.Delay(delayMs, token);
            }

            App.LogMessage($"[Marker] Timeout waiting for completion marker: {markerPath}");
            return null;
        }

        private async Task WriteCombinedResultAsync(string outputDir, HardwareSensorsResult sensorsResult)
        {
            if (!File.Exists(_resultJsonPath))
            {
                App.LogMessage("JSON PowerShell introuvable pour l'enveloppe combinée.");
                return;
            }

            try
            {
                // P2.1 Normaliser sentinelles AVANT écriture JSON combiné (alignement TXT↔JSON)
                var sanitizeActions = DataSanitizer.SanitizeSensors(sensorsResult);
                if (sanitizeActions.Count > 0)
                {
                    App.LogMessage($"[SANITIZE] Avant écriture JSON combiné: {sanitizeActions.Count} métrique(s) invalidée(s)");
                    foreach (var a in sanitizeActions)
                        App.LogMessage($"  SANITIZE: {a}");
                }

                // P1.1: canonical dedicated VRAM policy (bounded percent + provenance).
                GpuMetricCanonPolicy.ApplyInPlace(sensorsResult);

                var jsonContent = await File.ReadAllTextAsync(_resultJsonPath, Encoding.UTF8);
                using var doc = JsonDocument.Parse(jsonContent);

                // Build DiagnosticSnapshot with schemaVersion 2.3.0 (contractual - see SchemaRegistry)
                var snapshotBuilder = new DiagnosticSnapshotBuilder()
                    .AddCpuMetrics(sensorsResult)
                    .AddGpuMetrics(sensorsResult)
                    .AddStorageMetrics(sensorsResult)
                    .AddPowerShellData(doc.RootElement)
                    .AddDiagnosticSignals(_lastDiagnosticSignals?.Signals);
                
                var diagnosticSnapshot = snapshotBuilder.Build();
                _lastDiagnosticSnapshot = diagnosticSnapshot;

                // P0.2: Collect WMI errors for detailed diagnostics
                var wmiErrors = WmiQueryRunner.GetErrors();
                CollectorDiagnostics? collectorDiagnostics = null;
                if (wmiErrors.Count > 0)
                {
                    collectorDiagnostics = new CollectorDiagnostics { WmiErrors = wmiErrors };
                    App.LogMessage($"[WmiErrors] {wmiErrors.Count} erreurs WMI capturées pour le rapport");
                }

                var combined = new CombinedScanResult
                {
                    ScanPowershell = doc.RootElement.Clone(),
                    SensorsCsharp = sensorsResult,
                    Cpu = CpuTemperatureSummary.FromSensors(sensorsResult),
                    DiagnosticSnapshot = diagnosticSnapshot,
                    DiagnosticSignals = _lastDiagnosticSignals?.Signals,
                    ProcessTelemetry = _lastProcessTelemetry,
                    NetworkDiagnostics = _lastNetworkDiagnostics,
                    CollectorDiagnostics = collectorDiagnostics,
                    DriverInventory = _lastDriverInventory,
                    UpdatesCsharp = _lastWindowsUpdateResult,
                    SecurityInfoCsharp = _lastSecurityInfo,
                    PerformanceTimeseriesSummary = _lastPerformanceTimeseriesSummary,
                    EventLogsDetailed = _lastEventLogsDetailed ?? new List<EventLogDetailedEntry>(),
                    SmartAttributes = _lastSmartAttributes,
                    MinidumpsDetailed = _lastMinidumpsDetailed,
                    Timings = BuildTimingEnvelope(doc.RootElement)
                };
                
                // === EXTRACTION DES NŒUDS EXPLICITES (missingData, metadata, findings, errors, sections, paths) ===
                ExtractExplicitNodes(doc.RootElement, combined, outputDir);

                // Inject technical contract early (score fields will be finalized after HealthReport/UDIS).
                combined.TechnicalContract = TechnicalContractBuilder.Build(combined, null);
                // P0: Trace/version envelope for deterministic lineage across logs + exports.
                var fallbackRunId = !string.IsNullOrWhiteSpace(_activeRunId) ? _activeRunId : Guid.NewGuid().ToString("N");
                var resolvedRunId = string.IsNullOrWhiteSpace(combined.Metadata.RunId) ? fallbackRunId : combined.Metadata.RunId;
                var appVersion = typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";

                combined.Trace.RunId = resolvedRunId;
                combined.Trace.TraceId = resolvedRunId;
                combined.ComponentVersions.Ps = string.IsNullOrWhiteSpace(combined.Metadata.Version)
                    ? PCDiagnosticPro.Models.SchemaRegistry.AppVersion
                    : combined.Metadata.Version;
                combined.ComponentVersions.Snapshot = string.IsNullOrWhiteSpace(diagnosticSnapshot.SchemaVersion)
                    ? PCDiagnosticPro.Models.SchemaRegistry.SnapshotSchemaVersion
                    : diagnosticSnapshot.SchemaVersion;
                combined.ComponentVersions.App = appVersion;
                combined.Metadata.RunId = resolvedRunId;
                App.LogMessage($"[Trace] RunId={combined.Trace.RunId} TraceId={combined.Trace.TraceId}");

                // Gate #2: combined contract check before export write.
                var combinedGate = TechnicalContractValidator.ValidateCombinedResult(combined, null, _contractGateOptions);
                var runStatus = ApplyGateResult(combinedGate, "combined_export_pre_write", null);
                combined.RunStatus = runStatus;

                var canonicalRunId = !string.IsNullOrWhiteSpace(_activeRunId) ? _activeRunId : resolvedRunId;
                if (!string.IsNullOrWhiteSpace(canonicalRunId))
                {
                    combined.Paths.CombinedJson = Services.ScanStorageService.GetCombinedJsonPath(canonicalRunId);
                }

                // JS-1: Compute SHA-256 integrity hash over the content without the integrity field,
                // then re-serialize with the hash included so readers can detect corruption.
                combined.Integrity = null;
                var jsonForHash = SerializeCombinedResult(combined);
                try
                {
                    var hashBytes = System.Security.Cryptography.SHA256.HashData(
                        Encoding.UTF8.GetBytes(jsonForHash));
                    combined.Integrity = new PCDiagnosticPro.Models.ScanIntegrity
                    {
                        ContentSha256 = Convert.ToHexString(hashBytes).ToLowerInvariant(),
                        ContentBytes = Encoding.UTF8.GetByteCount(jsonForHash)
                    };
                }
                catch (Exception intEx)
                {
                    App.LogMessage($"[JS-1] Integrity hash skipped: {intEx.Message}");
                }

                var combinedJson = SerializeCombinedResult(combined);
                string finalCombinedPath;
                if (!string.IsNullOrWhiteSpace(canonicalRunId))
                {
                    try
                    {
                        finalCombinedPath = await Services.ScanStorageService.SaveCombinedJsonAsync(canonicalRunId, combinedJson);
                        App.LogMessage($"Rapport combine genere (canonical): {finalCombinedPath} (schemaVersion={diagnosticSnapshot.SchemaVersion})");
                    }
                    catch (Exception canonicalEx)
                    {
                        App.LogMessage($"[History][RunId:{canonicalRunId}] Canonical combined save failed: {canonicalEx.Message}");
                        finalCombinedPath = Path.Combine(outputDir, "scan_result_combined.json");
                        await File.WriteAllTextAsync(finalCombinedPath, combinedJson, Encoding.UTF8);
                        App.LogMessage($"Rapport combine fallback write: {finalCombinedPath}");
                    }
                }
                else
                {
                    finalCombinedPath = Path.Combine(outputDir, "scan_result_combined.json");
                    await File.WriteAllTextAsync(finalCombinedPath, combinedJson, Encoding.UTF8);
                    App.LogMessage($"Rapport combine local write: {finalCombinedPath}");
                }

                _combinedJsonPath = finalCombinedPath;
                SetCombinedJsonContent(combinedJson, combined);
            }
            catch (Exception ex)
            {
                App.LogMessage($"Erreur création rapport combiné: {ex.Message}");
            }
        }

        private async Task GenerateUnifiedTxtReportAsync(string outputDir)
        {
            try
            {
                if (string.IsNullOrEmpty(_combinedJsonPath) || !File.Exists(_combinedJsonPath))
                {
                    App.LogMessage("[UnifiedTXT] JSON combiné introuvable, génération TXT annulée");
                    return;
                }

                // Trouver le TXT PowerShell original
                var originalTxtPath = Services.UnifiedReportBuilder.FindLatestPsTxtReport(outputDir);
                if (originalTxtPath == null)
                {
                    // Chercher aussi dans le dossier parent
                    var parentDir = Path.GetDirectoryName(outputDir);
                    if (parentDir != null)
                    {
                        originalTxtPath = Services.UnifiedReportBuilder.FindLatestPsTxtReport(parentDir);
                    }
                }
                
                App.LogMessage($"[UnifiedTXT] TXT PowerShell trouvé: {originalTxtPath ?? "AUCUN"}");

                // Chemin du TXT unifié final
                var unifiedTxtPath = Path.Combine(outputDir, $"Rapport_Unifie_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

                // Générer le rapport unifié
                var success = await Services.UnifiedReportBuilder.BuildUnifiedReportAsync(
                    _combinedJsonPath,
                    originalTxtPath,
                    unifiedTxtPath,
                    HealthReport);

                if (success)
                {
                    App.LogMessage($"[UnifiedTXT] ✅... Rapport unifié généré: {unifiedTxtPath}");
                    _lastUnifiedTxtPath = unifiedTxtPath;
                }
                else
                {
                    App.LogMessage("[UnifiedTXT] ❌ Échec génération rapport unifié");
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[UnifiedTXT] ERREUR: {ex.Message}");
            }
        }
        
        // Chemin du dernier TXT unifié généré
        private string _lastUnifiedTxtPath = string.Empty;

        private void UpdateScanItemsFromResult(ScanResult result)
        {
            ScanItems.Clear();
            foreach (var item in result.Items)
            {
                ScanItems.Add(item);
            }
        }

        private void UpdateResultSectionsFromResult(ScanResult result)
        {
            ResultSections.Clear();
            foreach (var section in result.Sections)
            {
                ResultSections.Add(section);
            }
            OnPropertyChanged(nameof(HasResultSections));
        }
    }
}

