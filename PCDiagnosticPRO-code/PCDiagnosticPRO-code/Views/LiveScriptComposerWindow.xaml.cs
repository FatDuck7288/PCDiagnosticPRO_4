using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCDiagnosticPro.AI.Models;

namespace PCDiagnosticPro.Views
{
    public partial class LiveScriptComposerWindow : Window
    {
        private readonly AiRunReport _report;
        private readonly Queue<TypingStage> _typingStages = new();
        private readonly DispatcherTimer _typingTimer;
        private TypingStage? _currentStage;
        private int _currentStageIndex;

        public LiveScriptComposerWindow(AiRunReport report)
        {
            _report = report ?? throw new ArgumentNullException(nameof(report));
            InitializeComponent();
            _typingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(24) };
            _typingTimer.Tick += TypingTimer_Tick;
            Loaded += (_, _) => InitializeView();
            Closed += (_, _) => _typingTimer.Stop();
        }

        private void InitializeView()
        {
            MetaTextBlock.Text = $"TraceId: {_report.AiRunId}  |  RunId: {_report.RunId}";
            TimelineListView.ItemsSource = BuildTimeline(_report);

            var draftScript = _report.ScriptDraft?.ScriptText ?? string.Empty;
            var reviewScript = _report.ReviewResult?.RevisedScriptText ?? string.Empty;
            var refinedScript = _report.RefineResult?.RefinedScriptText ?? string.Empty;
            var finalScript = _report.FinalScript ?? string.Empty;

            var coderOutput = !string.IsNullOrWhiteSpace(draftScript)
                ? draftScript
                : ReadArtifactOrMissing("agent1_raw.txt");
            var reviewerDiff = BuildSimpleDiff(draftScript, reviewScript, "Reviewer");
            var refinerDiff = BuildSimpleDiff(reviewScript, refinedScript, "Refiner");
            var finalOutput = !string.IsNullOrWhiteSpace(finalScript)
                ? finalScript
                : ReadArtifactOrMissing("judge_input.txt");

            CoderOutputTextBox.Text = string.Empty;
            ReviewerDiffTextBox.Text = string.Empty;
            RefinerDiffTextBox.Text = string.Empty;
            FinalScriptTextBox.Text = string.Empty;

            _typingStages.Clear();
            _typingStages.Enqueue(new TypingStage(CoderOutputTextBox, coderOutput));
            _typingStages.Enqueue(new TypingStage(ReviewerDiffTextBox, reviewerDiff));
            _typingStages.Enqueue(new TypingStage(RefinerDiffTextBox, refinerDiff));
            _typingStages.Enqueue(new TypingStage(FinalScriptTextBox, finalOutput));

            _currentStage = null;
            _currentStageIndex = 0;
            _typingTimer.Start();
        }

        private void TypingTimer_Tick(object? sender, EventArgs e)
        {
            if (_currentStage == null)
            {
                if (_typingStages.Count == 0)
                {
                    _typingTimer.Stop();
                    return;
                }

                _currentStage = _typingStages.Dequeue();
                _currentStageIndex = 0;
            }

            var stage = _currentStage.Value;
            if (string.IsNullOrEmpty(stage.Text))
            {
                _currentStage = null;
                return;
            }

            const int chunkSize = 48;
            var remaining = stage.Text.Length - _currentStageIndex;
            var toCopy = Math.Min(chunkSize, remaining);
            if (toCopy > 0)
            {
                stage.Target.AppendText(stage.Text.Substring(_currentStageIndex, toCopy));
                stage.Target.ScrollToEnd();
                _currentStageIndex += toCopy;
            }

            if (_currentStageIndex >= stage.Text.Length)
            {
                _currentStage = null;
            }
        }

        private string ReadArtifactOrMissing(string fileName)
        {
            try
            {
                var dir = _report.AutoFixTraceDirectory ?? string.Empty;
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    return $"artifact manquant: dossier introuvable ({dir})";
                }

                var path = Path.Combine(dir, fileName);
                if (!File.Exists(path))
                {
                    return $"artifact manquant: {path}";
                }

                return File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                return $"artifact read error ({fileName}): {ex.Message}";
            }
        }

        private static List<TimelineItem> BuildTimeline(AiRunReport report)
        {
            var items = new List<TimelineItem>();
            foreach (var step in report.Steps)
            {
                var start = step.StartedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
                var end = step.CompletedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
                var duration = step.StartedAt.HasValue && step.CompletedAt.HasValue
                    ? $"{Math.Max(0, (step.CompletedAt.Value - step.StartedAt.Value).TotalSeconds):0.0}s"
                    : "-";

                items.Add(new TimelineItem
                {
                    Agent = step.AgentName,
                    Start = start,
                    End = end,
                    Duration = duration,
                    Status = step.Status.ToString()
                });
            }

            return items;
        }

        private static string BuildSimpleDiff(string before, string after, string stageName)
        {
            before ??= string.Empty;
            after ??= string.Empty;

            if (string.IsNullOrWhiteSpace(before) && string.IsNullOrWhiteSpace(after))
            {
                return $"[{stageName}] No content.";
            }

            if (string.Equals(before.Trim(), after.Trim(), StringComparison.Ordinal))
            {
                return $"[{stageName}] No differences.";
            }

            var beforeLines = before.Replace("\r\n", "\n").Split('\n');
            var afterLines = after.Replace("\r\n", "\n").Split('\n');
            var beforeSet = new HashSet<string>(beforeLines, StringComparer.Ordinal);
            var afterSet = new HashSet<string>(afterLines, StringComparer.Ordinal);

            var added = afterLines.Where(l => !beforeSet.Contains(l)).Take(200).ToList();
            var removed = beforeLines.Where(l => !afterSet.Contains(l)).Take(200).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"[{stageName}] +{added.Count} / -{removed.Count}");
            if (added.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Added:");
                foreach (var line in added)
                {
                    sb.AppendLine($"+ {line}");
                }
            }

            if (removed.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Removed:");
                foreach (var line in removed)
                {
                    sb.AppendLine($"- {line}");
                }
            }

            return sb.ToString().TrimEnd();
        }

        private void CopyFinalScriptButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(FinalScriptTextBox.Text ?? string.Empty);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[AI] LiveScriptComposer copy failed: {ex.Message}");
            }
        }

        private void OpenLogsFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = _report.AutoFixTraceDirectory ?? string.Empty;
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    MessageBox.Show("Logs folder not found.", "Live Script Composer", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                App.LogMessage($"[AI] LiveScriptComposer open logs failed: {ex.Message}");
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private readonly struct TypingStage
        {
            public TypingStage(TextBox target, string text)
            {
                Target = target;
                Text = text ?? string.Empty;
            }

            public TextBox Target { get; }
            public string Text { get; }
        }

        private sealed class TimelineItem
        {
            public string Agent { get; set; } = string.Empty;
            public string Start { get; set; } = string.Empty;
            public string End { get; set; } = string.Empty;
            public string Duration { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
        }
    }
}
