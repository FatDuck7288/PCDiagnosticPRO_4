using System;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    public static class AvailabilityDisplayService
    {
        public static string ToPrimaryLabel<T>(AvailabilityEnvelope<T>? envelope)
        {
            if (envelope == null || !envelope.Available)
                return "Indisponible";

            if (envelope.Value == null)
                return "Indisponible";

            return envelope.Value.ToString() ?? "Indisponible";
        }

        public static string ToReasonLabel<T>(AvailabilityEnvelope<T>? envelope)
        {
            if (envelope == null || envelope.Available)
                return string.Empty;

            var reason = string.IsNullOrWhiteSpace(envelope.Reason) ? "Unknown" : envelope.Reason!;
            return reason switch
            {
                "NotSupported" => "Non supporté",
                "NotPresent" => "Absent",
                "BlockedBySecurity" => "Bloqué sécurité",
                "PermissionDenied" => "Permission refusée",
                "Timeout" => "Délai dépassé",
                "ProviderMissing" => "Fournisseur absent",
                "ParseError" => "Erreur de lecture",
                _ => "Inconnu"
            };
        }

        public static string ToTooltip<T>(AvailabilityEnvelope<T>? envelope)
        {
            if (envelope == null)
                return "Indisponible";

            var source = string.IsNullOrWhiteSpace(envelope.Source) ? "unknown" : envelope.Source;
            var confidence = envelope.Confidence.ToString();
            var reason = envelope.Available ? "N/A" : ToReasonLabel(envelope);
            return $"Source: {source} | Confiance: {confidence} | Raison: {reason}";
        }
    }
}
