using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PCDiagnosticPro.Services;

namespace PCDiagnosticPro.Views
{
    /// <summary>
    /// Fenêtre générique listant des éléments (périph. audio, imprimantes, pilotes obsolètes).
    /// </summary>
    public partial class ListDetailWindow : Window
    {
        public ListDetailWindow(string title, string summary, IReadOnlyList<string> items)
        {
            InitializeComponent();
            DataContext = new ListDetailViewModel(title, summary, items);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    public class ListDetailViewModel
    {
        public string WindowTitle { get; }
        public string SummaryText { get; }
        public IReadOnlyList<string> Items { get; }

        public ListDetailViewModel(string title, string summary, IReadOnlyList<string> items)
        {
            WindowTitle = TextEncodingNormalizer.Normalize(title ?? "Liste");
            SummaryText = TextEncodingNormalizer.Normalize(summary ?? string.Empty);
            Items = (items ?? new List<string>())
                .Select(TextEncodingNormalizer.Normalize)
                .ToArray();
        }
    }
}
