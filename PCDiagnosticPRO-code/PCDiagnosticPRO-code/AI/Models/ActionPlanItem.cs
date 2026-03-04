namespace PCDiagnosticPro.AI.Models
{
    public enum ActionPlanCategory
    {
        Manual,
        AutoFix
    }

    public sealed class ActionPlanItem
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Severity { get; set; } = "info";
        public ActionPlanCategory Category { get; set; } = ActionPlanCategory.Manual;
        public bool RequiresAdmin { get; set; }
        public bool IsAutoFix => Category == ActionPlanCategory.AutoFix;
    }
}
