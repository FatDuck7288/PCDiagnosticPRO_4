using System.Windows;

namespace PCDiagnosticPro.Views
{
    /// <summary>
    /// Pre-scan consent dialog shown when app is not running as admin.
    /// Result: "UAC" = restart as admin, "Limited" = continue without hardware sensors, null = cancel.
    /// </summary>
    public partial class ScanConsentDialog : Window
    {
        /// <summary>
        /// "UAC" = user chose to restart as admin
        /// "Limited" = user chose limited mode (no hardware sensors)
        /// null = user cancelled
        /// </summary>
        public string? UserChoice { get; private set; }

        public ScanConsentDialog()
        {
            InitializeComponent();
        }

        private void ContinueUAC_Click(object sender, RoutedEventArgs e)
        {
            UserChoice = "UAC";
            DialogResult = true;
            Close();
        }

        private void LimitedMode_Click(object sender, RoutedEventArgs e)
        {
            UserChoice = "Limited";
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            UserChoice = null;
            DialogResult = false;
            Close();
        }
    }
}
