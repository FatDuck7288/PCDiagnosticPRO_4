using System;

namespace PCDiagnosticPro.Services
{
    public readonly struct ParsedProgressMarker
    {
        public string Phase { get; init; }
        public string Section { get; init; }
        public string Message { get; init; }
        public int? Done { get; init; }
        public int? Total { get; init; }
        public int? Percent { get; init; }
    }

    public static class ProgressMarkerParser
    {
        public static bool TryParseLive(string line, out string message)
        {
            message = string.Empty;
            if (string.IsNullOrWhiteSpace(line) ||
                !line.StartsWith("LIVE|", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var payload = line.Substring("LIVE|".Length).Trim();
            if (string.IsNullOrWhiteSpace(payload))
                return false;

            message = TextEncodingNormalizer.NormalizeIfCorrupted(payload);
            return true;
        }

        public static bool TryParseProgress(string line, out ParsedProgressMarker marker)
        {
            marker = default;
            if (string.IsNullOrWhiteSpace(line) ||
                !line.StartsWith("PROGRESS|", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var payload = line.Substring("PROGRESS|".Length).Trim();
            if (string.IsNullOrWhiteSpace(payload))
                return false;

            string phase = string.Empty;
            string section = string.Empty;
            string message = string.Empty;
            int? done = null;
            int? total = null;
            int? percent = null;

            var parts = payload.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                var idx = part.IndexOf('=');
                if (idx <= 0)
                    continue;

                var key = part[..idx].Trim().ToLowerInvariant();
                var value = part[(idx + 1)..].Trim();

                switch (key)
                {
                    case "phase":
                        phase = value;
                        break;
                    case "section":
                        section = value;
                        break;
                    case "message":
                        message = value;
                        break;
                    case "done":
                        if (int.TryParse(value, out var parsedDone))
                            done = parsedDone;
                        break;
                    case "total":
                        if (int.TryParse(value, out var parsedTotal))
                            total = parsedTotal;
                        break;
                    case "percent":
                    case "pct":
                        value = value.TrimEnd('%');
                        if (int.TryParse(value, out var parsedPercent))
                            percent = Math.Max(0, Math.Min(100, parsedPercent));
                        break;
                }
            }

            marker = new ParsedProgressMarker
            {
                Phase = TextEncodingNormalizer.NormalizeIfCorrupted(phase),
                Section = TextEncodingNormalizer.NormalizeIfCorrupted(section),
                Message = TextEncodingNormalizer.NormalizeIfCorrupted(message),
                Done = done,
                Total = total,
                Percent = percent
            };
            return true;
        }
    }
}
