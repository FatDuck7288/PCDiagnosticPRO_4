using System.Windows.Controls;

namespace PCDiagnosticPro.Views
{
    /// <summary>
    /// Code-behind pour FullReportView.
    /// FIX: Le contenu utilise maintenant un ContentControl bindé à SelectedSection.
    /// Plus besoin de BringIntoView — le clic sidebar change immédiatement la section affichée.
    /// </summary>
    public partial class FullReportView : UserControl
    {
        public FullReportView()
        {
            InitializeComponent();
        }
    }
}
