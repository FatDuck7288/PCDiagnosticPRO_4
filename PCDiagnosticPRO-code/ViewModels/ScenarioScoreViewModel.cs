namespace PCDiagnosticPro.ViewModels
{
    /// <summary>
    /// Single scenario row for Performance section bar chart and capability matrix.
    /// </summary>
    public class ScenarioScoreViewModel
    {
        public string Name { get; set; } = "";
        public int Score { get; set; }
        public string Classification { get; set; } = "";
    }
}
