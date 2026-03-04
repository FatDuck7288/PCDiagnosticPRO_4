using System;
using System.Collections.Generic;

namespace PCDiagnosticPro.AI.Models
{
    /// <summary>
    /// Compact representation of a scan run, sized to fit inside the LLM context window.
    /// Built by <see cref="PCDiagnosticPro.AI.ContextPackBuilder"/>.
    /// </summary>
    public class ContextPack
    {
        public string RunId { get; set; } = string.Empty;
        public string ScanDate { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<string> KeyFindings { get; set; } = new();
        public List<string> TablesCompact { get; set; } = new();
        public List<string> SourcesUsed { get; set; } = new();
        public List<string> ExcludedSections { get; set; } = new();
        public Dictionary<string, int> BudgetBySection { get; set; } = new();
        public string CoverageSummary { get; set; } = string.Empty;
        public int EstimatedTokens { get; set; }

        // ── Truncation metadata ──────────────────────────────────────────────
        /// <summary>Total findings before token-budget trimming.</summary>
        public int TotalFindingsCount { get; set; }
        /// <summary>Total tables before token-budget trimming.</summary>
        public int TotalTablesCount { get; set; }
        /// <summary>True when findings or tables were dropped due to token budget.</summary>
        public bool Truncated { get; set; }
        /// <summary>Number of findings excluded (TotalFindingsCount - KeyFindings.Count).</summary>
        public int ExcludedFindingsCount => Truncated ? Math.Max(0, TotalFindingsCount - KeyFindings.Count) : 0;

        /// <summary>Renders a compact text block suitable for prompt injection.</summary>
        public string ToPromptText()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"## Scan Report - {RunId} ({ScanDate})");
            sb.AppendLine();
            sb.AppendLine("### Summary");
            sb.AppendLine(Summary);
            if (KeyFindings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### Key Findings");
                foreach (var finding in KeyFindings)
                {
                    sb.AppendLine($"- {finding}");
                }
            }

            if (TablesCompact.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### Hardware And Security Data");
                foreach (var row in TablesCompact)
                {
                    sb.AppendLine(row);
                }
            }

            if (SourcesUsed.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"*Sources: {string.Join(", ", SourcesUsed)}*");
            }

            if (!string.IsNullOrWhiteSpace(CoverageSummary))
            {
                sb.AppendLine();
                sb.AppendLine($"*Coverage: {CoverageSummary}*");
            }

            return sb.ToString();
        }
    }
}
