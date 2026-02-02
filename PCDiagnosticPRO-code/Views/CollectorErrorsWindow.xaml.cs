using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Views
{
    /// <summary>
    /// Fenêtre de détails des erreurs de collecte
    /// PARTIE 2: Remplace le MessageBox par une fenêtre structurée avec tableau
    /// </summary>
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

    /// <summary>
    /// ViewModel pour la fenêtre d'erreurs collecteur
    /// </summary>
    public class CollectorErrorsViewModel
    {
        public CollectorErrorsViewModel(List<ScanErrorInfo>? errors, List<string>? missingData, int collectorErrorsLogical)
        {
            var errorList = errors ?? new List<ScanErrorInfo>();
            var missingList = missingData ?? new List<string>();

            ErrorCount = errorList.Count;
            MissingCount = missingList.Count;
            CollectorErrorsLogical = collectorErrorsLogical;
            
            // Build error items for DataGrid
            var items = new List<ErrorDisplayItem>();
            int num = 1;
            
            foreach (var error in errorList)
            {
                items.Add(new ErrorDisplayItem
                {
                    Number = num++,
                    TypeIcon = "❌",
                    Section = error.Section ?? "N/A",
                    Code = error.Code ?? "N/A",
                    Message = error.Message ?? "Erreur inconnue",
                    Source = error.ExceptionType ?? "N/A",
                    SuggestedAction = GetSuggestedAction(error)
                });
            }
            
            // Add missing data as warning items
            foreach (var missing in missingList)
            {
                items.Add(new ErrorDisplayItem
                {
                    Number = num++,
                    TypeIcon = "⚠️",
                    Section = ExtractSectionFromMissing(missing),
                    Code = "MISSING",
                    Message = missing,
                    Source = "Collecte",
                    SuggestedAction = "Relancer en mode Admin"
                });
            }
            
            ErrorItems = items;
            MissingDataItems = missingList.Select(m => $"• {m}").ToList();
        }

        public int ErrorCount { get; }
        public int MissingCount { get; }
        public int CollectorErrorsLogical { get; }
        public bool HasErrors => ErrorCount > 0;
        public bool HasMissing => MissingCount > 0;
        public string SummaryText => $"{ErrorCount + MissingCount} problème(s) de collecte identifié(s)";
        public string CollectionScoreDisplay => $"{Math.Max(0, 100 - (ErrorCount * 10) - (MissingCount * 5))}/100";
        public List<ErrorDisplayItem> ErrorItems { get; }
        public List<string> MissingDataItems { get; }

        private static string GetSuggestedAction(ScanErrorInfo error)
        {
            var section = (error.Section ?? "").ToLowerInvariant();
            var code = (error.Code ?? "").ToLowerInvariant();
            
            if (section.Contains("sensor") || code.Contains("wmi"))
                return "Exécuter en Admin";
            if (section.Contains("driver") || section.Contains("pilote"))
                return "Vérifier pilotes";
            if (section.Contains("network") || section.Contains("réseau"))
                return "Vérifier connectivité";
            if (code.Contains("timeout"))
                return "Réessayer plus tard";
            if (code.Contains("permission") || code.Contains("access"))
                return "Droits Admin requis";
            
            return "Relancer le scan";
        }

        private static string ExtractSectionFromMissing(string missing)
        {
            if (missing.Contains("CPU", StringComparison.OrdinalIgnoreCase)) return "CPU";
            if (missing.Contains("GPU", StringComparison.OrdinalIgnoreCase)) return "GPU";
            if (missing.Contains("RAM", StringComparison.OrdinalIgnoreCase)) return "Mémoire";
            if (missing.Contains("Disk", StringComparison.OrdinalIgnoreCase) || 
                missing.Contains("Storage", StringComparison.OrdinalIgnoreCase)) return "Stockage";
            if (missing.Contains("Network", StringComparison.OrdinalIgnoreCase)) return "Réseau";
            if (missing.Contains("Security", StringComparison.OrdinalIgnoreCase)) return "Sécurité";
            if (missing.Contains("SMART", StringComparison.OrdinalIgnoreCase)) return "S.M.A.R.T.";
            return "Général";
        }
    }

    /// <summary>
    /// Item d'affichage pour le DataGrid des erreurs
    /// </summary>
    public class ErrorDisplayItem
    {
        public int Number { get; set; }
        public string TypeIcon { get; set; } = "❌";
        public string Section { get; set; } = "";
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
        public string Source { get; set; } = "";
        public string SuggestedAction { get; set; } = "";
    }
}
