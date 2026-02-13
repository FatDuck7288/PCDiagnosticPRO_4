using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.ViewModels
{
    /// <summary>
    /// Single scenario row for Performance section bar chart and capability matrix.
    /// Includes decimal precision for scores and optional explanation.
    /// </summary>
    public class ScenarioScoreViewModel
    {
        public string Name { get; set; } = "";
        
        /// <summary>
        /// Score with decimal precision (0-100).
        /// Display shows this value; progress bar uses it scaled to 0-1.
        /// </summary>
        public double Score { get; set; }

        /// <summary>
        /// Integer score for backwards compatibility.
        /// </summary>
        public int ScoreInt => (int)System.Math.Round(Score);

        public string Classification { get; set; } = "";

        /// <summary>
        /// Optional explanation of how the score was calculated.
        /// </summary>
        public ScoreExplanation? Explanation { get; set; }

        /// <summary>
        /// Tooltip text showing score breakdown.
        /// </summary>
        public string ExplanationTooltip => Explanation?.ToTooltip() ?? $"Score: {Score:F1}\nClassification: {Classification}";

        /// <summary>
        /// Formatted score display (e.g., "92.4" or "92.4/100").
        /// </summary>
        public string ScoreDisplay => Score >= 99.95 ? "100" : $"{Score:F1}";
    }
}
