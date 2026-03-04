using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PCDiagnosticPro.Services
{
    public sealed class EncodingAuditFinding
    {
        public string FilePath { get; init; } = string.Empty;
        public int LineNumber { get; init; }
        public string Marker { get; init; } = string.Empty;
        public string Context { get; init; } = string.Empty;
    }

    public sealed class EncodingAuditReport
    {
        public string RootPath { get; init; } = string.Empty;
        public int FilesScanned { get; set; }
        public int FindingsCount => Findings.Count;
        public List<EncodingAuditFinding> Findings { get; } = new();
    }

    public sealed class EncodingNormalizationResult
    {
        public int FilesScanned { get; set; }
        public int FilesRewritten { get; set; }
        public int MojibakeRepairedFiles { get; set; }
        public int EncodingConvertedFiles { get; set; }
        public List<string> RewrittenFiles { get; } = new();
    }

    public static class EncodingAuditService
    {
        private static readonly HashSet<string> TargetExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".xaml",
            ".resx",
            ".ps1",
            ".json",
            ".txt"
        };

        private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".vs",
            "bin",
            "obj",
            "TestResults",
            "node_modules"
        };

        private static readonly string[] MarkerTokens =
        {
            "\uFFFD", // Replacement char
            "\u00C3",                // Ã
            "\u00C2",                // Â
            "\u00F0\u0178",          // ðŸ
            "\u00EF\u00BF\u00BD",    // ï¿½ (mojibake form of replacement char)
            "\u00E2\u20AC",          // â€
            "\u00E2\u20AC\u2122",    // â€™
            "\u00E2\u20AC\u0153",    // â€œ
            "\u00E2\u20AC\u009D"     // â€�
        };

        private static readonly HashSet<string> IntentionalMarkerRelativePaths = new(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("Services", "TextEncodingNormalizer.cs"),
            Path.Combine("Services", "EncodingAuditService.cs"),
            Path.Combine("Tests", "EncodingNormalizationTests.cs")
        };

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly Encoding Utf8Bom = new UTF8Encoding(true);
        private static readonly Encoding Latin1 = Encoding.Latin1;
        private static readonly Lazy<Encoding> Win1252Lazy = new(() =>
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252);
        });

        private static Encoding Win1252 => Win1252Lazy.Value;

        public static EncodingAuditReport ScanRepository(
            string repositoryRoot,
            bool includeTests = false,
            bool includeIntentional = false)
        {
            var fullRoot = Path.GetFullPath(repositoryRoot);
            var report = new EncodingAuditReport { RootPath = fullRoot };

            foreach (var file in EnumerateTargetFiles(fullRoot, includeTests))
            {
                if (!includeIntentional && IsIntentionalMarkerFile(fullRoot, file))
                    continue;

                ScanFileIntoReport(fullRoot, file, report);
            }

            return report;
        }

        public static EncodingAuditReport ScanSpecificFiles(
            string repositoryRoot,
            IEnumerable<string> filePaths,
            bool includeIntentional = false)
        {
            var fullRoot = Path.GetFullPath(repositoryRoot);
            var report = new EncodingAuditReport { RootPath = fullRoot };

            foreach (var file in filePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                    continue;

                if (!HasTargetExtension(file))
                    continue;

                if (!includeIntentional && IsIntentionalMarkerFile(fullRoot, file))
                    continue;

                ScanFileIntoReport(fullRoot, Path.GetFullPath(file), report);
            }

            return report;
        }

        public static EncodingNormalizationResult NormalizeRepositoryFilesToUtf8(
            string repositoryRoot,
            bool includeTests = false)
        {
            var fullRoot = Path.GetFullPath(repositoryRoot);
            var result = new EncodingNormalizationResult();

            foreach (var file in EnumerateTargetFiles(fullRoot, includeTests))
            {
                result.FilesScanned++;

                // Keep intentional corruption test vectors untouched.
                if (IsIntentionalMarkerFile(fullRoot, file))
                    continue;

                var decoded = ReadTextWithDetection(file);
                var repaired = RepairTextForSource(decoded.Text);

                var targetHasBom = ShouldUseUtf8Bom(file);
                var targetEncoding = targetHasBom ? Utf8Bom : Utf8NoBom;

                var contentChanged = !string.Equals(decoded.Text, repaired, StringComparison.Ordinal);
                var encodingChanged = !decoded.IsUtf8 || decoded.HasUtf8Bom != targetHasBom;

                if (!contentChanged && !encodingChanged)
                    continue;

                File.WriteAllText(file, repaired, targetEncoding);
                result.FilesRewritten++;
                if (contentChanged) result.MojibakeRepairedFiles++;
                if (encodingChanged) result.EncodingConvertedFiles++;
                result.RewrittenFiles.Add(Path.GetRelativePath(fullRoot, file));
            }

            return result;
        }

        public static string WriteReport(
            EncodingAuditReport report,
            string reportPath,
            EncodingNormalizationResult? normalization = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Encoding Audit Report ===");
            sb.AppendLine($"GeneratedUtc: {DateTime.UtcNow:O}");
            sb.AppendLine($"RootPath: {report.RootPath}");
            sb.AppendLine($"FilesScanned: {report.FilesScanned}");
            sb.AppendLine($"Findings: {report.FindingsCount}");

            if (normalization != null)
            {
                sb.AppendLine();
                sb.AppendLine("=== Normalization Pass ===");
                sb.AppendLine($"FilesScanned: {normalization.FilesScanned}");
                sb.AppendLine($"FilesRewritten: {normalization.FilesRewritten}");
                sb.AppendLine($"MojibakeRepairedFiles: {normalization.MojibakeRepairedFiles}");
                sb.AppendLine($"EncodingConvertedFiles: {normalization.EncodingConvertedFiles}");
                if (normalization.RewrittenFiles.Count > 0)
                {
                    sb.AppendLine("RewrittenFiles:");
                    foreach (var file in normalization.RewrittenFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        sb.AppendLine($" - {file}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("=== Findings (file:line | marker | context) ===");
            foreach (var finding in report.Findings
                         .OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(f => f.LineNumber)
                         .ThenBy(f => f.Marker, StringComparer.Ordinal))
            {
                sb.AppendLine($"{finding.FilePath}:{finding.LineNumber} | {finding.Marker} | {finding.Context}");
            }

            File.WriteAllText(reportPath, sb.ToString(), Utf8NoBom);
            return reportPath;
        }

        private static IEnumerable<string> EnumerateTargetFiles(string rootPath, bool includeTests)
        {
            var pending = new Stack<string>();
            pending.Push(rootPath);

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                var dirName = Path.GetFileName(current);
                if (ExcludedDirectoryNames.Contains(dirName))
                    continue;
                if (!includeTests && dirName.Equals("Tests", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var subDir in SafeEnumerateDirectories(current))
                    pending.Push(subDir);

                foreach (var file in SafeEnumerateFiles(current))
                {
                    if (HasTargetExtension(file))
                        yield return file;
                }
            }
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string path)
        {
            try { return Directory.EnumerateDirectories(path); }
            catch { return Enumerable.Empty<string>(); }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string path)
        {
            try { return Directory.EnumerateFiles(path); }
            catch { return Enumerable.Empty<string>(); }
        }

        private static bool HasTargetExtension(string filePath) =>
            TargetExtensions.Contains(Path.GetExtension(filePath));

        private static bool IsIntentionalMarkerFile(string rootPath, string absoluteFilePath)
        {
            var relative = Path.GetRelativePath(rootPath, absoluteFilePath);
            var normalized = relative.Replace('/', Path.DirectorySeparatorChar);
            return IntentionalMarkerRelativePaths.Contains(normalized);
        }

        private static bool ShouldUseUtf8Bom(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            return ext.Equals(".xaml", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".resx", StringComparison.OrdinalIgnoreCase);
        }

        private static void ScanFileIntoReport(string rootPath, string filePath, EncodingAuditReport report)
        {
            var decoded = ReadTextWithDetection(filePath);
            report.FilesScanned++;

            var relativePath = Path.GetRelativePath(rootPath, filePath);
            var lines = SplitLines(decoded.Text);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                foreach (var marker in MarkerTokens)
                {
                    if (!line.Contains(marker, StringComparison.Ordinal))
                        continue;

                    report.Findings.Add(new EncodingAuditFinding
                    {
                        FilePath = relativePath,
                        LineNumber = i + 1,
                        Marker = marker,
                        Context = BuildContextPreview(line)
                    });
                }
            }
        }

        private static string BuildContextPreview(string line)
        {
            if (line == null)
                return string.Empty;

            var compact = line.Replace('\t', ' ').Trim();
            if (compact.Length <= 200)
                return compact;

            return compact.Substring(0, 197) + "...";
        }

        private static string[] SplitLines(string text) =>
            text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');

        private static string RepairTextForSource(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var lines = SplitLines(text);
            var changed = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!ContainsMarkers(line))
                    continue;

                var repaired = RepairSingleLine(line);
                if (string.Equals(repaired, line, StringComparison.Ordinal))
                    continue;

                lines[i] = repaired;
                changed = true;
            }

            if (!changed)
                return text;

            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            return string.Join(newline, lines);
        }

        private static bool ContainsMarkers(string text)
        {
            foreach (var marker in MarkerTokens)
            {
                if (text.Contains(marker, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static string RepairSingleLine(string line)
        {
            // Candidate set: original + common recoding attempts.
            var candidates = new HashSet<string>(StringComparer.Ordinal)
            {
                line,
                DecodeAsUtf8From(line, Win1252),
                DecodeAsUtf8From(line, Latin1)
            };

            var baseCandidates = candidates.ToArray();
            foreach (var c in baseCandidates)
            {
                candidates.Add(DecodeAsUtf8From(c, Win1252));
                candidates.Add(DecodeAsUtf8From(c, Latin1));
            }

            return candidates
                .Select(TextEncodingNormalizer.NormalizePreservingWhitespace)
                .OrderByDescending(ScoreCandidate)
                .ThenBy(s => s.Length)
                .FirstOrDefault() ?? line;
        }

        private static string DecodeAsUtf8From(string value, Encoding source)
        {
            try
            {
                var bytes = source.GetBytes(value);
                return Utf8NoBom.GetString(bytes);
            }
            catch
            {
                return value;
            }
        }

        private static int ScoreCandidate(string value)
        {
            var score = 0;

            foreach (var marker in MarkerTokens)
            {
                if (value.Contains(marker, StringComparison.Ordinal))
                    score -= 10;
            }

            if (value.IndexOfAny(new[] { 'é', 'è', 'ê', 'à', 'ù', 'ç', 'ô', 'î', 'É', 'À', 'Ç', 'œ', 'Œ' }) >= 0)
                score += 5;

            if (value.Contains("Système", StringComparison.OrdinalIgnoreCase)) score += 3;
            if (value.Contains("Mémoire", StringComparison.OrdinalIgnoreCase)) score += 3;
            if (value.Contains("Réseau", StringComparison.OrdinalIgnoreCase)) score += 3;
            if (value.Contains('\uFFFD')) score -= 15;

            return score;
        }

        private sealed class DecodedText
        {
            public string Text { get; init; } = string.Empty;
            public bool IsUtf8 { get; init; }
            public bool HasUtf8Bom { get; init; }
        }

        private static DecodedText ReadTextWithDetection(string filePath)
        {
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length == 0)
            {
                return new DecodedText
                {
                    Text = string.Empty,
                    IsUtf8 = true,
                    HasUtf8Bom = false
                };
            }

            if (HasUtf8Bom(bytes))
            {
                return new DecodedText
                {
                    Text = Utf8Bom.GetString(bytes).TrimStart('\uFEFF'),
                    IsUtf8 = true,
                    HasUtf8Bom = true
                };
            }

            if (TryDecodeUtf8Strict(bytes, out var utf8Text))
            {
                return new DecodedText
                {
                    Text = utf8Text.TrimStart('\uFEFF'),
                    IsUtf8 = true,
                    HasUtf8Bom = false
                };
            }

            if (HasUtf16LeBom(bytes))
            {
                return new DecodedText
                {
                    Text = Encoding.Unicode.GetString(bytes).TrimStart('\uFEFF'),
                    IsUtf8 = false,
                    HasUtf8Bom = false
                };
            }

            if (HasUtf16BeBom(bytes))
            {
                return new DecodedText
                {
                    Text = Encoding.BigEndianUnicode.GetString(bytes).TrimStart('\uFEFF'),
                    IsUtf8 = false,
                    HasUtf8Bom = false
                };
            }

            return new DecodedText
            {
                Text = Win1252.GetString(bytes).TrimStart('\uFEFF'),
                IsUtf8 = false,
                HasUtf8Bom = false
            };
        }

        private static bool TryDecodeUtf8Strict(byte[] bytes, out string text)
        {
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
                return true;
            }
            catch
            {
                text = string.Empty;
                return false;
            }
        }

        private static bool HasUtf8Bom(byte[] bytes) =>
            bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF;

        private static bool HasUtf16LeBom(byte[] bytes) =>
            bytes.Length >= 2 &&
            bytes[0] == 0xFF &&
            bytes[1] == 0xFE;

        private static bool HasUtf16BeBom(byte[] bytes) =>
            bytes.Length >= 2 &&
            bytes[0] == 0xFE &&
            bytes[1] == 0xFF;
    }
}
