using System.Collections.Generic;
using System.Windows;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.Services;

namespace PCDiagnosticPro.Views
{
    public partial class DiskTemperatureInfoWindow : Window
    {
        public DiskTemperatureInfoWindow()
            : this(new InfoExplanationService().BuildInfoLines(new InfoContext
            {
                ContextId = InfoContextId.DiskTemp,
                SectionId = InfoSectionId.Storage,
                MetricLabel = "Température disque",
                Value = 55d,
                Unit = "°C",
                Severity = InfoSeverity.Warning,
                Confidence = InfoConfidence.Low
            }))
        {
        }

        public DiskTemperatureInfoWindow(IReadOnlyList<InfoLine> lines)
        {
            InitializeComponent();
            InfoLinesControl.ItemsSource = lines;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
