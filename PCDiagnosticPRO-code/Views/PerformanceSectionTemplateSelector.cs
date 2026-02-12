using System.Windows;
using System.Windows.Controls;
using PCDiagnosticPro.ViewModels;

namespace PCDiagnosticPro.Views
{
    /// <summary>
    /// Chooses Performance-specific template (with bar chart) when section Id is "Performance", else default section template.
    /// </summary>
    public class PerformanceSectionTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? DefaultTemplate { get; set; }
        public DataTemplate? PerformanceTemplate { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        {
            if (item is ReportSectionViewModel section && section.Id == "Performance" && PerformanceTemplate != null)
                return PerformanceTemplate;
            return DefaultTemplate;
        }
    }
}
