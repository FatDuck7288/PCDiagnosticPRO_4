using System.Windows;

namespace PCDiagnosticPro.Views
{
    public partial class CyberpunkInfoWindow : Window
    {
        public CyberpunkInfoWindow(string title, string content)
        {
            InitializeComponent();
            Title = title;
            TitleBlock.Text = title;
            ContentBlock.Text = content ?? "";
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
