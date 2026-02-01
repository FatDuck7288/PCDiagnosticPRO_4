namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// UI detail row for evidence data (label/value + optional JSON path).
    /// </summary>
    public class DetailRow
    {
        public string Label { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string? JsonPath { get; set; }
    }
}
