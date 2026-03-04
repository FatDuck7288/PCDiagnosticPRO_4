using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Views
{
    public partial class CollectorErrorsWindow : Window
    {
        public CollectorErrorsWindow(CollectorDiagnosticsDialogData dialogData)
        {
            InitializeComponent();
            DataContext = new CollectorErrorsViewModel(dialogData ?? new CollectorDiagnosticsDialogData());
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    public sealed class CollectorErrorsViewModel
    {
        public CollectorErrorsViewModel(CollectorDiagnosticsDialogData data)
        {
            ErrorItems = (data.Errors ?? new List<CollectorDiagnosticDetailItem>())
                .Where(item => item != null)
                .ToList();
            MissingItems = (data.MissingData ?? new List<CollectorDiagnosticDetailItem>())
                .Where(item => item != null)
                .ToList();
            CsharpItems = (data.CsharpExceptions ?? new List<CollectorDiagnosticDetailItem>())
                .Where(item => item != null)
                .ToList();
            CollectorErrorsLogical = data.CollectorErrorsLogical;
        }

        public int CollectorErrorsLogical { get; }
        public List<CollectorDiagnosticDetailItem> ErrorItems { get; }
        public List<CollectorDiagnosticDetailItem> MissingItems { get; }
        public List<CollectorDiagnosticDetailItem> CsharpItems { get; }

        public int TotalItems => ErrorItems.Count + MissingItems.Count + CsharpItems.Count;

        public string SummaryText
        {
            get
            {
                if (TotalItems == 0)
                    return "Aucun incident structuré n'a été remonté pour cette collecte.";

                return $"{TotalItems} élément(s) diagnostic(s) | erreurs logiques: {CollectorErrorsLogical}";
            }
        }

        public string ErrorHeader => $"ERREURS DE COLLECTE ({ErrorItems.Count})";
        public string MissingHeader => $"DONNÉES MANQUANTES ({MissingItems.Count})";
        public string CsharpHeader => $"EXCEPTIONS COLLECTEUR C# ({CsharpItems.Count})";
    }
}
