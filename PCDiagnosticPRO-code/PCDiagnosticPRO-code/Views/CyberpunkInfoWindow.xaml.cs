using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.Services;

namespace PCDiagnosticPro.Views
{
    public partial class CyberpunkInfoWindow : Window
    {
        public CyberpunkInfoWindow(string title, IReadOnlyList<InfoLine> lines)
        {
            InitializeComponent();
            var normalizedTitle = TextEncodingNormalizer.Normalize(title);
            Title = normalizedTitle;
            TitleBlock.Text = normalizedTitle;

            var normalizedLines = (lines ?? Array.Empty<InfoLine>())
                .Select(line => new InfoLine
                {
                    Emoji = TextEncodingNormalizer.Normalize(line?.Emoji),
                    Label = TextEncodingNormalizer.Normalize(line?.Label),
                    Text = TextEncodingNormalizer.Normalize(line?.Text),
                    Tone = line?.Tone ?? InfoTone.Neutral
                })
                .ToArray();

            ContentLinesControl.ItemsSource = normalizedLines;
            SmartTemperatureRangesPanel.Visibility = IsSmartHealthContext(normalizedTitle, normalizedLines)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public CyberpunkInfoWindow(string title, string content)
            : this(title, ParseLegacyContent(content))
        {
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private static IReadOnlyList<InfoLine> ParseLegacyContent(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return Array.Empty<InfoLine>();

            var normalized = TextEncodingNormalizer
                .NormalizePreservingWhitespace(content)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');

            var rows = normalized
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            if (rows.Count == 0)
                return Array.Empty<InfoLine>();

            return rows
                .Select((row, index) => new InfoLine
                {
                    Emoji = index == 0 ? "🔧" : "📄",
                    Label = index == 0 ? string.Empty : "Détail",
                    Text = row,
                    Tone = index == 0 ? InfoTone.Info : InfoTone.Neutral
                })
                .ToArray();
        }

        private static bool IsSmartHealthContext(string title, IReadOnlyList<InfoLine> lines)
        {
            if (!string.IsNullOrWhiteSpace(title) &&
                title.IndexOf("SMART", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (lines == null || lines.Count == 0)
                return false;

            return lines.Any(line =>
                (line.Text?.IndexOf("SMART", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
        }
    }
}
