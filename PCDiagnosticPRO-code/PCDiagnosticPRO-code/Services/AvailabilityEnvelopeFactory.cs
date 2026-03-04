using System;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    public static class AvailabilityEnvelopeFactory
    {
        public static AvailabilityEnvelope<T> Available<T>(
            T value,
            string source,
            MetricConfidence confidence = MetricConfidence.High,
            string? details = null)
        {
            return new AvailabilityEnvelope<T>
            {
                Available = true,
                Value = value,
                Source = string.IsNullOrWhiteSpace(source) ? "unknown" : source,
                Confidence = confidence,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Details = details
            };
        }

        public static AvailabilityEnvelope<T> Unavailable<T>(
            UnavailableReason reason,
            string source,
            MetricConfidence confidence = MetricConfidence.Low,
            string? details = null)
        {
            return new AvailabilityEnvelope<T>
            {
                Available = false,
                Value = default,
                Reason = reason.ToString(),
                Source = string.IsNullOrWhiteSpace(source) ? "unknown" : source,
                Confidence = confidence,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Details = details
            };
        }

        public static AvailabilityEnvelope<T> FromMetric<T>(
            MetricValue<T>? metric,
            string? sourceFallback = null,
            MetricConfidence confidenceFallback = MetricConfidence.Low)
        {
            if (metric == null)
            {
                return Unavailable<T>(UnavailableReason.Unknown, sourceFallback ?? "unknown", confidenceFallback, "metric_null");
            }

            if (metric.Available)
            {
                return Available(
                    metric.Value!,
                    string.IsNullOrWhiteSpace(metric.Source) ? (sourceFallback ?? "unknown") : metric.Source,
                    ParseConfidence(metric.Confidence, confidenceFallback),
                    metric.Details);
            }

            var reason = ParseReason(metric.Reason);
            return Unavailable<T>(
                reason,
                string.IsNullOrWhiteSpace(metric.Source) ? (sourceFallback ?? "unknown") : metric.Source,
                ParseConfidence(metric.Confidence, confidenceFallback),
                metric.Details);
        }

        public static UnavailableReason ParseReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return UnavailableReason.Unknown;

            if (Enum.TryParse<UnavailableReason>(reason, ignoreCase: true, out var parsed))
                return parsed;

            return reason.Trim().ToLowerInvariant() switch
            {
                "notsupported" => UnavailableReason.NotSupported,
                "not_present" => UnavailableReason.NotPresent,
                "notpresent" => UnavailableReason.NotPresent,
                "blockedbysecurity" => UnavailableReason.BlockedBySecurity,
                "accessdenied" => UnavailableReason.PermissionDenied,
                "permissiondenied" => UnavailableReason.PermissionDenied,
                "timeout" => UnavailableReason.Timeout,
                "providermissing" => UnavailableReason.ProviderMissing,
                "parseerror" => UnavailableReason.ParseError,
                _ => UnavailableReason.Unknown
            };
        }

        public static MetricConfidence ParseConfidence(string? confidence, MetricConfidence fallback = MetricConfidence.Low)
        {
            if (string.IsNullOrWhiteSpace(confidence))
                return fallback;

            if (Enum.TryParse<MetricConfidence>(confidence, ignoreCase: true, out var parsed))
                return parsed;

            return confidence.Trim().ToLowerInvariant() switch
            {
                "none" => MetricConfidence.Low,
                "low" => MetricConfidence.Low,
                "medium" => MetricConfidence.Medium,
                "med" => MetricConfidence.Medium,
                "high" => MetricConfidence.High,
                _ => fallback
            };
        }
    }
}
