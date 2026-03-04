using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

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
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            CenterDialog();
            Activate();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                ContinueUacButton.Focus();
                Keyboard.Focus(ContinueUacButton);
            }));
        }

        private void CenterDialog()
        {
            if (Owner != null)
            {
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                return;
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            var area = SystemParameters.WorkArea;
            Left = area.Left + Math.Max(0, (area.Width - ActualWidth) / 2);
            Top = area.Top + Math.Max(0, (area.Height - ActualHeight) / 2);
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