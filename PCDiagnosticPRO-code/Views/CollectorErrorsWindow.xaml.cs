using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Views
{
    public partial class CollectorErrorsWindow : Window
    {
        public CollectorErrorsWindow(List<ScanErrorInfo>? errors, List<string>? missingData, int collectorErrorsLogical)
        {
            InitializeComponent();
            DataContext = new CollectorErrorsViewModel(errors, missingData, collectorErrorsLogical);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    public class CollectorErrorsViewModel
    {
        public CollectorErrorsViewModel(List<ScanErrorInfo>? errors, List<string>? missingData, int collectorErrorsLogical)
        {
            var errorList = errors ?? new List<ScanErrorInfo>();
            var missingList = missingData ?? new List<string>();

            ErrorCount = errorList.Count;
            MissingCount = missingList.Count;
            CollectorErrorsLogical = collectorErrorsLogical;

            // Formatted errors with French explanations
            FormattedErrors = errorList.Select((e, i) => new FormattedError
            {
                Number = $"Erreur {i + 1}",
                Code = string.IsNullOrWhiteSpace(e.Code) ? "UNKNOWN" : e.Code.ToUpperInvariant(),
                Explanation = GetFrenchExplanation(e)
            }).ToList();

            // Formatted missing data (parse semicolon-separated entries)
            FormattedMissingData = missingList.Select(m => ParseMissingEntry(m)).ToList();

            // Legacy compatibility
            ErrorListSimple = errorList.Select((e, i) =>
                $"{i + 1}  [{e.Section ?? ""}] {e.Message ?? e.Code ?? "Unknown error"}"
            ).ToList();
            MissingListSimple = missingList.ToList();
            ErrorNamesSummary = errorList.Count == 0 ? "" : "Erreurs : " + string.Join(" ; ", errorList.Select((e, i) => $"{i + 1}. [{e.Section ?? "N/A"}] {e.Message ?? e.Code ?? "—"}"));
            MissingDataNamesSummary = missingList.Count == 0 ? "" : "Données manquantes : " + string.Join(" ; ", missingList);
        }

        // --- Public properties ---
        public int ErrorCount { get; }
        public int MissingCount { get; }
        public int CollectorErrorsLogical { get; }
        public bool HasErrors => ErrorCount > 0;
        public bool HasMissing => MissingCount > 0;
        public string SummaryText => $"{ErrorCount + MissingCount} problème(s) de collecte";

        // Formatted collections for new XAML
        public List<FormattedError> FormattedErrors { get; }
        public List<FormattedMissing> FormattedMissingData { get; }

        // Legacy
        public List<string> ErrorListSimple { get; }
        public List<string> MissingListSimple { get; }
        public string ErrorNamesSummary { get; }
        public string MissingDataNamesSummary { get; }

        /// <summary>
        /// Maps error codes/messages to clear French explanations.
        /// </summary>
        private static string GetFrenchExplanation(ScanErrorInfo error)
        {
            var code = (error.Code ?? "").ToUpperInvariant().Trim();
            var msg = (error.Message ?? "").Trim();
            var section = (error.Section ?? "").Trim();

            // Match by code
            if (code.Contains("WMI") || msg.Contains("WMI", StringComparison.OrdinalIgnoreCase))
                return $"Échec de la récupération via WMI (Windows Management Instrumentation). " +
                       "Le service WMI peut être indisponible, les droits insuffisants, ou la requête a échoué." +
                       (string.IsNullOrEmpty(section) ? "" : $" Section : {section}.");

            if (code.Contains("TIMEOUT") || msg.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                return "Délai d'attente dépassé. L'opération a pris trop de temps à répondre." +
                       (string.IsNullOrEmpty(section) ? "" : $" Section : {section}.");

            if (code.Contains("ACCESS") || code.Contains("PERMISSION") ||
                msg.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("permission", StringComparison.OrdinalIgnoreCase))
                return "Accès refusé. Les droits administrateur sont requis pour cette collecte." +
                       (string.IsNullOrEmpty(section) ? "" : $" Section : {section}.");

            if (code.Contains("SENSOR") || msg.Contains("sensor", StringComparison.OrdinalIgnoreCase))
                return "Impossible d'accéder aux capteurs matériels. " +
                       "Vérifiez les droits administrateur ou les exclusions Windows Defender." +
                       (string.IsNullOrEmpty(section) ? "" : $" Section : {section}.");

            if (code.Contains("NETWORK") || msg.Contains("network", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("réseau", StringComparison.OrdinalIgnoreCase))
                return "Erreur réseau. La connectivité peut être indisponible ou le test a échoué." +
                       (string.IsNullOrEmpty(section) ? "" : $" Section : {section}.");

            // Unknown error
            if (string.IsNullOrWhiteSpace(msg) || msg.Equals("Unknown error", StringComparison.OrdinalIgnoreCase))
                return "Erreur inattendue. La cause exacte n'a pas pu être identifiée par le collecteur." +
                       (string.IsNullOrEmpty(section) ? "" : $" Section : {section}.");

            // Fallback: use the raw message with section context
            return msg + (string.IsNullOrEmpty(section) ? "" : $" (Section : {section})");
        }

        /// <summary>
        /// Parses a semicolon-separated missing data string into a structured entry.
        /// Format: "Name ; Timestamp ; Details"
        /// </summary>
        private static FormattedMissing ParseMissingEntry(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new FormattedMissing { Name = "Donnée inconnue", Details = "", Timestamp = "" };

            var parts = raw.Split(';');
            var name = parts.Length > 0 ? parts[0].Trim() : raw.Trim();
            var timestamp = "";
            var details = "";

            for (int i = 1; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                // Detect timestamps (ISO 8601 pattern)
                if (part.Length > 10 && (part.Contains("T") && part.Contains(":") || part.Contains("-") && part.Contains(":")))
                {
                    timestamp = $"Horodatage : {part}";
                }
                else if (!string.IsNullOrWhiteSpace(part))
                {
                    details = string.IsNullOrEmpty(details) ? part : $"{details} — {part}";
                }
            }

            return new FormattedMissing
            {
                Name = name,
                Details = string.IsNullOrEmpty(details) ? "Données non disponibles" : details,
                Timestamp = timestamp
            };
        }
    }

    public class FormattedError
    {
        public string Number { get; set; } = "";
        public string Code { get; set; } = "";
        public string Explanation { get; set; } = "";
    }

    public class FormattedMissing
    {
        public string Name { get; set; } = "";
        public string Details { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public bool HasTimestamp => !string.IsNullOrWhiteSpace(Timestamp);
    }
}
