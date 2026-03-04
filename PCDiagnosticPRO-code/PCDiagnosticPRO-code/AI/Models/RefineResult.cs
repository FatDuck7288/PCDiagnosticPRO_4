using System.Collections.Generic;

namespace PCDiagnosticPro.AI.Models
{
    /// <summary>Output of <see cref="PCDiagnosticPro.AI.Agents.CodeRefinerAgent"/>.</summary>
    public class RefineResult
    {
        public string RefinedScriptText { get; set; } = "";
        public List<string> StyleFixes { get; set; } = new();
        public List<string> Validations { get; set; } = new();
        public List<string> LoggingAdded { get; set; } = new();
        public bool SyntaxValid { get; set; } = true;
        public string SyntaxError { get; set; } = string.Empty;
    }
}
