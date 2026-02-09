using System.Collections.ObjectModel;

namespace PCDiagnosticPro.ViewModels
{
    /// <summary>
    /// Section du rapport intégral : titre, niveau, résumé, tableau clé/valeur, issues, preuves.
    /// </summary>
    public class ReportSectionViewModel : ViewModelBase
    {
        private string _id = "";
        private string _title = "";
        private IssueLevel _level = IssueLevel.Info;
        private bool _hasCritical;
        private double _sectionScore = -1;
        private string _summaryLine1 = "";
        private string _summaryLine2 = "";
        private string _evidenceText = "";
        private string _notesText = "";

        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value ?? "");
        }

        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value ?? "");
        }

        public IssueLevel Level
        {
            get => _level;
            set => SetProperty(ref _level, value);
        }

        public bool HasCritical
        {
            get => _hasCritical;
            set => SetProperty(ref _hasCritical, value);
        }

        /// <summary>Score de la section 0–100 (pour bordure colorée : or 100, vert 70+, jaune 60–70, rouge &lt;60).</summary>
        public double SectionScore
        {
            get => _sectionScore;
            set => SetProperty(ref _sectionScore, value);
        }

        public string SummaryLine1
        {
            get => _summaryLine1;
            set => SetProperty(ref _summaryLine1, value ?? "");
        }

        public string SummaryLine2
        {
            get => _summaryLine2;
            set => SetProperty(ref _summaryLine2, value ?? "");
        }

        public ObservableCollection<KeyValueRow> KeyValues { get; } = new();
        public ObservableCollection<ReportIssue> Issues { get; } = new();
        public ObservableCollection<ReportTableViewModel> Tables { get; } = new();

        public string EvidenceText
        {
            get => _evidenceText;
            set => SetProperty(ref _evidenceText, value ?? "");
        }

        public string NotesText
        {
            get => _notesText;
            set => SetProperty(ref _notesText, value ?? "");
        }
    }
}
