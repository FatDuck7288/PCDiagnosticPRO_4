using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.ViewModels;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Repairs common mojibake patterns when UTF-8 text was decoded with a legacy code page.
    /// Safe to call on already-correct text.
    /// </summary>
    public static class TextEncodingNormalizer
    {
        private static readonly Encoding Win1252 = GetEncodingOrUtf8(1252);
        private static readonly Encoding Latin1 = GetEncodingOrUtf8("ISO-8859-1");
        private static readonly Regex MultiSpaces = new(@"\s{2,}", RegexOptions.Compiled);

        private static readonly string[] CorruptionMarkers =
        {
            "�",
            "Ã",
            "Â",
            "â€™",
            "â€œ",
            "â€",
            "â€“",
            "â€”",
            "â€¦",
            "ðŸ",
            "ï¿½"
        };

        // Fast-path replacements for recurring mojibake fragments in UI strings.
        private static readonly (string Bad, string Good)[] CommonFixes =
        {
            ("Ã©", "é"),
            ("Ã¨", "è"),
            ("Ãª", "ê"),
            ("Ã«", "ë"),
            ("Ã ", "à"),
            ("Ã¢", "â"),
            ("Ã®", "î"),
            ("Ã¯", "ï"),
            ("Ã´", "ô"),
            ("Ã¶", "ö"),
            ("Ã¹", "ù"),
            ("Ã»", "û"),
            ("Ã¼", "ü"),
            ("Ã§", "ç"),
            ("Ã‰", "É"),
            ("Ã€", "À"),
            ("Ã‡", "Ç"),
            ("â€™", "'"),
            ("â€œ", "\""),
            ("â€", "\""),
            ("â€“", "-"),
            ("â€”", "-"),
            ("â€¦", "..."),
            ("â†", "←"),
            ("â†’", "→"),
            ("â†‘", "↑"),
            ("â†“", "↓"),
            ("â†", "→"),
            ("ðŸ”§", "🔧"),
            ("ðŸ“„", "📄"),
            ("ðŸ’¡", "💡"),
            ("ðŸ› ", "🛠"),
            ("âš ï¸", "⚠️"),
            ("âš ï¸", "⚠️"),
            ("âš ", "⚠️"),
            ("Â°", "°"),
            ("Â«", "«"),
            ("Â»", "»"),
            ("Â:", ":"),
            ("Â;", ";"),
            ("Â%", "%"),
            ("Â", ""),
            ("SystÃ¨me", "Système"),
            ("MÃ©moire", "Mémoire"),
            ("RÃ©seau", "Réseau"),
            ("SÃ©curitÃ©", "Sécurité"),
            ("TempÃ©rature", "Température"),
            ("DÃ©marrage", "Démarrage"),
            ("donnÃ©es", "données"),
            ("intÃ©gritÃ©", "intégrité"),
            ("mÃ©tadonnÃ©es", "métadonnées"),
            ("gÃ©nÃ©ration", "génération")
        };

        static TextEncodingNormalizer()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public static string Normalize(string? input) => NormalizeCore(input, preserveWhitespace: false);

        /// <summary>
        /// Normalizes only when corruption markers are detected.
        /// Keeps already-valid text untouched to avoid repeated conversions.
        /// </summary>
        public static string NormalizeIfCorrupted(string? input)
        {
            if (string.IsNullOrEmpty(input))
                return input ?? string.Empty;

            return LooksCorrupted(input) ? Normalize(input) : input!;
        }

        /// <summary>
        /// Variant intended for source-file normalization where whitespace must remain untouched.
        /// </summary>
        public static string NormalizePreservingWhitespace(string? input) => NormalizeCore(input, preserveWhitespace: true);

        /// <summary>
        /// Returns a user-facing value without exposing internal unavailable reason codes.
        /// </summary>
        public static string ToUserFacingValue(string? input)
        {
            var normalized = Normalize(input);
            if (string.IsNullOrWhiteSpace(normalized))
                return normalized;

            if (normalized.StartsWith("Indisponible (", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("NotProvidedBy", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("unavailable_", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("sentinel_", StringComparison.OrdinalIgnoreCase))
            {
                return "Indisponible";
            }

            return normalized;
        }

        private static Encoding GetEncodingOrUtf8(int codePage)
        {
            try { return Encoding.GetEncoding(codePage); }
            catch { return Encoding.UTF8; }
        }

        private static Encoding GetEncodingOrUtf8(string name)
        {
            try { return Encoding.GetEncoding(name); }
            catch { return Encoding.UTF8; }
        }

        private static string NormalizeCore(string? input, bool preserveWhitespace)
        {
            if (string.IsNullOrEmpty(input))
                return input ?? string.Empty;

            var current = input!;

            // Limit passes to avoid over-normalizing already clean strings.
            for (var i = 0; i < 3; i++)
            {
                var repaired = ApplyCommonFixes(TryRepair(current), preserveWhitespace);
                if (string.Equals(repaired, current, StringComparison.Ordinal))
                    break;

                current = repaired;
            }

            return ApplyCommonFixes(current, preserveWhitespace);
        }

        public static void NormalizeHealthReport(HealthReport report)
        {
            report.GlobalMessage = Normalize(report.GlobalMessage);
            report.Grade = Normalize(report.Grade);
            report.CollectionStatus = Normalize(report.CollectionStatus);
            report.MissingData = report.MissingData.Select(Normalize).ToList();

            foreach (var rec in report.Recommendations)
            {
                rec.Title = Normalize(rec.Title);
                rec.Description = Normalize(rec.Description);
                rec.ActionText = Normalize(rec.ActionText);
            }

            foreach (var section in report.Sections)
            {
                section.DisplayName = Normalize(section.DisplayName);
                section.Icon = Normalize(section.Icon);
                section.StatusMessage = Normalize(section.StatusMessage);
                section.DetailedExplanation = Normalize(section.DetailedExplanation);
                section.ScoreUnavailableReason = Normalize(section.ScoreUnavailableReason);
                section.PerformanceCategory = Normalize(section.PerformanceCategory);
                section.PrimaryBottleneck = Normalize(section.PrimaryBottleneck);
                section.RealisticSummary = Normalize(section.RealisticSummary);
                section.PerformanceCpuDisplay = Normalize(section.PerformanceCpuDisplay);
                section.PerformanceGpuDisplay = Normalize(section.PerformanceGpuDisplay);
                section.PerformanceVramDisplay = Normalize(section.PerformanceVramDisplay);
                section.PerformanceRamDisplay = Normalize(section.PerformanceRamDisplay);
                section.PerformanceStorageDisplay = Normalize(section.PerformanceStorageDisplay);
                section.CollectionStatus = Normalize(section.CollectionStatus);

                section.SectionRecommendations = section.SectionRecommendations.Select(Normalize).ToList();

                var normalizedEvidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in section.EvidenceData)
                    normalizedEvidence[Normalize(kvp.Key)] = Normalize(kvp.Value);
                section.EvidenceData = normalizedEvidence;

                var normalizedTooltips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in section.EvidenceTooltips)
                    normalizedTooltips[Normalize(kvp.Key)] = NormalizePreservingWhitespace(kvp.Value);
                section.EvidenceTooltips = normalizedTooltips;

                foreach (var d in section.ScoreDeductions)
                {
                    d.RuleId = Normalize(d.RuleId);
                    d.Reason = NormalizePreservingWhitespace(d.Reason);
                    d.SourceMetric = Normalize(d.SourceMetric);
                    d.Confidence = Normalize(d.Confidence);
                }

                foreach (var f in section.Findings)
                {
                    f.Title = Normalize(f.Title);
                    f.Description = Normalize(f.Description);
                    f.Source = Normalize(f.Source);
                }

                foreach (var row in section.PerformanceMarketRows ?? new List<PerformanceMarketRow>())
                {
                    if (row == null) continue;
                    row.Component = Normalize(row.Component);
                    row.DetectedModel = Normalize(row.DetectedModel);
                    row.BenchmarkScoreDisplay = Normalize(row.BenchmarkScoreDisplay);
                    row.PercentileDisplay = Normalize(row.PercentileDisplay);
                    row.RankDisplay = Normalize(row.RankDisplay);
                    row.Source = Normalize(row.Source);
                    row.ConfidenceDisplay = Normalize(row.ConfidenceDisplay);
                }
            }

            if (report.UdisReport != null)
            {
                report.UdisReport.Message = Normalize(report.UdisReport.Message);
                report.UdisReport.FailCloseReasonCode = Normalize(report.UdisReport.FailCloseReasonCode);
                report.UdisReport.FailCloseUserMessage = Normalize(report.UdisReport.FailCloseUserMessage);
                report.UdisReport.FailCloseImpact = Normalize(report.UdisReport.FailCloseImpact);
                report.UdisReport.FailCloseAction = Normalize(report.UdisReport.FailCloseAction);
            }
        }

        public static void NormalizeFullReportViewModel(FullReportViewModel vm)
        {
            vm.Title = Normalize(vm.Title);
            vm.RunId = Normalize(vm.RunId);
            vm.Status = Normalize(vm.Status);

            foreach (var section in vm.Sections)
            {
                section.Id = Normalize(section.Id);
                section.Title = Normalize(section.Title);
                section.SummaryLine1 = Normalize(section.SummaryLine1);
                section.SummaryLine2 = Normalize(section.SummaryLine2);
                section.EvidenceText = Normalize(section.EvidenceText);
                section.SectionHeaderBadge = Normalize(section.SectionHeaderBadge);
                section.SectionHeaderBadgeDetail = Normalize(section.SectionHeaderBadgeDetail);

                foreach (var kv in section.KeyValues)
                {
                    kv.Key = Normalize(kv.Key);
                    kv.Value = Normalize(kv.Value);
                    kv.Unit = Normalize(kv.Unit);
                    kv.Provenance = Normalize(kv.Provenance);
                    kv.JsonPath = Normalize(kv.JsonPath);
                    kv.Reason = Normalize(kv.Reason);
                    kv.Confidence = Normalize(kv.Confidence);
                }

                foreach (var issue in section.Issues)
                {
                    issue.Message = Normalize(issue.Message);
                    issue.Code = Normalize(issue.Code);
                    issue.Source = Normalize(issue.Source);
                }

                foreach (var row in section.ServicesStartupTaskRows)
                {
                    row.Category = Normalize(row.Category);
                    row.Metric = Normalize(row.Metric);
                    row.Value = Normalize(row.Value);
                    row.Source = Normalize(row.Source);
                }
            }
        }

        public static bool LooksCorrupted(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            if (text!.Contains('\uFFFD'))
                return true;

            foreach (var marker in CorruptionMarkers)
            {
                if (text.Contains(marker, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static string TryRepair(string input)
        {
            if (!LooksCorrupted(input))
                return input;

            var candidates = new HashSet<string>(StringComparer.Ordinal)
            {
                input,
                DecodeWith(input, Win1252),
                DecodeWith(input, Latin1)
            };

            // Extra pass for double-encoded text.
            var win1252Decoded = DecodeWith(input, Win1252);
            var latin1Decoded = DecodeWith(input, Latin1);
            candidates.Add(DecodeWith(win1252Decoded, Win1252));
            candidates.Add(DecodeWith(win1252Decoded, Latin1));
            candidates.Add(DecodeWith(latin1Decoded, Win1252));
            candidates.Add(DecodeWith(latin1Decoded, Latin1));

            return candidates
                .Select(c => ApplyCommonFixes(c, preserveWhitespace: true))
                .OrderByDescending(Score)
                .ThenBy(s => s.Length)
                .First();
        }

        private static string DecodeWith(string text, Encoding sourceEncoding)
        {
            try
            {
                var bytes = sourceEncoding.GetBytes(text);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return text;
            }
        }

        private static string ApplyCommonFixes(string text, bool preserveWhitespace)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var fixedText = text;
            foreach (var (bad, good) in CommonFixes)
                fixedText = fixedText.Replace(bad, good, StringComparison.Ordinal);

            if (!preserveWhitespace)
            {
                fixedText = fixedText.Replace('\uFFFD', ' ').Trim();
                fixedText = MultiSpaces.Replace(fixedText, " ");
            }

            return fixedText;
        }

        private static int Score(string text)
        {
            var score = 0;

            foreach (var marker in CorruptionMarkers)
            {
                if (text.Contains(marker, StringComparison.Ordinal))
                    score -= 8;
            }

            if (text.Contains('\uFFFD'))
                score -= 10;

            if (text.IndexOfAny(new[] { 'é', 'è', 'ê', 'ë', 'à', 'â', 'î', 'ï', 'ô', 'ö', 'ù', 'û', 'ü', 'ç', 'É', 'À', 'Ç' }) >= 0)
                score += 6;

            if (text.Contains("Système", StringComparison.OrdinalIgnoreCase)) score += 4;
            if (text.Contains("Mémoire", StringComparison.OrdinalIgnoreCase)) score += 4;
            if (text.Contains("Réseau", StringComparison.OrdinalIgnoreCase)) score += 4;
            if (text.Contains("Température", StringComparison.OrdinalIgnoreCase)) score += 4;
            if (text.Contains("Sécurité", StringComparison.OrdinalIgnoreCase)) score += 4;
            if (text.Contains("Redémarrage", StringComparison.OrdinalIgnoreCase)) score += 4;

            // Prefer readable words over noisy symbol-heavy text.
            var words = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0)
            {
                var letterDensity = words.Sum(w => w.Count(char.IsLetter));
                score += Math.Min(6, letterDensity / 20);
            }

            if (text.Any(ch => char.GetUnicodeCategory(ch) == UnicodeCategory.OtherSymbol))
                score -= 1;

            return score;
        }
    }

    /// <summary>
    /// Lightweight runtime detector for UI-bound string corruption.
    /// Logs once per source/value fingerprint to avoid noise.
    /// </summary>
    public static class EncodingCorruptionWatcher
    {
        private static readonly ConcurrentDictionary<string, byte> Seen = new(StringComparer.OrdinalIgnoreCase);
        private static bool IsEnabled =>
            Debugger.IsAttached ||
            string.Equals(Environment.GetEnvironmentVariable("PCDIAG_ENCODING_WATCH"), "1", StringComparison.OrdinalIgnoreCase);

        public static void CheckAndLog(string? text, string source)
        {
            if (!IsEnabled)
                return;

            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(source))
                return;

            if (!TextEncodingNormalizer.LooksCorrupted(text))
                return;

            var normalized = TextEncodingNormalizer.Normalize(text);
            if (!TextEncodingNormalizer.LooksCorrupted(normalized))
                return;

            var fingerprint = $"{source}:{normalized}";
            if (!Seen.TryAdd(fingerprint, 0))
                return;

            App.LogMessage($"[EncodingWarning] source={source} value=\"{Shorten(normalized)}\"");
        }

        private static string Shorten(string value, int max = 160)
        {
            if (value.Length <= max)
                return value;

            return value.Substring(0, max - 3) + "...";
        }
    }
}
