using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Converts technical status tokens to user-facing triplet: label, reason, confidence.
    /// </summary>
    public static class StatusPresentationService
    {
        private static readonly Regex ReasonRegex = new(@"reason(?:IfMissing)?\s*:\s*(?<reason>[^,\)]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ConfidenceRegex = new(@"(?:confiance|confidence)\s*:\s*(?<confidence>[^,\)]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ParenthesizedPayloadRegex = new(@"^(?<label>[^(\r\n]+)\((?<payload>.+)\)\s*$", RegexOptions.Compiled);

        private static readonly Dictionary<string, string> ReasonTokenMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["unknown"] = "La donnée n'a pas pu être identifiée sur cette machine.",
            ["notprovidedby"] = "La source de collecte ne fournit pas cette donnée.",
            ["unavailable"] = "La donnée n'a pas été collectée.",
            ["unavailable_sensor"] = "Capteur indisponible sur cette machine.",
            ["unavailable_permission"] = "Permissions insuffisantes pour collecter cette donnée.",
            ["sentinel_zero"] = "Valeur technique non fiable détectée (sentinelle).",
            ["sentinel_minus_one"] = "Valeur technique non fiable détectée (sentinelle).",
            ["nan_or_infinite"] = "La valeur mesurée est invalide.",
            ["wmi_error"] = "Le collecteur Windows n'a pas pu lire cette information."
        };

        public sealed class StatusPresentation
        {
            public string Label { get; set; } = "Non disponible";
            public string Reason { get; set; } = "La donnée n'a pas pu être collectée.";
            public string Confidence { get; set; } = "Faible";
            public bool IsMissing { get; set; }
            public bool HadTechnicalStatus { get; set; }
        }

        public static StatusPresentation Present(string? rawValue, string? sourceHint = null)
        {
            var normalized = TextEncodingNormalizer.Normalize(rawValue);
            if (string.IsNullOrWhiteSpace(normalized) ||
                normalized.Equals("Non disponible", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Indisponible", StringComparison.OrdinalIgnoreCase))
            {
                return Missing("Non disponible", "La donnée n'a pas pu être collectée.", "Faible");
            }

            var lower = normalized.ToLowerInvariant();
            var hasTechToken = lower.Contains("unknown", StringComparison.Ordinal) ||
                               lower.Contains("unavailable", StringComparison.Ordinal) ||
                               lower.Contains("sentinel_", StringComparison.Ordinal) ||
                               lower.Contains("reasonifmissing", StringComparison.Ordinal) ||
                               lower.Contains("notprovidedby", StringComparison.Ordinal);

            if (normalized.StartsWith("Indisponible", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Non disponible", StringComparison.OrdinalIgnoreCase) ||
                hasTechToken)
            {
                var reason = ExtractReason(normalized) ?? GuessReasonFromTokens(lower);
                var confidence = MapConfidence(ExtractConfidence(normalized) ?? GuessConfidenceFromSource(sourceHint, isMissing: true));
                return Missing("Non disponible", reason, confidence, hasTechToken: true);
            }

            // Remove "(confiance: X)" from display value while preserving confidence metadata.
            var confidenceToken = ExtractConfidence(normalized);
            var trimmedValue = StripConfidencePayload(normalized);

            return new StatusPresentation
            {
                Label = string.IsNullOrWhiteSpace(trimmedValue) ? "Disponible" : trimmedValue,
                Reason = string.Empty,
                Confidence = MapConfidence(confidenceToken ?? GuessConfidenceFromSource(sourceHint, isMissing: false)),
                IsMissing = false,
                HadTechnicalStatus = hasTechToken
            };
        }

        private static StatusPresentation Missing(string label, string reason, string confidence, bool hasTechToken = false)
        {
            return new StatusPresentation
            {
                Label = label,
                Reason = TextEncodingNormalizer.Normalize(reason),
                Confidence = MapConfidence(confidence),
                IsMissing = true,
                HadTechnicalStatus = hasTechToken
            };
        }

        private static string? ExtractReason(string value)
        {
            var reasonMatch = ReasonRegex.Match(value);
            if (reasonMatch.Success)
            {
                var reason = reasonMatch.Groups["reason"].Value.Trim();
                return string.IsNullOrWhiteSpace(reason) ? null : HumanizeReason(reason);
            }

            var payloadMatch = ParenthesizedPayloadRegex.Match(value);
            if (!payloadMatch.Success)
                return null;

            var payload = payloadMatch.Groups["payload"].Value.Trim();
            if (payload.Contains("confiance", StringComparison.OrdinalIgnoreCase) ||
                payload.Contains("confidence", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return HumanizeReason(payload);
        }

        private static string? ExtractConfidence(string value)
        {
            var confidenceMatch = ConfidenceRegex.Match(value);
            if (!confidenceMatch.Success)
                return null;

            var confidence = confidenceMatch.Groups["confidence"].Value.Trim();
            return string.IsNullOrWhiteSpace(confidence) ? null : confidence;
        }

        private static string StripConfidencePayload(string value)
        {
            var confidenceMatch = ConfidenceRegex.Match(value);
            if (!confidenceMatch.Success)
                return value;

            var index = value.IndexOf('(');
            if (index <= 0)
                return value;

            return value.Substring(0, index).Trim();
        }

        private static string GuessReasonFromTokens(string lowerValue)
        {
            foreach (var kvp in ReasonTokenMap)
            {
                if (lowerValue.Contains(kvp.Key.ToLowerInvariant(), StringComparison.Ordinal))
                    return kvp.Value;
            }

            return "La donnée n'a pas pu être collectée de façon fiable.";
        }

        private static string GuessConfidenceFromSource(string? source, bool isMissing)
        {
            if (isMissing)
                return "none";

            if (string.IsNullOrWhiteSpace(source))
                return "medium";

            if (source.Contains("C#", StringComparison.OrdinalIgnoreCase) ||
                source.Contains("scan_powershell", StringComparison.OrdinalIgnoreCase) ||
                source.Contains("PS", StringComparison.OrdinalIgnoreCase))
            {
                return "high";
            }

            if (source.Contains("PerformanceEvaluationEngine", StringComparison.OrdinalIgnoreCase) ||
                source.Contains("diagnostic_signals", StringComparison.OrdinalIgnoreCase))
            {
                return "medium";
            }

            return "medium";
        }

        private static string HumanizeReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return "La donnée n'a pas pu être collectée.";

            var normalized = reason.Replace('_', ' ').Trim();
            if (ReasonTokenMap.TryGetValue(normalized, out var mapped))
                return mapped;

            if (ReasonTokenMap.TryGetValue(reason, out mapped))
                return mapped;

            return char.ToUpperInvariant(normalized[0]) + normalized.Substring(1);
        }

        public static string MapConfidence(string? confidence)
        {
            if (string.IsNullOrWhiteSpace(confidence))
                return "Moyenne";

            var normalized = confidence.Trim().ToLowerInvariant();
            if (normalized is "high" or "elevee" or "élevée")
                return "Élevée";
            if (normalized is "medium" or "moyenne")
                return "Moyenne";
            if (normalized is "low" or "faible")
                return "Faible";
            if (normalized is "none" or "aucune" or "n/a")
                return "Aucune";

            return confidence.Trim();
        }
    }
}
