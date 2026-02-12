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

        /// <summary>Scenario scores for Performance section (bar chart and capability matrix).</summary>
        public ObservableCollection<ScenarioScoreViewModel> ScenarioScores { get; } = new();

        /// <summary>Performance section only: system category label (Entry-Level / Mid-Range / High-End / Workstation Grade).</summary>
        public string PerformanceCategory { get; set; } = "";

        /// <summary>Performance section only: primary limiting factor (CPU / GPU / RAM / Storage / None significant).</summary>
        public string PrimaryBottleneck { get; set; } = "";

        /// <summary>Performance section only: evidence block – CPU spec (model or tier).</summary>
        public string PerformanceCpuDisplay { get; set; } = "";
        /// <summary>Performance section only: evidence block – GPU spec (model or tier).</summary>
        public string PerformanceGpuDisplay { get; set; } = "";
        /// <summary>Performance section only: evidence block – VRAM dedicated (e.g. "8192 MB").</summary>
        public string PerformanceVramDisplay { get; set; } = "";
        /// <summary>Performance section only: evidence block – RAM (e.g. "16 GB").</summary>
        public string PerformanceRamDisplay { get; set; } = "";
        /// <summary>Performance section only: evidence block – storage type (HDD/SATA_SSD/NVMe).</summary>
        public string PerformanceStorageDisplay { get; set; } = "";

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
