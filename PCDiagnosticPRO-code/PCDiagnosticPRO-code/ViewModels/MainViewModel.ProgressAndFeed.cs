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
        private void TickRainBits()
        {
            if (LiveFeedBackgroundBits.Count != 240) return;
            // Décaler d'une ligne vers le bas : lignes 1..11 deviennent 0..10, nouvelle ligne en 11
            var next = new List<string>();
            for (int i = 20; i < 240; i++)
                next.Add(LiveFeedBackgroundBits[i]);
            for (int i = 0; i < 20; i++)
                next.Add(_rainBitsRandom.Next(2) == 0 ? "0" : "1");
            LiveFeedBackgroundBits.Clear();
            foreach (var s in next)
                LiveFeedBackgroundBits.Add(s);
        }

        private void EmitAmbientFeedLine()
        {
            if (!IsScanning)
                return;

            // Préserver les messages "anciens" (réels) : ne compléter que pendant les silences du flux réel.
            if (_lastNonAmbientFeedAtUtc != DateTime.MinValue &&
                DateTime.UtcNow - _lastNonAmbientFeedAtUtc < TimeSpan.FromSeconds(1.8))
                return;

            var ambientSection = ResolveAmbientSection();
            var detail = PickAmbientDetail(ambientSection);
            if (string.IsNullOrWhiteSpace(detail))
                return;

            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            AddLiveFeedEntryCore(new LiveFeedEntry
            {
                EntryType = "STATUS",
                Section = ambientSection,
                Detail = detail,
                Timestamp = timestamp,
                RawMessage = $"[AMBIENT] {detail}",
                DisplayText = $"[{timestamp}] {ambientSection} - {detail}",
                IsAmbient = true
            });
        }

        private LiveFeedEntry ParseLiveFeedLine(string item)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var match = _structuredPattern.Match(item);

            if (match.Success)
            {
                var type = match.Groups["type"].Value;
                var section = match.Groups["section"].Value.Trim();
                var rest = match.Groups["rest"].Success ? match.Groups["rest"].Value.Trim() : "";

                var entry = new LiveFeedEntry
                {
                    EntryType = type,
                    Section = DecorateSectionLabel(section),
                    Detail = rest,
                    Timestamp = timestamp,
                    RawMessage = item,
                    DisplayText = $"[{timestamp}] {section} - {rest}"
                };

                // Parse PROGRESS specifics : "14/35 | 40%"
                if (type == "PROGRESS" && !string.IsNullOrEmpty(rest))
                {
                    var parts = rest.Split('|');
                    if (parts.Length >= 2)
                    {
                        var countPart = parts[0].Trim().Split('/');
                        if (countPart.Length == 2
                            && int.TryParse(countPart[0].Trim(), out int cur)
                            && int.TryParse(countPart[1].Trim(), out int tot))
                        {
                            entry.Current = cur;
                            entry.Total = tot;
                        }
                        var pctStr = parts[1].Trim().TrimEnd('%');
                        if (int.TryParse(pctStr.Trim(), out int pct))
                            entry.Percent = pct;
                    }
                    // Reformulate display for progress
                    entry.Detail = entry.Total > 0 ? $"{entry.Current}/{entry.Total}" : rest;
                }

                return entry;
            }

            // Fallback : message non-structuré (phases C#, speed test, etc.)
            var level = InferLiveFeedLevel(item);
            var fallbackType = level == "Error" ? "ERROR" : level == "Warning" ? "WARN" : "INFO";
            // Tenter d'extraire une icône/section des emojis existants
            var cleanItem = item;
            var fallbackSection = "";
            if (item.Length > 2 && (item[0] > 0xFF || item.StartsWith("📍") || item.StartsWith("🌐") || item.StartsWith("✅") || item.StartsWith("❌") || item.StartsWith("⚠") || item.StartsWith("↑") || item.StartsWith("⏹")))
            {
                // L'icône sert de section visuelle
                var spaceIdx = item.IndexOf(' ');
                if (spaceIdx > 0 && spaceIdx < 5)
                {
                    fallbackSection = item.Substring(0, spaceIdx).Trim();
                    cleanItem = item.Substring(spaceIdx + 1).Trim();
                }
            }

            return new LiveFeedEntry
            {
                EntryType = fallbackType,
                Section = fallbackSection,
                Detail = cleanItem,
                Timestamp = timestamp,
                DisplayText = $"[{timestamp}] {item}",
                RawMessage = item
            };
        }

        private void AddLiveFeedItem(string item)
        {
            EncodingCorruptionWatcher.CheckAndLog(item, "livefeed.item");
            // Filtrer les lignes de progression ASCII type [#### 16%]
            if (_progressBarPattern.IsMatch(item))
                return;
            if (item.StartsWith("PROGRESS|", StringComparison.OrdinalIgnoreCase) ||
                item.StartsWith("LIVE|", StringComparison.OrdinalIgnoreCase))
                return;

            // Filtrer les lignes vides ou juste des espaces
            if (string.IsNullOrWhiteSpace(item))
                return;

            if (Application.Current?.Dispatcher.CheckAccess() == false)
            {
                Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => AddLiveFeedItem(item)));
                return;
            }

            var entry = ParseLiveFeedLine(item);
            AddLiveFeedEntryCore(entry);
        }

        private void AddLiveFeedItems(IReadOnlyList<string> items)
        {
            if (items.Count == 0)
                return;

            if (Application.Current?.Dispatcher.CheckAccess() == false)
            {
                Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => AddLiveFeedItems(items)));
                return;
            }

            var coalesced = new List<string>(items.Count);
            string? lastLine = null;
            var repeatCount = 0;

            foreach (var item in items)
            {
                var normalized = TextEncodingNormalizer.NormalizeIfCorrupted(item);
                if (_progressBarPattern.IsMatch(normalized) ||
                    normalized.StartsWith("PROGRESS|", StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith("LIVE|", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(normalized))
                    continue;

                if (string.Equals(lastLine, normalized, StringComparison.Ordinal))
                {
                    repeatCount++;
                    continue;
                }

                if (lastLine != null)
                {
                    coalesced.Add(repeatCount > 1 ? $"{lastLine} (x{repeatCount})" : lastLine);
                }

                lastLine = normalized;
                repeatCount = 1;
            }

            if (lastLine != null)
            {
                coalesced.Add(repeatCount > 1 ? $"{lastLine} (x{repeatCount})" : lastLine);
            }

            using (_filteredLiveFeedView?.DeferRefresh())
            {
                foreach (var normalized in coalesced)
                {
                    EncodingCorruptionWatcher.CheckAndLog(normalized, "livefeed.batch");
                    AddLiveFeedEntryCore(ParseLiveFeedLine(normalized));
                }
            }
        }

        private void AddLiveFeedEntryCore(LiveFeedEntry entry)
        {
            if (!entry.IsAmbient)
                _lastNonAmbientFeedAtUtc = DateTime.UtcNow;

            LiveFeedItems.Insert(0, entry.DisplayText);
            while (LiveFeedItems.Count > 100)
                LiveFeedItems.RemoveAt(LiveFeedItems.Count - 1);

            LiveFeedEntries.Insert(0, entry);
            while (LiveFeedEntries.Count > 200)
                LiveFeedEntries.RemoveAt(LiveFeedEntries.Count - 1);
        }

        private void SetCombinedJsonContent(string? jsonContent, CombinedScanResult? combined = null)
        {
            _lastCombinedJsonContent = jsonContent;
            _lastCombinedResult = combined;
            _lastRunStatus = combined?.RunStatus;

            if (_lastRunStatus != null)
                UpdateGateBanner(_lastRunStatus, "combined_cache_update");
            else if (string.IsNullOrWhiteSpace(jsonContent))
            {
                ContractGateBannerText = string.Empty;
                ContractGateBannerDetails = string.Empty;
            }

            lock (_combinedJsonCacheLock)
            {
                _combinedJsonDocumentCache?.Dispose();
                _combinedJsonDocumentCache = null;
                _combinedJsonDocumentCacheContent = null;
            }
        }

        private bool TryGetCombinedJsonRoot(out JsonElement root)
        {
            root = default;
            var content = _lastCombinedJsonContent;
            if (string.IsNullOrWhiteSpace(content))
                return false;

            lock (_combinedJsonCacheLock)
            {
                if (_combinedJsonDocumentCache == null || !string.Equals(_combinedJsonDocumentCacheContent, content, StringComparison.Ordinal))
                {
                    _combinedJsonDocumentCache?.Dispose();
                    _combinedJsonDocumentCache = JsonDocument.Parse(content);
                    _combinedJsonDocumentCacheContent = content;
                }

                root = _combinedJsonDocumentCache.RootElement;
                return true;
            }
        }

        private static string InferLiveFeedLevel(string message)
        {
            if (string.IsNullOrEmpty(message)) return "Info";
            var m = message.ToUpperInvariant();
            if (m.Contains("ERROR") || m.Contains("ERREUR") || m.Contains("EXCEPTION") || m.Contains("ÉCHEC")) return "Error";
            if (m.Contains("WARN") || m.Contains("ATTENTION") || m.Contains("⚠")) return "Warning";
            return "Info";
        }
        
        // Weighted progress model (total = 100):
        // 0 PowerShell total (collectors + dynamic + advanced): 79
        // 1 Sensors: 3
        // 2 Counters: 3
        // 3 Signals: 3
        // 4 Process telemetry: 2
        // 5 Network diagnostics: 2
        // 6 Report build/persist: 8
        private static readonly int[] PhaseWeights = { 79, 3, 3, 3, 2, 2, 8 };
        private const int TOTAL_PHASES = 7;
        
        /// <summary>
        /// Get progress percentage for a completed phase (0-6)
        /// Phase 0 done = 14%, Phase 1 done = 28%, ..., Phase 6 done = 100%
        /// </summary>
        private int GetProgressForCompletedPhase(int phaseIndex)
        {
            var clamped = Math.Max(0, Math.Min(TOTAL_PHASES - 1, phaseIndex));
            var sum = 0;
            for (var i = 0; i <= clamped; i++)
            {
                sum += PhaseWeights[i];
            }
            return Math.Min(100, sum);
        }
        
        /// <summary>
        /// Get progress percentage for a phase in progress (partial)
        /// </summary>
        private int GetProgressForPhaseInProgress(int phaseIndex, double internalProgress = 0.5)
        {
            var clampedIndex = Math.Max(0, Math.Min(TOTAL_PHASES - 1, phaseIndex));
            var normalizedInternal = Math.Max(0d, Math.Min(1d, internalProgress));

            var baseProgress = 0;
            for (var i = 0; i < clampedIndex; i++)
            {
                baseProgress += PhaseWeights[i];
            }

            var contribution = (int)Math.Round(PhaseWeights[clampedIndex] * normalizedInternal);
            return Math.Min(100, baseProgress + contribution);
        }
        
        private void InitializeSectionPhases()
        {
            SectionPhases.Clear();
            // Labels français selon le modèle visuel de référence
            var phaseLabels = new[] { 
                "Inventaire système",
                "Capteurs & températures", 
                "Performances temps réel",
                "Stabilité & intégrité",
                "Analyse processus",
                "Connectivité réseau",
                "Génération rapport"
            };
            foreach (var label in phaseLabels)
            {
                SectionPhases.Add(new SectionPhaseItem { Label = label, Status = "Pending" });
            }
        }
        
        private void SetSectionPhase(int index, string status)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (index >= 0 && index < SectionPhases.Count)
                {
                    SectionPhases[index].Status = status;
                }
            });
        }

        private void UpdateElapsedTime()
        {
            ElapsedTime = _scanStopwatch.Elapsed.ToString(@"mm\:ss");
        }

        private void UpdateProgress(int percent, string reason, bool allowDecrease = false)
        {
            var normalized = Math.Max(0, Math.Min(100, percent));
            if (!allowDecrease && normalized < ProgressPercent)
            {
                App.LogMessage($"Progress update ignored ({normalized}% < {ProgressPercent}%): {reason}");
                return;
            }

            Progress = normalized;
            ProgressPercent = normalized;

            // Smooth progression: advance the ceiling so the timer eases towards it
            // rather than jumping SmoothProgressPercent directly (which causes visible jolts).
            // Exception: allowDecrease (reset/cancel) resets immediately.
            if (allowDecrease)
            {
                SmoothProgressPercent = normalized;
                _scanProgressCeiling = normalized;
            }
            else
            {
                // Only raise the ceiling; the TickScanProgress timer does the smooth easing.
                _scanProgressCeiling = Math.Max(_scanProgressCeiling, normalized);
            }

            // Ne pas écraser la section courante par le timer : garder la vraie section (PowerShell ou C#).
            if (reason != "Progression timer")
            {
                CurrentSection = TextEncodingNormalizer.NormalizeIfCorrupted(reason);
                OnPropertyChanged(nameof(CurrentSection));
                OnPropertyChanged(nameof(CurrentSectionDisplay));
            }
            App.LogMessage($"Progress update: {ProgressPercent}% (ceiling={_scanProgressCeiling}) - {reason}");
        }

        /// <summary>Met à jour uniquement le pourcentage de progression (pour le timer), sans toucher à la section courante.</summary>
        private void SetProgressPercentOnly(int percent)
        {
            var normalized = Math.Max(0, Math.Min(100, percent));
            if (normalized < ProgressPercent) return;
            Progress = normalized;
            ProgressPercent = normalized;
            // Synchroniser la valeur lissée
            if (normalized > _smoothProgressPercent)
            {
                SmoothProgressPercent = normalized;
            }
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(ProgressPercent));
        }

        private void StartScanProgressTimer(int ceiling)
        {
            _scanProgressCeiling = Math.Max(0, Math.Min(100, ceiling));
            _scanProgressTimer.Start();
        }

        /// <summary>
        /// Met à jour le plafond de progression pour la phase en cours (timer continue, progression graduelle).
        /// </summary>
        private void UpdateScanProgressCeiling(int newCeiling)
        {
            _scanProgressCeiling = Math.Max(ProgressPercent, Math.Min(100, newCeiling));
        }

        private void StopScanProgressTimer()
        {
            _scanProgressTimer.Stop();
            IsScanProgressIndeterminate = false; // Scan finished — back to determinate at 100%
        }

        private void TickScanProgress()
        {
            if (!IsScanning)
            {
                return;
            }

            // Honest progress: when markers are missing, stay indeterminate and avoid fabricated % jumps.
            if (IsScanProgressIndeterminate)
            {
                return;
            }

            var ceiling = (double)_scanProgressCeiling;
            var remaining = ceiling - _smoothProgressPercent;

            // Si on est déjà au plafond (ou très proche), rien à faire
            if (remaining <= 0.01)
            {
                return;
            }

            // Easing exponentiel : on avance d'un pourcentage de la distance restante
            var increment = Math.Max(SmoothMinIncrement, remaining * SmoothEasingFactor);
            var newSmooth = Math.Min(ceiling, _smoothProgressPercent + increment);
            SmoothProgressPercent = newSmooth;

            // Mettre à jour ProgressPercent (int) quand on franchit un entier
            var newInt = (int)Math.Floor(newSmooth);
            if (newInt > ProgressPercent && newInt <= _scanProgressCeiling)
            {
                SetProgressPercentOnly(newInt);
            }
        }

        private void AttachScanProgressEngine()
        {
            _scanProgressEngine.PhaseChanged += HandleScanProgressPhaseChanged;
            _scanProgressEngine.StepChanged += HandleScanProgressStepChanged;
            _scanProgressEngine.ProgressChanged += HandleScanProgressChanged;
        }

        private void ResetScanProgressEngine()
        {
            _scanProgressEngine.Reset();
            _scanProgressEngine.BeginPhase(
                ScanProgressPhase.PowerShellScan,
                GetString("PhaseLabel_PowerShell"),
                GetString("ScanStatus_Preparation"),
                indeterminate: true);
        }

        private void BeginScanProgressPhase(ScanProgressPhase phase, string section, string? message = null, bool indeterminate = false)
        {
            _scanProgressEngine.BeginPhase(phase, section, message, indeterminate);
        }

        private void ReportScanProgressStep(
            string? section = null,
            string? message = null,
            int? done = null,
            int? total = null,
            int? explicitPercent = null,
            bool? indeterminate = null)
        {
            _scanProgressEngine.ReportStep(section, message, done, total, explicitPercent, indeterminate);
        }

        private void CompleteScanProgressPhase(ScanProgressPhase phase, string? message = null)
        {
            _scanProgressEngine.CompletePhase(phase, message);
            var n = (int)phase;
            var pct = GetProgressForCompletedPhase(n);
            App.LogMessage($"[Progress] Phase {n} '{phase}' complete, progress={pct}%");
        }

        private void HandleScanProgressPhaseChanged(ProgressPhaseState state)
        {
            App.LogMessage($"[ProgressEngine] phase={state.Phase} weighted={state.WeightedPercent}% indeterminate={state.IsIndeterminate}");
        }

        private void HandleScanProgressStepChanged(ProgressPhaseState state)
        {
            if (Application.Current?.Dispatcher.CheckAccess() == false)
            {
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => HandleScanProgressStepChanged(state)));
                return;
            }

            if (!string.IsNullOrWhiteSpace(state.Message))
                CurrentStep = TextEncodingNormalizer.NormalizeIfCorrupted(state.Message);

            if (!string.IsNullOrWhiteSpace(state.Section))
            {
                CurrentSection = TextEncodingNormalizer.NormalizeIfCorrupted(state.Section);
                OnPropertyChanged(nameof(CurrentSectionDisplay));
            }
        }

        private void HandleScanProgressChanged(ProgressPhaseState state)
        {
            if (Application.Current?.Dispatcher.CheckAccess() == false)
            {
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => HandleScanProgressChanged(state)));
                return;
            }

            IsScanProgressIndeterminate = state.IsIndeterminate;
            var reason = !string.IsNullOrWhiteSpace(state.Section)
                ? TextEncodingNormalizer.NormalizeIfCorrupted(state.Section)
                : GetString("ScanStatus_Preparation");

            if (state.IsIndeterminate)
            {
                UpdateScanProgressCeiling(state.WeightedPercent);
                if (state.WeightedPercent > ProgressPercent)
                    SetProgressPercentOnly(state.WeightedPercent);
                return;
            }

            UpdateProgress(state.WeightedPercent, reason, allowDecrease: true);
            UpdateScanProgressCeiling(state.WeightedPercent);
        }
    }
}
