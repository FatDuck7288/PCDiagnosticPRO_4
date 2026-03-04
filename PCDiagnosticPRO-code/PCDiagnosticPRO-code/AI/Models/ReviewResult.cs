using System.Collections.Generic;

namespace PCDiagnosticPro.AI.Models
{
    /// <summary>Output of <see cref="PCDiagnosticPro.AI.Agents.CodeReviewerAgent"/>.</summary>
    public class ReviewResult
    {
        public string RevisedScriptText { get; set; } = "";
        public List<string> Fixes { get; set; } = new();
        public List<string> Notes { get; set; } = new();
        public List<string> Checklist { get; set; } = new();
    }
}
