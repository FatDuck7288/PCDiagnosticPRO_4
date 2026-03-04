using System;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Centralizes CPU temperature metadata contract used by UI and JSON export.
    /// Contract:
    /// - Source: LHM | ACPI | None
    /// - Confidence: High | Low | None
    /// - ReasonIfMissing: NotSupported | BlockedBySecurity | NoSensors | AccessDenied | Error
    /// </summary>
    public static class CpuTemperatureMetadataService
    {
        public const string SourceLhm = "LHM";
        public const string SourceAcpi = "ACPI";
        public const string SourceNone = "None";

        public const string ConfidenceHigh = "High";
        public const string ConfidenceLow = "Low";
        public const string ConfidenceNone = "None";

        public const string ReasonNotSupported = "NotSupported";
        public const string ReasonBlockedBySecurity = "BlockedBySecurity";
        public const string ReasonNoSensors = "NoSensors";
        public const string ReasonAccessDenied = "AccessDenied";
        // Legacy value kept for backward compatibility with older persisted payloads.
        public const string ReasonNoSensor = "NoSensor";
        public const string ReasonError = "Error";

        private static readonly object UiSnapshotLock = new();
        private static CpuTemperatureUiSnapshot _lastUiSnapshot = new CpuTemperatureUiSnapshot();

        public sealed class CpuTemperatureUiSnapshot
        {
            public double? TemperatureC { get; init; }
            public string Source { get; init; } = SourceNone;
            public string Confidence { get; init; } = ConfidenceNone;
            public string? ReasonCode { get; init; }
            public string? ReasonDetail { get; init; }
        }

        public static void SetAvailableFromLhm(CpuMetrics cpu, string? sourceDetail)
        {
            if (cpu == null) return;
            cpu.CpuTempSource = SourceLhm;
            cpu.CpuTempConfidence = ConfidenceHigh;
            cpu.CpuTempReasonIfMissing = null;
            cpu.CpuTempSourceDetail = string.IsNullOrWhiteSpace(sourceDetail) ? SourceLhm : sourceDetail;
            PublishUiSnapshot(null, cpu.CpuTempSource, cpu.CpuTempConfidence, null, null);
        }

        public static void SetAvailableFromAcpi(CpuMetrics cpu, string? sourceDetail)
        {
            if (cpu == null) return;
            cpu.CpuTempSource = SourceAcpi;
            cpu.CpuTempConfidence = ConfidenceLow;
            cpu.CpuTempReasonIfMissing = null;
            cpu.CpuTempSourceDetail = string.IsNullOrWhiteSpace(sourceDetail) ? SourceAcpi : sourceDetail;
            PublishUiSnapshot(null, cpu.CpuTempSource, cpu.CpuTempConfidence, null, null);
        }

        public static void SetUnavailable(CpuMetrics cpu, string reasonCode, string? sourceDetail = null)
        {
            if (cpu == null) return;
            cpu.CpuTempSource = SourceNone;
            cpu.CpuTempConfidence = ConfidenceNone;
            cpu.CpuTempReasonIfMissing = NormalizeReasonCode(reasonCode);
            cpu.CpuTempSourceDetail = sourceDetail ?? SourceNone;
            PublishUiSnapshot(null, cpu.CpuTempSource, cpu.CpuTempConfidence, cpu.CpuTempReasonIfMissing, cpu.CpuTempC?.Reason);
        }

        public static void PublishUiSnapshot(
            double? temperatureC,
            string? source,
            string? confidence,
            string? reasonCode,
            string? reasonDetail)
        {
            lock (UiSnapshotLock)
            {
                _lastUiSnapshot = new CpuTemperatureUiSnapshot
                {
                    TemperatureC = temperatureC,
                    Source = string.IsNullOrWhiteSpace(source) ? SourceNone : source!,
                    Confidence = string.IsNullOrWhiteSpace(confidence) ? ConfidenceNone : confidence!,
                    ReasonCode = string.IsNullOrWhiteSpace(reasonCode) ? null : NormalizeReasonCode(reasonCode),
                    ReasonDetail = reasonDetail
                };
            }
        }

        public static CpuTemperatureUiSnapshot GetLastUiSnapshot()
        {
            lock (UiSnapshotLock)
            {
                return new CpuTemperatureUiSnapshot
                {
                    TemperatureC = _lastUiSnapshot.TemperatureC,
                    Source = _lastUiSnapshot.Source,
                    Confidence = _lastUiSnapshot.Confidence,
                    ReasonCode = _lastUiSnapshot.ReasonCode,
                    ReasonDetail = _lastUiSnapshot.ReasonDetail
                };
            }
        }

        public static string ClassifyReasonCode(string? reason, bool blockedBySecurity = false)
        {
            if (blockedBySecurity)
                return ReasonBlockedBySecurity;

            if (string.IsNullOrWhiteSpace(reason))
                return ReasonNoSensors;

            var r = reason.Trim().ToLowerInvariant();
            if (r == ReasonNoSensor.ToLowerInvariant() || r == ReasonNoSensors.ToLowerInvariant())
                return ReasonNoSensors;

            if (r.Contains("access denied", StringComparison.Ordinal) ||
                r.Contains("accessdenied", StringComparison.Ordinal) ||
                r.Contains("unauthorized", StringComparison.Ordinal) ||
                r.Contains("permission", StringComparison.Ordinal) ||
                r.Contains("refus", StringComparison.Ordinal))
            {
                return ReasonAccessDenied;
            }

            if (r.Contains("defender", StringComparison.Ordinal) ||
                r.Contains("security", StringComparison.Ordinal) ||
                r.Contains("winring", StringComparison.Ordinal) ||
                r.Contains("blocked", StringComparison.Ordinal))
            {
                return ReasonBlockedBySecurity;
            }

            if (r.Contains("not support", StringComparison.Ordinal) ||
                r.Contains("namespace_not_available", StringComparison.Ordinal) ||
                r.Contains("non support", StringComparison.Ordinal))
            {
                return ReasonNotSupported;
            }

            if (r.Contains("error", StringComparison.Ordinal) ||
                r.Contains("exception", StringComparison.Ordinal) ||
                r.Contains("erreur", StringComparison.Ordinal))
            {
                return ReasonError;
            }

            if (r.Contains("no_valid", StringComparison.Ordinal) ||
                r.Contains("no sensor", StringComparison.Ordinal) ||
                r.Contains("aucun capteur", StringComparison.Ordinal) ||
                r.Contains("indisponible", StringComparison.Ordinal))
            {
                return ReasonNoSensors;
            }

            return ReasonNoSensors;
        }

        public static string NormalizeReasonCode(string? reasonCode)
        {
            if (string.IsNullOrWhiteSpace(reasonCode))
                return ReasonNoSensors;

            if (reasonCode.Equals(ReasonNoSensor, StringComparison.OrdinalIgnoreCase))
                return ReasonNoSensors;

            if (reasonCode.Equals(ReasonNoSensors, StringComparison.OrdinalIgnoreCase))
                return ReasonNoSensors;

            if (reasonCode.Equals(ReasonAccessDenied, StringComparison.OrdinalIgnoreCase))
                return ReasonAccessDenied;

            if (reasonCode.Equals(ReasonBlockedBySecurity, StringComparison.OrdinalIgnoreCase))
                return ReasonBlockedBySecurity;

            if (reasonCode.Equals(ReasonNotSupported, StringComparison.OrdinalIgnoreCase))
                return ReasonNotSupported;

            if (reasonCode.Equals(ReasonError, StringComparison.OrdinalIgnoreCase))
                return ReasonError;

            return ClassifyReasonCode(reasonCode);
        }
    }
}
