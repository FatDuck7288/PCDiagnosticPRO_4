using System.Collections.Generic;

namespace PCDiagnosticPro.AI.Models
{
    /// <summary>Output of <see cref="PCDiagnosticPro.AI.Agents.ScriptBuilderAgent"/>.</summary>
    public class ScriptDraft
    {
        public string ScriptText { get; set; } = "";
        public List<string> Assumptions { get; set; } = new();
        public List<string> Risks { get; set; } = new();
        public List<string> Rollback { get; set; } = new();
        public bool RequiresAdmin { get; set; }
        public List<string> Capabilities { get; set; } = new();
    }
}
