using System.Windows;

namespace PCDiagnosticPro.Views
{
    public partial class KernelPowerInfoWindow : Window
    {
        public KernelPowerInfoWindow()
        {
            InitializeComponent();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
