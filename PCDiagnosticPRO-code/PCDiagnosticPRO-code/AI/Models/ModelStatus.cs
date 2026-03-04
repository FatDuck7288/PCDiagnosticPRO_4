namespace PCDiagnosticPro.AI.Models
{
    public enum ModelStatus
    {
        NotInstalled,
        InvalidPath,
        Loading,
        Ready,
        Error
    }

    public sealed class ModelValidationResult
    {
        public ModelStatus Status { get; set; } = ModelStatus.NotInstalled;
        public string Message { get; set; } = "Model not configured.";
        public string NormalizedPath { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public bool CanRead { get; set; }
        public string? OptionalSha256 { get; set; }

        public bool IsValid => Status == ModelStatus.Ready || Status == ModelStatus.Loading;
    }
}
