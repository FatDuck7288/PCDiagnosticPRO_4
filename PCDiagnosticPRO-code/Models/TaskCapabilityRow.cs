using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Represents a row in the Task Capability table.
    /// Shows how well the PC can perform a specific task (0-100 score).
    /// Philosophy: If PC meets or exceeds requirements → 100%.
    /// Points are only deducted when missing required capabilities.
    /// </summary>
    public class TaskCapabilityRow
    {
        /// <summary>
        /// The name of the task (e.g., "Bureau / Navigation", "Jeu 1080p")
        /// </summary>
        [JsonPropertyName("taskName")]
        public string TaskName { get; set; } = string.Empty;

        /// <summary>
        /// The task capability score (0-100).
        /// 100 = PC fully meets or exceeds all requirements.
        /// Score decreases when PC is missing capabilities.
        /// </summary>
        [JsonPropertyName("score")]
        public int Score { get; set; }

        /// <summary>
        /// Classification based on score:
        /// - Excellent (90-100): Exceeds all requirements
        /// - Très bien (80-89): Meets all requirements comfortably
        /// - Bien (70-79): Meets recommended requirements
        /// - Acceptable (55-69): Meets minimum requirements
        /// - Insuffisant (40-54): Below minimum
        /// - Non recommandé (0-39): Not suitable for this task
        /// </summary>
        [JsonPropertyName("classification")]
        public string Classification { get; set; } = string.Empty;

        /// <summary>
        /// The component limiting performance for this task.
        /// "Aucun" if PC meets all requirements, otherwise shows the weakest component.
        /// </summary>
        [JsonPropertyName("limitingFactor")]
        public string LimitingFactor { get; set; } = "Aucun";

        /// <summary>
        /// True if the PC meets at least the recommended requirements for this task.
        /// </summary>
        [JsonPropertyName("meetsRequirements")]
        public bool MeetsRequirements { get; set; }

        /// <summary>
        /// The scenario ID (e.g., "office", "gaming_1080p") for internal reference.
        /// </summary>
        [JsonPropertyName("scenarioId")]
        public string ScenarioId { get; set; } = string.Empty;

        /// <summary>
        /// Get classification string from score.
        /// </summary>
        public static string GetClassificationFromScore(int score)
        {
            return score switch
            {
                >= 90 => "Excellent",
                >= 80 => "Très bien",
                >= 70 => "Bien",
                >= 55 => "Acceptable",
                >= 40 => "Insuffisant",
                _ => "Non recommandé"
            };
        }

        /// <summary>
        /// Create a TaskCapabilityRow from scenario evaluation.
        /// </summary>
        public static TaskCapabilityRow Create(string scenarioId, string taskName, int score, string? limitingFactor = null)
        {
            return new TaskCapabilityRow
            {
                ScenarioId = scenarioId,
                TaskName = taskName,
                Score = score,
                Classification = GetClassificationFromScore(score),
                LimitingFactor = string.IsNullOrEmpty(limitingFactor) ? "Aucun" : limitingFactor,
                MeetsRequirements = score >= 70
            };
        }
    }
}
