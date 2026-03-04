using System.Windows;
using PCDiagnosticPro.ViewModels;

namespace PCDiagnosticPro.Views
{
    public partial class KernelPowerInfoWindow : Window
    {
        public KernelPowerInfoWindow(KernelPowerData? data = null)
        {
            InitializeComponent();
            DataContext = data ?? new KernelPowerData();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
