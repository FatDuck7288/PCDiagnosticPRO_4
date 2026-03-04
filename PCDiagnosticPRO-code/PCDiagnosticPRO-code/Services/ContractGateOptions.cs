namespace PCDiagnosticPro.Services
{
    public sealed class ContractGateOptions
    {
        public const double DefaultUiCoverageThresholdPercent = 70d;

        public double UiCoverageThresholdPercent { get; init; } = DefaultUiCoverageThresholdPercent;
    }
}
