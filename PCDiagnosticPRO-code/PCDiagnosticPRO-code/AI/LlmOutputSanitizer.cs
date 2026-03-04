using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PCDiagnosticPro.AI
{
    public sealed class LlmSanitizationResult
    {
        public string Text { get; init; } = string.Empty;
        public bool TruncatedAtInvalidPattern { get; init; }
        public string TriggerPattern { get; init; } = string.Empty;
        public bool RejectedByWhitelist { get; init; }
        public bool FallbackApplied { get; init; }
        public int KeptLines { get; init; }
        public int DroppedLines { get; init; }
        public string? FirstDropReason { get; init; }
    }

    /// <summary>
    /// Central sanitizer for LLM output shown in chat UI.
    /// </summary>
    public static class LlmOutputSanitizer
    {
        private static readonly string[] ChatInvalidPatterns =
        {
            "[LANGUAGE:",
            "<|assistant|>",
            "<|system|>",
            "<|user|>",
            "<|im_start|>",
            "<|im_end|>",
            "<|end|>",
            "INTERNAL INSTRUCTION",
            "DEBUG:",
            "<think>",
            "</think>"
        };

        private static readonly string[] ControlInvalidPatterns =
        {
            "[LANGUAGE:",
            "<|assistant|>",
            "<|system|>",
            "<|user|>",
            "### Assistant",
            "### Answering",
            "<think>",
            "</think>"
        };

        private static readonly Regex ThinkBlockStripRegex = new(
            @"<think>[\s\S]*?</think>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex UnclosedThinkStripRegex = new(
            @"<think>[\s\S]*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex NumberedListRegex = new(@"^\s*\d+\.\s+", RegexOptions.Compiled);
        private static readonly Regex BulletListRegex = new(@"^\s*-\s+", RegexOptions.Compiled);
        private static readonly Regex SectionLineRegex = new(
            @"^\s*(Probl.?me|Impact|Cause probable|Solution(?: recommand.?e)?|Priorit.?|Resume(?: ex.?cutif)?|Score de sant.? global|Probl.?mes prioritaires" +
            @"|S.?v.?rit.?|Preuve|Action recommand.?e|Hypoth.?se|Indices du scan|Probabilit.?|Plan d.action|Etat|Cat.?gorie|Observations" +
            @"|Issue|Severity|Evidence|Probable cause|Recommended action|Hypothesis|Scan evidence|Probability|Summary|Executive Summary|Health score|Status" +
            @"|Problema|Severidad|Evidencia|Causa probable|Acci.?n recomendada|Hip.?tesis|Probabilidad|Resumen|Estado" +
            @"|P0|P1|P2)\s*:",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FrenchSignalRegex = new(
            @"\b(le|la|les|de|des|du|et|est|pour|avec|dans|sur|une|un|vous|votre|diagnostic|solution|priorite)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex EnglishSignalRegex = new(
            @"\b(the|and|is|are|with|for|your|issue|solution|priority|should)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SpanishSignalRegex = new(
            @"\b(el|los|las|con|para|usted|problema|solucion|prioridad)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static LlmSanitizationResult SanitizeChatAssistantOutput(string raw, string language = "fr")
        {
            var normalized = Normalize(raw);
            var rawLen = normalized.Length;
            normalized = ThinkBlockStripRegex.Replace(normalized, string.Empty);
            normalized = UnclosedThinkStripRegex.Replace(normalized, string.Empty);
            normalized = normalized.Trim();
            var truncated = false;
            var trigger = string.Empty;

            var cut = FindFirstPatternIndex(normalized, ChatInvalidPatterns, out trigger);
            if (cut >= 0)
            {
                LogSanitizer($"CUT at pos={cut} trigger='{trigger}' beforeLen={normalized.Length} afterLen={cut}");
                normalized = normalized[..cut];
                truncated = true;
            }

            var lines = normalized.Split('\n');
            var kept = new List<string>(lines.Length);
            var dropped = 0;
            var collapseBlank = false;
            string? firstDropReason = null;

            foreach (var line in lines)
            {
                var cleaned = StripMarkdownHeading(line).TrimEnd();
                if (cleaned.Length == 0)
                {
                    if (!collapseBlank)
                    {
                        kept.Add(string.Empty);
                        collapseBlank = true;
                    }

                    continue;
                }

                collapseBlank = false;

                if (ContainsAnyPattern(cleaned, ChatInvalidPatterns, out var inlinePattern))
                {
                    var idx = cleaned.IndexOf(inlinePattern, StringComparison.OrdinalIgnoreCase);
                    if (idx > 0)
                    {
                        var before = cleaned[..idx].TrimEnd();
                        if (before.Length > 0 && IsAllowedLine(before))
                        {
                            kept.Add(before);
                        }
                    }

                    truncated = true;
                    trigger = string.IsNullOrWhiteSpace(trigger) ? inlinePattern : trigger;
                    break;
                }

                if (IsAllowedLine(cleaned))
                {
                    kept.Add(cleaned);
                }
                else
                {
                    dropped++;
                    var badChar = FindFirstDisallowedChar(cleaned);
                    var reason = badChar != null
                        ? $"disallowed_char=U+{(int)badChar.Value:X4}('{badChar.Value}') at pos={cleaned.IndexOf(badChar.Value)}"
                        : "IsPlainTextLine=false";
                    if (firstDropReason == null) firstDropReason = reason;
                    LogSanitizer($"LINE_DROPPED[{dropped}]: reason={reason} line='{SafeTrimLog(cleaned, 120)}'");
                }
            }

            var text = string.Join(Environment.NewLine, kept).Trim();
            var fallbackApplied = false;

            // Language check is now LOG-ONLY — never replace useful content with fallback.
            // Fallback only applies when the sanitized text is truly empty.
            if (text.Length == 0)
            {
                // Text is empty after filtering — apply fallback
                text = BuildFallback(language);
                fallbackApplied = true;
                LogSanitizer($"FALLBACK_EMPTY rawLen={rawLen} normalizedLen={normalized.Length} dropped={dropped} lang={language}");
            }
            else
            {
                // Log language mismatch as diagnostic (NOT a gate)
                var langMismatch = language switch
                {
                    "fr" => !LooksFrench(text),
                    "en" => !LooksEnglish(text),
                    "es" => !LooksSpanish(text),
                    _ => false
                };
                if (langMismatch)
                {
                    LogSanitizer($"LANG_MISMATCH lang={language} textLen={text.Length} dropped={dropped} — content preserved (not replaced)");
                }
            }

            LogSanitizer($"RESULT rawLen={rawLen} keptLines={kept.Count} dropped={dropped} truncated={truncated} fallback={fallbackApplied} resultLen={text.Length} firstDropReason={firstDropReason ?? "none"}");

            return new LlmSanitizationResult
            {
                Text = text,
                TruncatedAtInvalidPattern = truncated,
                TriggerPattern = trigger,
                RejectedByWhitelist = dropped > 0,
                FallbackApplied = fallbackApplied,
                KeptLines = kept.Count,
                DroppedLines = dropped,
                FirstDropReason = firstDropReason
            };
        }

        /// <summary>
        /// Generic trim for agent outputs to drop leaked control markers.
        /// </summary>
        public static string TrimAtFirstControlPattern(string raw, out string triggerPattern)
        {
            var normalized = StripThinkBlocks(Normalize(raw));
            var cut = FindFirstPatternIndex(normalized, ControlInvalidPatterns, out triggerPattern);
            return cut >= 0 ? normalized[..cut] : normalized;
        }

        /// <summary>
        /// Removes model reasoning blocks (&lt;think&gt;...&lt;/think&gt;) without discarding
        /// valid content that follows the block.
        /// </summary>
        public static string StripThinkBlocks(string raw)
        {
            var normalized = Normalize(raw);
            if (string.IsNullOrEmpty(normalized))
            {
                return string.Empty;
            }

            normalized = ThinkBlockStripRegex.Replace(normalized, string.Empty);
            normalized = UnclosedThinkStripRegex.Replace(normalized, string.Empty);
            return normalized.Trim();
        }

        private static bool IsAllowedLine(string line)
        {
            if (NumberedListRegex.IsMatch(line))
            {
                return true;
            }

            if (BulletListRegex.IsMatch(line))
            {
                return true;
            }

            var normalizedLine = StripLeadingDecorators(line);
            if (SectionLineRegex.IsMatch(normalizedLine))
            {
                return true;
            }

            return IsPlainTextLine(normalizedLine);
        }

        private static bool IsPlainTextLine(string line)
        {
            foreach (var ch in line)
            {
                if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
                {
                    continue;
                }

                if (ch is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"'
                    or '(' or ')' or '/' or '%' or '+' or '-'
                    or '_' or '#' or '=' or '[' or ']' or '@' or '\\' or '*' or '&' or '~'
                    or '<' or '>' or '{' or '}'
                    or '°' or '→' or '←' or '•' or '—' or '–' or '…' or '|' or '^'
                    or '$' or '©' or '®' or '™' or '«' or '»' or '\u2013' or '\u2014'
                    or '\u00E0' or '\u00E8' or '\u00E9' or '\u00EA' or '\u00EE' or '\u00F4'
                    or '\u00FB' or '\u00E7' or '\u00FC' or '\u00F1')
                {
                    continue;
                }

                if (char.IsHighSurrogate(ch) || char.IsLowSurrogate(ch))
                {
                    continue;
                }

                // Accept any character in the Latin, common punctuation, and symbol Unicode categories
                var cat = char.GetUnicodeCategory(ch);
                if (cat == System.Globalization.UnicodeCategory.OtherPunctuation
                    || cat == System.Globalization.UnicodeCategory.DashPunctuation
                    || cat == System.Globalization.UnicodeCategory.CurrencySymbol
                    || cat == System.Globalization.UnicodeCategory.MathSymbol
                    || cat == System.Globalization.UnicodeCategory.ModifierSymbol
                    || cat == System.Globalization.UnicodeCategory.OtherSymbol
                    || cat == System.Globalization.UnicodeCategory.InitialQuotePunctuation
                    || cat == System.Globalization.UnicodeCategory.FinalQuotePunctuation
                    || cat == System.Globalization.UnicodeCategory.NonSpacingMark
                    || cat == System.Globalization.UnicodeCategory.SpacingCombiningMark)
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        private static bool LooksFrench(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length < 80)
            {
                return true;
            }

            var fr = FrenchSignalRegex.Matches(text).Count;
            var en = EnglishSignalRegex.Matches(text).Count;

            // Diagnostic content naturally contains English technical terms (SMART, driver, registry, etc.)
            // Only reject when there's zero French AND strong English presence.
            if (fr == 0 && en >= 5)
            {
                return false;
            }

            // If there's at least some French signal, accept the text
            // (technical mixed-language diagnostic content is normal)
            return fr >= 1 || en < 8;
        }

        private static bool LooksEnglish(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length < 80)
                return true;

            var en = EnglishSignalRegex.Matches(text).Count;
            var fr = FrenchSignalRegex.Matches(text).Count;

            // Only reject when zero English AND strong other-language presence
            if (en == 0 && fr >= 5)
                return false;
            return en >= 1 || fr < 8;
        }

        private static bool LooksSpanish(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length < 80)
                return true;

            var es = SpanishSignalRegex.Matches(text).Count;
            var en = EnglishSignalRegex.Matches(text).Count;

            if (es == 0 && en >= 5)
                return false;
            return es >= 1 || en < 8;
        }

        public static string BuildFallback(string language)
        {
            return language switch
            {
                "en" => BuildEnglishFallback(),
                "es" => BuildSpanishFallback(),
                _ => BuildFrenchFallback()
            };
        }

        private static string BuildFrenchFallback()
        {
            var lines = new[]
            {
                "Resume global : La reponse IA n'a pas pu etre formatee correctement pour l'interface.",
                "Score de sante global : Indetermine",
                "Probleme : Reponse IA partielle ou structure instable",
                "Impact : L'analyse reste lisible mais peut manquer de precision",
                "Cause probable : Variabilite de generation du modele local",
                "Solution recommandee :",
                "- Relancer l'analyse sur le run selectionne",
                "- Reformuler la demande avec une question ciblee",
                "- Verifier le chargement correct du modele local",
                "Priorite : Moyenne"
            };

            return string.Join(Environment.NewLine, lines);
        }

        private static string BuildEnglishFallback()
        {
            var lines = new[]
            {
                "Global summary: The AI response could not be formatted correctly for the interface.",
                "Health score: Undetermined",
                "Issue: Partial AI response or unstable structure",
                "Impact: Analysis is readable but may lack precision",
                "Probable cause: Local model generation variability",
                "Recommended solution:",
                "- Re-run the analysis on the selected run",
                "- Rephrase your question with a more focused query",
                "- Verify that the local model is correctly loaded",
                "Priority: Medium"
            };

            return string.Join(Environment.NewLine, lines);
        }

        private static string BuildSpanishFallback()
        {
            var lines = new[]
            {
                "Resumen global: La respuesta IA no pudo formatearse correctamente para la interfaz.",
                "Puntuacion de salud: Indeterminada",
                "Problema: Respuesta IA parcial o estructura inestable",
                "Impacto: El analisis es legible pero puede carecer de precision",
                "Causa probable: Variabilidad de generacion del modelo local",
                "Solucion recomendada:",
                "- Relanzar el analisis en la ejecucion seleccionada",
                "- Reformular la solicitud con una pregunta mas especifica",
                "- Verificar que el modelo local este correctamente cargado",
                "Prioridad: Media"
            };

            return string.Join(Environment.NewLine, lines);
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static string StripMarkdownHeading(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return string.Empty;
            }

            var trimmed = line.TrimStart();
            while (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                trimmed = trimmed[1..].TrimStart();
            }

            return trimmed;
        }

        private static string StripLeadingDecorators(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return string.Empty;
            }

            var trimmed = line.TrimStart();
            var index = 0;
            while (index < trimmed.Length)
            {
                var ch = trimmed[index];
                if (char.IsLetterOrDigit(ch) || ch == '-')
                {
                    break;
                }

                index++;
            }

            return index <= 0 ? trimmed : trimmed[index..].TrimStart();
        }

        private static int FindFirstPatternIndex(string text, IEnumerable<string> patterns, out string triggerPattern)
        {
            var cut = -1;
            triggerPattern = string.Empty;

            foreach (var pattern in patterns)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    continue;
                }

                var idx = text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    continue;
                }

                if (cut == -1 || idx < cut)
                {
                    cut = idx;
                    triggerPattern = pattern;
                }
            }

            return cut;
        }

        private static bool ContainsAnyPattern(string text, IEnumerable<string> patterns, out string patternFound)
        {
            patternFound = string.Empty;
            foreach (var pattern in patterns.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                if (text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    patternFound = pattern;
                    return true;
                }
            }

            return false;
        }

        private static void LogSanitizer(string message)
        {
            try
            {
                var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PCDiagnosticPRO", "logs", "ai");
                System.IO.Directory.CreateDirectory(dir);
                var logPath = System.IO.Path.Combine(dir, "sanitizer_trace.log");
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:o}] {message}{Environment.NewLine}");
            }
            catch { /* trace logging must never crash */ }
        }

        /// <summary>
        /// Returns the first character in a line that would cause IsPlainTextLine to reject it, or null if all pass.
        /// </summary>
        private static char? FindFirstDisallowedChar(string line)
        {
            foreach (var ch in line)
            {
                if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch)) continue;
                if (ch is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"'
                    or '(' or ')' or '/' or '%' or '+' or '-'
                    or '_' or '#' or '=' or '[' or ']' or '@' or '\\' or '*' or '&' or '~'
                    or '<' or '>' or '{' or '}'
                    or '°' or '→' or '←' or '•' or '—' or '–' or '…' or '|' or '^'
                    or '$' or '©' or '®' or '™' or '«' or '»' or '\u2013' or '\u2014'
                    or '\u00E0' or '\u00E8' or '\u00E9' or '\u00EA' or '\u00EE' or '\u00F4'
                    or '\u00FB' or '\u00E7' or '\u00FC' or '\u00F1') continue;
                if (char.IsHighSurrogate(ch) || char.IsLowSurrogate(ch)) continue;
                var cat = char.GetUnicodeCategory(ch);
                if (cat == System.Globalization.UnicodeCategory.OtherPunctuation
                    || cat == System.Globalization.UnicodeCategory.DashPunctuation
                    || cat == System.Globalization.UnicodeCategory.CurrencySymbol
                    || cat == System.Globalization.UnicodeCategory.MathSymbol
                    || cat == System.Globalization.UnicodeCategory.ModifierSymbol
                    || cat == System.Globalization.UnicodeCategory.OtherSymbol
                    || cat == System.Globalization.UnicodeCategory.InitialQuotePunctuation
                    || cat == System.Globalization.UnicodeCategory.FinalQuotePunctuation
                    || cat == System.Globalization.UnicodeCategory.NonSpacingMark
                    || cat == System.Globalization.UnicodeCategory.SpacingCombiningMark) continue;
                return ch;
            }
            return null;
        }

        private static string SafeTrimLog(string s, int max) =>
            s.Length <= max ? s : s[..max] + "...";
    }
}
