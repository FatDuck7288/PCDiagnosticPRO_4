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
                // Debug: window loaded, sections available
            };
        }
    }
}
