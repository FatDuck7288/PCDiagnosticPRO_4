using System.Data;

namespace PCDiagnosticPro.Models
{
    public class ResultTable
    {
        public string Title { get; set; } = string.Empty;
        public DataTable Table { get; set; } = new DataTable();
        public DataView View => Table.DefaultView;
    }
}
