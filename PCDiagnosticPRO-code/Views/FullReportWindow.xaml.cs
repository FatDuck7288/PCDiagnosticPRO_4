using System.Windows;

namespace PCDiagnosticPro.Views
{
    public partial class FullReportWindow : Window
    {
        public FullReportWindow(ViewModels.FullReportViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            ReportView.DataContext = viewModel;
            Loaded += (_, _) =>
            {
                // Force ContentControl to refresh: re-set SelectedSection so the detail template re-applies with content
                if (viewModel.Sections != null && viewModel.Sections.Count > 0)
                {
                    var first = viewModel.Sections[0];
                    viewModel.SelectedSection = null;
                    viewModel.SelectedSection = first;
                }
            };
        }
    }
}
