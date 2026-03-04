using System.Collections.ObjectModel;

namespace PCDiagnosticPro.Models
{
    public class ResultSection
    {
        public string Title { get; set; } = string.Empty;
        public ObservableCollection<ResultField> Fields { get; } = new ObservableCollection<ResultField>();
        public ObservableCollection<ResultTable> Tables { get; } = new ObservableCollection<ResultTable>();
    }
}
