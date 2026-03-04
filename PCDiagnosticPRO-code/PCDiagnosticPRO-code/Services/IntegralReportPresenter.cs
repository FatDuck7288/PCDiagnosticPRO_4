using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows.Controls;

namespace PCDiagnosticPro.Services
{
    internal static class IntegralReportPresenter
    {
        // TableLayoutPolicy (Rapport Integral uniquement)
        public static DataGridLength TableKeyColumnWidth { get; } = new DataGridLength(38, DataGridLengthUnitType.Star);
        public static DataGridLength TableValueColumnWidth { get; } = new DataGridLength(50, DataGridLengthUnitType.Star);
        public static DataGridLength TableUnitColumnWidth { get; } = new DataGridLength(12, DataGridLengthUnitType.Star);
        public static double TableKeyColumnMinWidth => 280;
        public static double TableValueColumnMinWidth => 360;
        public static double TableUnitColumnMinWidth => 90;
        public static double TableValueMaxWidth => 760;

        internal readonly record struct PresentedRow(string Key, string Value, string Unit);

        public static PresentedRow PresentRow(
            string sectionId,
            string rawKey,
            string rawValue,
            string rawUnit,
            string? rawSource)
        {
            var key = KeyLabelTranslator.Translate(sectionId, rawKey);
            var value = ValueFormatter.Format(rawKey, rawValue);
            var unit = ValueFormatter.FormatUnit(rawUnit);
            return new PresentedRow(key, value, unit);
        }

        public static string TranslateKey(string sectionId, string rawKey) =>
            KeyLabelTranslator.Translate(sectionId, rawKey);

        public static string NormalizeLabelForComparison(string key) =>
            KeyLabelTranslator.NormalizeKeyForComparison(key);

        internal static class KeyLabelTranslator
        {
            private static readonly Dictionary<string, string> SystemMap = new(StringComparer.OrdinalIgnoreCase)
            {
                ["caption"] = "Edition Windows",
                ["version"] = "Version Windows",
                ["buildnumber"] = "Numero de build",
                ["build"] = "Numero de build",
                ["installdate"] = "Date d'installation",
                ["lastboottime"] = "Dernier demarrage",
                ["dernierdemarrage"] = "Dernier demarrage",
                ["uptime"] = "Temps de fonctionnement",
                ["tempsdefonctionnement"] = "Temps de fonctionnement",
                ["architecture"] = "Architecture (x64/x86/ARM64)",
                ["sfcstatus"] = "Verification fichiers systeme (SFC)",
                ["dismhealth"] = "Sante image Windows (DISM)",
                ["boottimeseconds"] = "Temps de demarrage (secondes)",
                ["logintimeseconds"] = "Temps ouverture session (secondes)",
                ["partialfailure"] = "Collecte partielle"
            };

            private static readonly Dictionary<string, string> PlatformMap = new(StringComparer.OrdinalIgnoreCase)
            {
                ["versionbios"] = "Version du BIOS",
                ["datebios"] = "Date du BIOS",
                ["tpm"] = "Presence TPM",
                ["versiontpm"] = "Version TPM",
                ["secureboot"] = "Demarrage securise (Secure Boot)"
            };

            private static readonly Dictionary<string, string> CpuMap = new(StringComparer.OrdinalIgnoreCase)
            {
                ["temperature"] = "Temperature CPU",
                ["cputemp"] = "Temperature CPU",
                ["cpuloadpercent"] = "Charge CPU",
                ["sourcetemperature"] = "Source capteur temperature",
                ["sourcecapteurtemperature"] = "Source capteur temperature",
                ["methodesdecollectetemperature"] = "Methode de lecture capteurs",
                ["methodedelecturecapteurs"] = "Methode de lecture capteurs",
                ["throttlingdetecte"] = "Throttling detecte",
                ["throttling"] = "Statut throttling",
                ["type"] = "Type de diagnostic",
                ["preuves"] = "Indicateurs mesures",
                ["evenementsthrottle7j"] = "Evenements de throttling (7 jours)",
                ["evenementsthrottle30j"] = "Evenements de throttling (30 jours)",
                ["frequencemoymax"] = "Frequence moyenne (% du max)",
                ["frequenceminmax"] = "Frequence minimale (% du max)",
                ["frequenceactuelle"] = "Frequence actuelle"
            };

            private static readonly Dictionary<string, string> GpuMap = new(StringComparer.OrdinalIgnoreCase)
            {
                ["load"] = "Charge GPU",
                ["chargegpu"] = "Charge GPU",
                ["temperature"] = "Temperature GPU",
                ["temperaturegpu"] = "Temperature GPU",
                ["name"] = "Nom GPU",
                ["chargegpu3d"] = "Charge GPU (3D)",
                ["vramutilisee"] = "VRAM dediee utilisee",
                ["vramtotal"] = "VRAM dediee totale",
                ["vramdedicatedused"] = "VRAM dediee utilisee",
                ["vramdedicatedtotal"] = "VRAM dediee totale",
                ["vramdedicatedpct"] = "Utilisation VRAM dediee (%)",
                ["vramdedieeutilisee"] = "VRAM dediee utilisee",
                ["vramdedieetotale"] = "VRAM dediee totale"
            };

