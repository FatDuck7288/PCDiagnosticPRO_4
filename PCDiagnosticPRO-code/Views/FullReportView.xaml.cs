using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PCDiagnosticPro.Views
{
    /// <summary>
    /// Code-behind pour FullReportView.
    /// FIX: PreviewMouseWheel redirige la molette vers le ScrollViewer quand le focus est dans un enfant (DataGrid, etc.).
    /// </summary>
    public partial class FullReportView : UserControl
    {
        public FullReportView()
        {
            InitializeComponent();
        }

        private void UserControl_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;
            var sv = ContentScroll;
            if (sv == null) return;
            // Ne rediriger que si la souris est au-dessus de la zone de contenu (ScrollViewer)
            var src = e.OriginalSource as DependencyObject;
            if (src != null && !IsVisualChildOf(sv, src))
                return;
            double step = e.Delta > 0 ? 60 : -60;
            double offset = sv.VerticalOffset - step;
            offset = System.Math.Max(0, System.Math.Min(sv.ScrollableHeight, offset));
            if (System.Math.Abs(offset - sv.VerticalOffset) > 0.01)
            {
                sv.ScrollToVerticalOffset(offset);
                e.Handled = true;
            }
        }

        private static bool IsVisualChildOf(DependencyObject parent, DependencyObject child)
        {
            var p = VisualTreeHelper.GetParent(child);
            while (p != null)
            {
                if (p == parent) return true;
                p = VisualTreeHelper.GetParent(p);
            }
            return false;
        }
    }
}
