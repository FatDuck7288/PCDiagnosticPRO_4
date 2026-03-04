using System;

namespace PCDiagnosticPro.Models
{
    public enum InfoContextId
    {
        Unknown = 0,
        DiskTemp,
        SMARTHealth,
        RestorePoints,
        TDR,
        WHEA,
        VRAM,
        GPULoad,
        CPUTemperature,
        CPUThrottle,
        KernelPower,
        RebootRequired,
        UpdatesPending,
        BSOD,
        NetworkLoss,
        SecurityAntivirus,
        SecurityFirewall,
        SecuritySecureBoot,
        SecurityBitLocker,
        SecurityUac,
        SecuritySmbV1,
        SecurityTamperProtection,
        SecurityRealTimeProtection,
        SecurityVbs,
        SecurityCredentialGuard,
        SecurityMemoryIntegrity,
        SecurityAsr
    }

    public enum InfoSectionId
    {
        Unknown = 0,
        OS,
        CPU,
        GPU,
        RAM,
        Storage,
        Network,
        SystemStability,
        Drivers,
        Applications,
        Performance,
        Security,
        Power
    }

    public enum InfoSeverity
    {
        Info = 0,
        Warning,
        Danger
    }

    public enum InfoConfidence
    {
        High = 0,
        Medium,
        Low,
        None
    }

    public enum InfoTone
    {
        Neutral = 0,
        Info,
        Warning,
        Danger,
        Action
    }

    public sealed class InfoEvidence
    {
        public int? EventCount { get; set; }
        public double? Threshold { get; set; }
        public DateTime? LastSeen { get; set; }
        public string? Source { get; set; }
        public bool? MismatchFlag { get; set; }
    }

    public sealed class InfoContext
    {
        public InfoContextId ContextId { get; set; } = InfoContextId.Unknown;
        public InfoSectionId SectionId { get; set; } = InfoSectionId.Unknown;
        public string MetricLabel { get; set; } = string.Empty;
        public object? Value { get; set; }
        public string? Unit { get; set; }
        public InfoSeverity Severity { get; set; } = InfoSeverity.Info;
        public InfoConfidence Confidence { get; set; } = InfoConfidence.None;
        public InfoEvidence Evidence { get; set; } = new();
    }

    public sealed class InfoLine
    {
        public string Emoji { get; set; } = string.Empty;
        public string? Label { get; set; }
        public string Text { get; set; } = string.Empty;
        public InfoTone Tone { get; set; } = InfoTone.Neutral;

        public string Prefix => string.IsNullOrWhiteSpace(Label) ? string.Empty : $"{Label} : ";
    }
}