            private static readonly Dictionary<string, string> GlobalMap = new(StringComparer.OrdinalIgnoreCase)
            {
                ["runid"] = "ID du run",
                ["timestamp"] = "Horodatage",
                ["schemaversion"] = "Version schema",
                ["scriptversion"] = "Version script PowerShell",
                ["dureescan"] = "Duree du scan",
                ["jsoncombine"] = "Fichier JSON combine",
                ["rapporttxt"] = "Rapport TXT unifie",
                ["jsonpsbrut"] = "JSON PowerShell brut",
                ["txtbrut"] = "Rapport texte brut",
                ["sectionsps"] = "Sections PowerShell",
                ["latencep50"] = "Latence P50",
                ["latencep95"] = "Latence P95",
                ["jitterp95"] = "Jitter P95",
                ["pertepaquets"] = "Perte de paquets",
                ["dnsp95"] = "Latence DNS P95",
                ["pendingps"] = "Mises a jour en attente (PowerShell)",
                ["pendingc"] = "Mises a jour en attente (C#)",
                ["nomgpuc"] = "Nom GPU",
                ["cpups"] = "Processeur (PowerShell)",
                ["ramtotale"] = "RAM totale",
                ["scoreperformance"] = "Score performance",
                ["primarylimitingfactor"] = "Facteur limitant principal",
                ["topcpuprocess"] = "Processus le plus consommateur CPU",
                ["topmemoireprocess"] = "Processus le plus consommateur memoire"
            };

            public static string Translate(string sectionId, string rawKey)
            {
                var cleaned = CleanupKey(rawKey);
                var normalized = NormalizeForLookup(cleaned);
                if (string.IsNullOrWhiteSpace(normalized))
                    return "Champ technique";

                var bySection = ResolveSectionDictionary(sectionId);
                if (bySection != null && bySection.TryGetValue(normalized, out var translated))
                    return translated;

                if (GlobalMap.TryGetValue(normalized, out translated))
                    return translated;

                var humanized = Humanize(cleaned);
                return TranslateHumanized(humanized);
            }

            private static Dictionary<string, string>? ResolveSectionDictionary(string sectionId)
            {
                if (string.Equals(sectionId, "System", StringComparison.OrdinalIgnoreCase))
                    return SystemMap;
                if (string.Equals(sectionId, "PlatformFirmware", StringComparison.OrdinalIgnoreCase))
                    return PlatformMap;
                if (string.Equals(sectionId, "CPU", StringComparison.OrdinalIgnoreCase))
                    return CpuMap;
                if (string.Equals(sectionId, "GPU", StringComparison.OrdinalIgnoreCase))
                    return GpuMap;
                return null;
            }

            private static string CleanupKey(string rawKey)
            {
                if (string.IsNullOrWhiteSpace(rawKey))
                    return string.Empty;

                var key = rawKey.Trim();
                var bracketIndex = key.IndexOf('[');
                if (bracketIndex >= 0)
                    key = key.Substring(0, bracketIndex).Trim();

                return key;
            }

            private static string NormalizeForLookup(string key)
            {
                if (string.IsNullOrWhiteSpace(key))
                    return string.Empty;

                var decomposed = key.Normalize(NormalizationForm.FormD);
                var sb = new StringBuilder(decomposed.Length);

                foreach (var c in decomposed)
                {
                    if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                        continue;

                    if (char.IsLetterOrDigit(c))
                        sb.Append(char.ToLowerInvariant(c));
                }

                return sb.ToString();
            }

            internal static string NormalizeKeyForComparison(string key) =>
                NormalizeForLookup(key);

            private static string Humanize(string key)
            {
                if (string.IsNullOrWhiteSpace(key))
                    return "Champ technique";

                var sb = new StringBuilder();
                for (var i = 0; i < key.Length; i++)
                {
                    var c = key[i];
                    if (i > 0 && char.IsUpper(c))
                    {
                        var prev = key[i - 1];
                        var next = i + 1 < key.Length ? key[i + 1] : '\0';
                        var shouldSplit =
                            char.IsLower(prev) ||
                            char.IsDigit(prev) ||
                            (char.IsUpper(prev) && char.IsLower(next));

                        if (shouldSplit)
                            sb.Append(' ');
                    }

                    if (c == '_' || c == '-' || c == '.')
                        sb.Append(' ');
                    else
                        sb.Append(c);
                }

                var value = sb.ToString().Trim();
                while (value.Contains("  ", StringComparison.Ordinal))
                    value = value.Replace("  ", " ", StringComparison.Ordinal);

                return value;
            }

