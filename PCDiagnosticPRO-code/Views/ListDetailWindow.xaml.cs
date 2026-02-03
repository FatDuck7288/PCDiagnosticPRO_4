using System.Collections.Generic;
using System.Windows;

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
            WindowTitle = title ?? "Liste";
            SummaryText = summary ?? "";
            Items = items ?? new List<string>();
        }
    }
}
