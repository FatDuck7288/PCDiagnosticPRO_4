using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    public sealed class InfoContextResolver
    {
        private static readonly Regex NumberRegex = new(@"-?\d+(?:[.,]\d+)?", RegexOptions.Compiled);
        private static readonly Regex PercentRegex = new(@"(?<value>\d+(?:[.,]\d+)?)\s*%", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CelsiusRegex = new(@"(?<value>\d+(?:[.,]\d+)?)\s*(?:[°º])?\s*C", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool SupportsMetricKey(string? key) =>
            ResolveContextIdFromKey(key) != InfoContextId.Unknown;

        public InfoContext ResolveFromMetric(HealthSection section, EvidenceItem item)
        {
            var key = TextEncodingNormalizer.Normalize(item.Key);
            var value = TextEncodingNormalizer.Normalize(item.Value);
            var contextId = ResolveContextIdFromKey(key);

            var context = BuildBaseContext(section, contextId, key);
            PopulateValueAndSeverity(context, value, section);
            return context;
        }

        public InfoContext ResolveFromSection(HealthSection section, InfoContextId forcedContextId)
        {
            var metricLabel = forcedContextId switch
            {
                InfoContextId.DiskTemp => "Température disque",
                InfoContextId.CPUTemperature => "Température CPU",
                InfoContextId.KernelPower => "Kernel-Power",
                InfoContextId.RestorePoints => "Points de restauration",
                InfoContextId.UpdatesPending => "Updates Windows",
                InfoContextId.SecurityAntivirus => "Antivirus",
                InfoContextId.SecurityFirewall => "Pare-feu",
                InfoContextId.SecuritySecureBoot => "Secure Boot",
                InfoContextId.SecurityBitLocker => "BitLocker",
                InfoContextId.SecurityUac => "UAC",
                InfoContextId.SecuritySmbV1 => "SMBv1",
                InfoContextId.SecurityTamperProtection => "Protection contre altération",
                InfoContextId.SecurityRealTimeProtection => "Protection en temps réel",
                InfoContextId.SecurityVbs => "VBS",
                InfoContextId.SecurityCredentialGuard => "Credential Guard",
                InfoContextId.SecurityMemoryIntegrity => "Intégrité mémoire",
                InfoContextId.SecurityAsr => "Règles ASR",
                _ => section.DisplayName
            };

            var context = BuildBaseContext(section, forcedContextId, metricLabel);
            var rawValue = SelectSectionValueForContext(section, forcedContextId);
            PopulateValueAndSeverity(context, rawValue, section);
            return context;
        }

        private static InfoContext BuildBaseContext(HealthSection section, InfoContextId contextId, string label)
        {
            return new InfoContext
            {
                ContextId = contextId,
                SectionId = MapSectionId(section.Domain),
                MetricLabel = string.IsNullOrWhiteSpace(label) ? "Information" : label,
                Confidence = InfoConfidence.None,
                Severity = InfoSeverity.Info
            };
        }

        private static InfoContextId ResolveContextIdFromKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return InfoContextId.Unknown;

            var normalized = SimplifyKey(key);

            if (normalized.Contains("tdr", StringComparison.Ordinal))
                return InfoContextId.TDR;
            if (normalized.Contains("whea", StringComparison.Ordinal))
                return InfoContextId.WHEA;
            if (normalized.Contains("bsod", StringComparison.Ordinal))
                return InfoContextId.BSOD;
            if (normalized.Contains("kernel", StringComparison.Ordinal) && normalized.Contains("power", StringComparison.Ordinal))
                return InfoContextId.KernelPower;
            if ((normalized.Contains("point", StringComparison.Ordinal) && normalized.Contains("restauration", StringComparison.Ordinal)) ||
                (normalized.Contains("restore", StringComparison.Ordinal) && normalized.Contains("point", StringComparison.Ordinal)))
                return InfoContextId.RestorePoints;
            if (normalized.Contains("smart", StringComparison.Ordinal))
                return InfoContextId.SMARTHealth;
            if ((normalized.Contains("temp", StringComparison.Ordinal) && normalized.Contains("disque", StringComparison.Ordinal)) ||
                normalized.Contains("tempmax", StringComparison.Ordinal))
                return InfoContextId.DiskTemp;
            if ((normalized.Contains("temp", StringComparison.Ordinal) || normalized.Contains("temperature", StringComparison.Ordinal)) &&
                (normalized.Contains("cpu", StringComparison.Ordinal) || normalized.Contains("processeur", StringComparison.Ordinal)))
                return InfoContextId.CPUTemperature;
            if (normalized.Contains("vram", StringComparison.Ordinal))
                return InfoContextId.VRAM;
            if ((normalized.Contains("charge", StringComparison.Ordinal) && normalized.Contains("gpu", StringComparison.Ordinal)) ||
                normalized.Contains("gpu load", StringComparison.Ordinal))
                return InfoContextId.GPULoad;
            if (normalized.Contains("throttling", StringComparison.Ordinal))
                return InfoContextId.CPUThrottle;
            if (normalized.Contains("redemarrage", StringComparison.Ordinal) || normalized.Contains("reboot", StringComparison.Ordinal))
                return InfoContextId.RebootRequired;
            if ((normalized.Contains("updates", StringComparison.Ordinal) && normalized.Contains("windows", StringComparison.Ordinal)) ||
                normalized.Contains("mise a jour", StringComparison.Ordinal))
                return InfoContextId.UpdatesPending;
            if (normalized.Contains("antivirus", StringComparison.Ordinal))
                return InfoContextId.SecurityAntivirus;
            if (normalized.Contains("pare feu", StringComparison.Ordinal) || normalized.Contains("firewall", StringComparison.Ordinal))
                return InfoContextId.SecurityFirewall;
            if (normalized.Contains("secure boot", StringComparison.Ordinal))
                return InfoContextId.SecuritySecureBoot;
            if (normalized.Contains("bitlocker", StringComparison.Ordinal))
                return InfoContextId.SecurityBitLocker;
            if (normalized.Equals("uac", StringComparison.Ordinal) ||
                normalized.Contains("controle de compte", StringComparison.Ordinal))
                return InfoContextId.SecurityUac;
            if (normalized.Contains("smbv1", StringComparison.Ordinal) ||
                (normalized.Contains("smb", StringComparison.Ordinal) && normalized.Contains("v1", StringComparison.Ordinal)))
                return InfoContextId.SecuritySmbV1;
            if (normalized.Contains("tamper", StringComparison.Ordinal) || normalized.Contains("alteration", StringComparison.Ordinal))
                return InfoContextId.SecurityTamperProtection;
            if ((normalized.Contains("protection", StringComparison.Ordinal) && normalized.Contains("temps reel", StringComparison.Ordinal)) ||
                normalized.Contains("real time", StringComparison.Ordinal))
                return InfoContextId.SecurityRealTimeProtection;
            if (normalized.Equals("vbs", StringComparison.Ordinal) ||
                normalized.Contains("virtualization based security", StringComparison.Ordinal))
                return InfoContextId.SecurityVbs;
            if (normalized.Contains("credential guard", StringComparison.Ordinal))
                return InfoContextId.SecurityCredentialGuard;
            if (normalized.Contains("integrite memoire", StringComparison.Ordinal) ||
                normalized.Contains("memory integrity", StringComparison.Ordinal) ||
                normalized.Contains("core isolation", StringComparison.Ordinal))
                return InfoContextId.SecurityMemoryIntegrity;
            if (normalized.Contains("asr", StringComparison.Ordinal) ||
                normalized.Contains("attack surface reduction", StringComparison.Ordinal))
                return InfoContextId.SecurityAsr;
            if (normalized.Contains("perte", StringComparison.Ordinal) && normalized.Contains("paquets", StringComparison.Ordinal))
                return InfoContextId.NetworkLoss;
            if (normalized.Contains("packet", StringComparison.Ordinal) && normalized.Contains("loss", StringComparison.Ordinal))
                return InfoContextId.NetworkLoss;

            return InfoContextId.Unknown;
        }

        private static string SelectSectionValueForContext(HealthSection section, InfoContextId contextId)
        {
            return contextId switch
            {
                InfoContextId.DiskTemp => SelectDiskTemperature(section),
                InfoContextId.CPUTemperature => GetFirstSectionValue(section, "Température CPU", "Temperature CPU"),
                InfoContextId.KernelPower => GetFirstSectionValue(section, "Kernel-Power", "Kernel Power"),
                InfoContextId.RestorePoints => GetFirstSectionValue(section, "Points de restauration", "Restore points"),
                InfoContextId.UpdatesPending => GetFirstSectionValue(section, "Updates Windows", "Updates en attente"),
                InfoContextId.SecurityAntivirus => GetFirstSectionValue(section, "Antivirus"),
                InfoContextId.SecurityFirewall => GetFirstSectionValue(section, "Pare-feu", "Pare feu", "Firewall"),
                InfoContextId.SecuritySecureBoot => GetFirstSectionValue(section, "Secure Boot"),
                InfoContextId.SecurityBitLocker => GetFirstSectionValue(section, "BitLocker"),
                InfoContextId.SecurityUac => GetFirstSectionValue(section, "UAC"),
                InfoContextId.SecuritySmbV1 => GetFirstSectionValue(section, "SMBv1"),
                InfoContextId.SecurityTamperProtection => GetFirstSectionValue(section, "Protection contre altération", "Tamper Protection"),
                InfoContextId.SecurityRealTimeProtection => GetFirstSectionValue(section, "Protection en temps réel", "Protection temps reel", "Defender temps reel"),
                InfoContextId.SecurityVbs => GetFirstSectionValue(section, "VBS"),
                InfoContextId.SecurityCredentialGuard => GetFirstSectionValue(section, "Credential Guard"),
                InfoContextId.SecurityMemoryIntegrity => GetFirstSectionValue(section, "Intégrité mémoire", "Integrite memoire", "Memory Integrity"),
                InfoContextId.SecurityAsr => GetFirstSectionValue(section, "Règles ASR", "Regles ASR", "ASR"),
                InfoContextId.RebootRequired => GetFirstSectionValue(section, "Redémarrage requis", "Redemarrage requis"),
                _ => string.Empty
            };
        }

        private static string SelectDiskTemperature(HealthSection section)
        {
            var direct = GetFirstSectionValue(section, "TempMax Disques", "Températures disques", "Temperature disques");
            if (!string.IsNullOrWhiteSpace(direct))
                return direct;

            double? maxFromRows = null;
            foreach (var kvp in section.EvidenceData)
            {
                if (!kvp.Key.StartsWith("Disque ", StringComparison.OrdinalIgnoreCase))
                    continue;

                var temp = ExtractTemperature(kvp.Value);
                if (!temp.HasValue)
                    continue;

                maxFromRows = !maxFromRows.HasValue ? temp.Value : Math.Max(maxFromRows.Value, temp.Value);
            }

            if (maxFromRows.HasValue)
                return $"{maxFromRows.Value:F0}°C";

            return string.Empty;
        }

        private static string GetFirstSectionValue(HealthSection section, params string[] keys)
        {
            foreach (var expectedKey in keys)
            {
                var match = section.EvidenceData
                    .FirstOrDefault(kvp => string.Equals(TextEncodingNormalizer.Normalize(kvp.Key), expectedKey, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match.Value))
                    return TextEncodingNormalizer.Normalize(match.Value);
            }

            return string.Empty;
        }

        private static void PopulateValueAndSeverity(InfoContext context, string rawValue, HealthSection section)
        {
            var normalizedRaw = TextEncodingNormalizer.Normalize(rawValue);
            context.Value = normalizedRaw;

            switch (context.ContextId)
            {
                case InfoContextId.DiskTemp:
                {
                    var temp = ExtractTemperature(normalizedRaw) ?? ExtractTemperature(SelectDiskTemperature(section));
                    if (temp.HasValue)
                    {
                        context.Value = temp.Value;
                        context.Unit = "°C";
                        context.Confidence = InfoConfidence.High;
                        context.Evidence.Threshold = temp.Value > 60 ? 60 : 50;
                        context.Severity = temp.Value > 60 ? InfoSeverity.Danger :
                                           temp.Value >= 50 ? InfoSeverity.Warning :
                                           InfoSeverity.Info;
                    }
                    else
                    {
                        context.Confidence = InfoConfidence.Low;
                        context.Severity = InfoSeverity.Warning;
                    }

                    return;
                }

                case InfoContextId.CPUTemperature:
                {
                    var temp = ExtractTemperature(normalizedRaw);
                    context.Unit = "°C";
                    if (temp.HasValue)
                    {
                        context.Value = temp.Value;
                        context.Confidence = InfoConfidence.High;
                        context.Severity = temp.Value >= 90 ? InfoSeverity.Danger :
                                           temp.Value >= 80 ? InfoSeverity.Warning :
                                           InfoSeverity.Info;
                    }
                    else
                    {
                        context.Confidence = InfoConfidence.Low;
                        context.Severity = normalizedRaw.Contains("BlockedBySecurity", StringComparison.OrdinalIgnoreCase) ||
                                           normalizedRaw.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                                           normalizedRaw.Contains("bloque par la securite", StringComparison.OrdinalIgnoreCase) ||
                                           normalizedRaw.Contains("acces refuse", StringComparison.OrdinalIgnoreCase) ||
                                           normalizedRaw.Contains("erreur de lecture", StringComparison.OrdinalIgnoreCase)
                            ? InfoSeverity.Warning
                            : InfoSeverity.Info;
                    }

                    return;
                }

                case InfoContextId.TDR:
                {
                    var count = ExtractInteger(normalizedRaw);
                    context.Evidence.EventCount = count;
                    context.Unit = "événement(s)";
                    context.Confidence = count.HasValue ? InfoConfidence.High : InfoConfidence.Medium;
                    context.Severity = !count.HasValue || count.Value == 0 ? InfoSeverity.Info :
                                       count.Value <= 2 ? InfoSeverity.Warning :
                                       InfoSeverity.Danger;
                    if (count.HasValue)
                        context.Value = count.Value;
                    return;
                }

                case InfoContextId.VRAM:
                {
                    var pct = ExtractPercent(normalizedRaw) ?? InferPercentFromFraction(normalizedRaw);
                    context.Unit = "%";
                    context.Confidence = pct.HasValue ? InfoConfidence.High : InfoConfidence.Medium;
                    if (pct.HasValue)
                    {
                        context.Value = pct.Value;
                        context.Severity = pct.Value > 90 ? InfoSeverity.Danger :
                                           pct.Value >= 70 ? InfoSeverity.Warning :
                                           InfoSeverity.Info;
                        context.Evidence.Threshold = pct.Value > 90 ? 90 : 70;
                    }
                    else
                    {
                        context.Severity = InfoSeverity.Warning;
                    }

                    return;
                }

                case InfoContextId.GPULoad:
                {
                    var pct = ExtractPercent(normalizedRaw);
                    context.Unit = "%";
                    context.Confidence = pct.HasValue ? InfoConfidence.High : InfoConfidence.Medium;
                    if (pct.HasValue)
                    {
                        context.Value = pct.Value;
                        context.Severity = pct.Value >= 95 ? InfoSeverity.Danger :
                                           pct.Value >= 80 ? InfoSeverity.Warning :
                                           InfoSeverity.Info;
                    }
                    else
                    {
                        context.Severity = InfoSeverity.Info;
                    }

                    return;
                }

                case InfoContextId.CPUThrottle:
                {
                    var detected = ExtractBoolean(normalizedRaw);
                    context.Confidence = detected.HasValue ? InfoConfidence.High : InfoConfidence.Medium;
                    context.Value = detected.HasValue ? (detected.Value ? "Oui" : "Non") : normalizedRaw;
                    context.Severity = detected == true
                        ? (section.Severity is HealthSeverity.Critical or HealthSeverity.Degraded
                            ? InfoSeverity.Danger
                            : InfoSeverity.Warning)
                        : InfoSeverity.Info;
                    return;
                }

                case InfoContextId.SMARTHealth:
                {
                    var s = SimplifyKey(normalizedRaw);
                    if (IsUnavailableText(s))
                    {
                        context.Confidence = InfoConfidence.None;
                        context.Severity = InfoSeverity.Warning;
                        return;
                    }

                    context.Confidence = InfoConfidence.High;
                    context.Severity = (s.Contains("defaillance", StringComparison.Ordinal) ||
                                        s.Contains("failure", StringComparison.Ordinal) ||
                                        s.Contains("critical", StringComparison.Ordinal) ||
                                        s.Contains("critique", StringComparison.Ordinal) ||
                                        s.Contains("danger", StringComparison.Ordinal))
                        ? InfoSeverity.Danger
                        : (s.Contains("warning", StringComparison.Ordinal) ||
                           s.Contains("avertissement", StringComparison.Ordinal) ||
                           s.Contains("attention", StringComparison.Ordinal))
                            ? InfoSeverity.Warning
                            : InfoSeverity.Info;
                    return;
                }

                case InfoContextId.RestorePoints:
                {
                    var count = ExtractInteger(normalizedRaw);
                    if (count.HasValue && count.Value >= 0)
                    {
                        context.Value = count.Value;
                        context.Evidence.EventCount = count.Value;
                        context.Unit = "point(s)";
                        context.Confidence = InfoConfidence.High;
                        context.Severity = count.Value == 0 ? InfoSeverity.Warning : InfoSeverity.Info;
                    }
                    else
                    {
                        context.Confidence = InfoConfidence.None;
                        context.Severity = InfoSeverity.Warning;
                    }
                    return;
                }

                case InfoContextId.WHEA:
                case InfoContextId.BSOD:
                case InfoContextId.KernelPower:
                {
                    var count = ExtractInteger(normalizedRaw);
                    context.Evidence.EventCount = count;
                    context.Unit = "événement(s)";
                    context.Confidence = count.HasValue ? InfoConfidence.High : InfoConfidence.Medium;
                    context.Severity = !count.HasValue || count.Value == 0 ? InfoSeverity.Info :
                                       count.Value <= 2 ? InfoSeverity.Warning :
                                       InfoSeverity.Danger;
                    if (count.HasValue)
                        context.Value = count.Value;
                    return;
                }

                case InfoContextId.RebootRequired:
                {
                    var reboot = ExtractBoolean(normalizedRaw);
                    context.Confidence = reboot.HasValue ? InfoConfidence.High : InfoConfidence.Medium;
                    context.Value = reboot.HasValue ? (reboot.Value ? "Oui" : "Non") : normalizedRaw;
                    context.Severity = reboot == true ? InfoSeverity.Warning : InfoSeverity.Info;
                    return;
                }

                case InfoContextId.UpdatesPending:
                {
                    var count = ExtractInteger(normalizedRaw);
                    context.Evidence.EventCount = count;
                    context.Unit = "mise(s) à jour";
                    context.Confidence = count.HasValue ? InfoConfidence.High : InfoConfidence.Medium;
                    context.Severity = !count.HasValue || count.Value == 0 ? InfoSeverity.Info :
                                       count.Value >= 10 ? InfoSeverity.Danger :
                                       InfoSeverity.Warning;
                    if (count.HasValue)
                        context.Value = count.Value;
                    return;
                }

                case InfoContextId.NetworkLoss:
                {
                    var loss = ExtractPercent(normalizedRaw);
                    context.Unit = "%";
                    context.Confidence = loss.HasValue ? InfoConfidence.High : InfoConfidence.Medium;
                    if (loss.HasValue)
                    {
                        context.Value = loss.Value;
                        context.Severity = loss.Value > 5 ? InfoSeverity.Danger :
                                           loss.Value > 1 ? InfoSeverity.Warning :
                                           InfoSeverity.Info;
                    }
                    else
                    {
                        context.Severity = InfoSeverity.Warning;
                    }

                    return;
                }

                case InfoContextId.SecurityAntivirus:
                case InfoContextId.SecurityFirewall:
                case InfoContextId.SecuritySecureBoot:
                case InfoContextId.SecurityBitLocker:
                case InfoContextId.SecurityUac:
                case InfoContextId.SecuritySmbV1:
                case InfoContextId.SecurityTamperProtection:
                case InfoContextId.SecurityRealTimeProtection:
                case InfoContextId.SecurityVbs:
                case InfoContextId.SecurityCredentialGuard:
                case InfoContextId.SecurityMemoryIntegrity:
                case InfoContextId.SecurityAsr:
                {
                    PopulateSecurityContext(context, normalizedRaw);
                    return;
                }

                default:
                    context.Confidence = string.IsNullOrWhiteSpace(normalizedRaw) ? InfoConfidence.None : InfoConfidence.Medium;
                    context.Severity = context.Confidence == InfoConfidence.None ? InfoSeverity.Warning : InfoSeverity.Info;
                    return;
            }
        }

        private static void PopulateSecurityContext(InfoContext context, string rawValue)
        {
            var normalized = SimplifyKey(rawValue);
            context.Value = rawValue;

            if (IsUnavailableText(normalized))
            {
                context.Confidence = InfoConfidence.None;
                context.Severity = InfoSeverity.Warning;
                return;
            }

            var boolValue = ExtractBoolean(rawValue);
            if (!boolValue.HasValue)
            {
                if (normalized.Contains("active", StringComparison.Ordinal) || normalized.Contains("enabled", StringComparison.Ordinal))
                    boolValue = true;
                else if (normalized.Contains("desactive", StringComparison.Ordinal) ||
                         normalized.Contains("disabled", StringComparison.Ordinal) ||
                         normalized.Contains("off", StringComparison.Ordinal))
                    boolValue = false;
            }

            context.Confidence = boolValue.HasValue ? InfoConfidence.High : InfoConfidence.Medium;

            switch (context.ContextId)
            {
                case InfoContextId.SecuritySmbV1:
                    context.Severity = boolValue == true ? InfoSeverity.Danger : InfoSeverity.Info;
                    break;

                case InfoContextId.SecurityFirewall:
                case InfoContextId.SecurityRealTimeProtection:
                    context.Severity = boolValue == false ? InfoSeverity.Danger : InfoSeverity.Info;
                    break;

                case InfoContextId.SecuritySecureBoot:
                case InfoContextId.SecurityBitLocker:
                case InfoContextId.SecurityUac:
                case InfoContextId.SecurityTamperProtection:
                case InfoContextId.SecurityVbs:
                case InfoContextId.SecurityCredentialGuard:
                case InfoContextId.SecurityMemoryIntegrity:
                    context.Severity = boolValue == false ? InfoSeverity.Warning : InfoSeverity.Info;
                    break;

                case InfoContextId.SecurityAsr:
                {
                    var count = ExtractInteger(rawValue);
                    if (count.HasValue)
                    {
                        context.Evidence.EventCount = count.Value;
                        context.Unit = "règle(s)";
                        context.Severity = count.Value == 0 ? InfoSeverity.Warning : InfoSeverity.Info;
                    }
                    else
                    {
                        context.Severity = normalized.Contains("absent", StringComparison.Ordinal) ||
                                           normalized.Contains("desactive", StringComparison.Ordinal)
                            ? InfoSeverity.Warning
                            : InfoSeverity.Info;
                    }
                    break;
                }

                default:
                    context.Severity = boolValue == false ? InfoSeverity.Warning : InfoSeverity.Info;
                    break;
            }
        }

        private static InfoSectionId MapSectionId(HealthDomain domain)
        {
            return domain switch
            {
                HealthDomain.OS => InfoSectionId.OS,
                HealthDomain.CPU => InfoSectionId.CPU,
                HealthDomain.GPU => InfoSectionId.GPU,
                HealthDomain.RAM => InfoSectionId.RAM,
                HealthDomain.Storage => InfoSectionId.Storage,
                HealthDomain.Network => InfoSectionId.Network,
                HealthDomain.SystemStability => InfoSectionId.SystemStability,
                HealthDomain.Drivers => InfoSectionId.Drivers,
                HealthDomain.Applications => InfoSectionId.Applications,
                HealthDomain.Performance => InfoSectionId.Performance,
                HealthDomain.Security => InfoSectionId.Security,
                HealthDomain.Power => InfoSectionId.Power,
                _ => InfoSectionId.Unknown
            };
        }

        private static double? ExtractTemperature(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var match = CelsiusRegex.Match(value);
            if (!match.Success)
                return null;

            return TryParseDouble(match.Groups["value"].Value);
        }

        private static double? ExtractPercent(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var match = PercentRegex.Match(value);
            if (!match.Success)
                return null;

            return TryParseDouble(match.Groups["value"].Value);
        }

        private static double? InferPercentFromFraction(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var matches = NumberRegex.Matches(value);
            if (matches.Count < 2)
                return null;

            var used = TryParseDouble(matches[0].Value);
            var total = TryParseDouble(matches[1].Value);
            if (!used.HasValue || !total.HasValue || total.Value <= 0)
                return null;

            return Math.Round((used.Value / total.Value) * 100.0, 1);
        }

        private static int? ExtractInteger(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var match = NumberRegex.Match(value);
            if (!match.Success)
                return null;

            var parsed = TryParseDouble(match.Value);
            return parsed.HasValue ? (int)Math.Round(parsed.Value) : null;
        }

        private static bool? ExtractBoolean(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var normalized = SimplifyKey(value);
            if (normalized.Contains("oui", StringComparison.Ordinal) ||
                normalized.Contains("detecte", StringComparison.Ordinal) ||
                normalized.Contains("active", StringComparison.Ordinal) ||
                normalized.Contains("true", StringComparison.Ordinal) ||
                normalized.Contains("yes", StringComparison.Ordinal))
            {
                return true;
            }

            if (normalized.Contains("non", StringComparison.Ordinal) ||
                normalized.Contains("desactive", StringComparison.Ordinal) ||
                normalized.Contains("not detected", StringComparison.Ordinal) ||
                normalized.Contains("false", StringComparison.Ordinal) ||
                normalized.Contains("no", StringComparison.Ordinal))
            {
                return false;
            }

            return null;
        }

        private static double? TryParseDouble(string value)
        {
            var normalized = value.Replace(',', '.');
            if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
                return result;

            return null;
        }

        private static string SimplifyKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return string.Empty;

            var normalized = TextEncodingNormalizer.Normalize(key).Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);

            foreach (var ch in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (category == UnicodeCategory.NonSpacingMark)
                    continue;

                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    continue;
                }

                if (char.IsWhiteSpace(ch) || ch == '/' || ch == '-')
                    sb.Append(' ');
            }

            return Regex.Replace(sb.ToString(), @"\s{2,}", " ").Trim();
        }

        private static bool IsUnavailableText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            return value.Contains("indisponible", StringComparison.Ordinal) ||
                   value.Contains("inconnu", StringComparison.Ordinal) ||
                   value.Contains("unknown", StringComparison.Ordinal) ||
                   value.Contains("non detect", StringComparison.Ordinal) ||
                   value.Contains("n a", StringComparison.Ordinal);
        }
    }
}