            private static string TranslateHumanized(string humanized)
            {
                if (string.IsNullOrWhiteSpace(humanized))
                    return "Champ technique";

                var translated = humanized;
                translated = translated.Replace("Build Number", "Numero de build", StringComparison.OrdinalIgnoreCase);
                translated = translated.Replace("Install Date", "Date d'installation", StringComparison.OrdinalIgnoreCase);
                translated = translated.Replace("Last Boot Time", "Dernier demarrage", StringComparison.OrdinalIgnoreCase);
                translated = translated.Replace("Boot Time Seconds", "Temps de demarrage (secondes)", StringComparison.OrdinalIgnoreCase);
                translated = translated.Replace("Login Time Seconds", "Temps ouverture session (secondes)", StringComparison.OrdinalIgnoreCase);
                translated = translated.Replace("Uptime", "Temps de fonctionnement", StringComparison.OrdinalIgnoreCase);
                translated = translated.Replace("Caption", "Edition Windows", StringComparison.OrdinalIgnoreCase);

                return char.ToUpperInvariant(translated[0]) + translated.Substring(1);
            }
        }

        internal static class ValueFormatter
        {
            public static string Format(string rawKey, string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return "Non disponible";

                if (value.StartsWith("Indisponible", StringComparison.OrdinalIgnoreCase))
                    return value;

                if (value.StartsWith("Non disponible", StringComparison.OrdinalIgnoreCase))
                    return value;

                if (LooksLikeDateTime(rawKey) && DateTimeOffset.TryParse(value, out var dto))
                    return dto.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.GetCultureInfo("fr-FR"));

                return value;
            }

            public static string FormatUnit(string unit)
            {
                if (string.IsNullOrWhiteSpace(unit))
                    return string.Empty;

                return unit;
            }

            private static bool LooksLikeDateTime(string rawKey)
            {
                if (string.IsNullOrWhiteSpace(rawKey))
                    return false;

                var normalized = rawKey.Normalize(NormalizationForm.FormD);
                var sb = new StringBuilder(normalized.Length);
                foreach (var c in normalized)
                {
                    if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                        continue;
                    sb.Append(char.ToLowerInvariant(c));
                }

                var key = sb.ToString();
                return key.Contains("date", StringComparison.Ordinal) ||
                       key.Contains("time", StringComparison.Ordinal) ||
                       key.Contains("timestamp", StringComparison.Ordinal) ||
                       key.Contains("demarrage", StringComparison.Ordinal);
            }
        }

        internal static class SourceConfidenceFormatter
        {
            public static string AppendSource(string value, string? rawSource, string key)
            {
                if (string.IsNullOrWhiteSpace(value) ||
                    value.Equals("Non disponible", StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }

                if (string.IsNullOrWhiteSpace(rawSource))
                    return value;

                if (key.Contains("Source", StringComparison.OrdinalIgnoreCase))
                    return value;

                if (value.Contains("(source:", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("(provenance:", StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }

                var source = ToFriendlySource(rawSource);
                if (string.IsNullOrWhiteSpace(source))
                    return value;

                var confidenceIdx = value.LastIndexOf("(confiance:", StringComparison.OrdinalIgnoreCase);
                if (confidenceIdx < 0)
                    confidenceIdx = value.LastIndexOf("(confidence:", StringComparison.OrdinalIgnoreCase);

                if (confidenceIdx >= 0 && value.EndsWith(")", StringComparison.Ordinal))
                {
                    if (value.Contains("provenance:", StringComparison.OrdinalIgnoreCase))
                        return value;

                    return value.Insert(value.Length - 1, $", provenance: {source}");
                }

                return $"{value} (source: {source})";
            }

            private static string? ToFriendlySource(string rawSource)
            {
                var source = rawSource.Trim();
                var lower = source.ToLowerInvariant();

                if (lower.Contains("scan_powershell") || lower == "ps")
                    return "PowerShell";
                if (lower.StartsWith("ps/") || lower.Contains("ps/"))
                    return "PowerShell";
                if (lower.Contains("librehardware") || lower.Contains("lhm"))
                    return "LibreHardwareMonitor";
                if (lower.Contains("diagnostic_signals"))
                    return "Signaux diagnostic";
                if (lower.Contains("diagnostic_snapshot"))
                    return "Snapshot diagnostique";
                if (lower.Contains("hardwaresensorsresult") || lower.Contains("hardwaresensorscollector") || lower.Contains("c#"))
                    return "C#";
                if (lower.Contains("windowsupdate"))
                    return "Windows Update";
                if (lower.Contains("driverinventory"))
                    return "Inventaire pilotes";
                if (lower.Contains("securityinfocollector"))
                    return "Securite C#";
                if (lower.Contains("networkdiagnostics"))
                    return "Reseau C#";
                if (lower.Contains("event_logs") || lower.Contains("eventlogs"))
                    return "Journaux evenements";
                if (lower.Contains("minidumps"))
                    return "Minidumps";

                if (source.Length <= 32)
                    return source;

                return null;
            }
        }
    }
}
