using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    public class PowerShellJsonMapper
    {
        /// <summary>
        /// Null means "data not collected". UI layer decides how to render this.
        /// We never store the French sentinel in data fields anymore.
        /// </summary>
        private const string? FallbackValue = null;

        /// <summary>
        /// PF-1: Set to true only from a developer/debug menu to enable diagnostic I/O to %TEMP%.
        /// Off by default to avoid privacy leaks and unnecessary disk writes in production.
        /// </summary>
        public static bool DiagnosticLoggingEnabled { get; set; } = false;

        /// <summary>
        /// UI-2: Protected acronyms that must stay uppercase regardless of context.
        /// </summary>
        private static readonly Dictionary<string, string> _acronyms = new(StringComparer.OrdinalIgnoreCase)
        {
            { "cpu", "CPU" }, { "gpu", "GPU" }, { "ram", "RAM" }, { "bios", "BIOS" },
            { "tpm", "TPM" }, { "smart", "SMART" }, { "vram", "VRAM" }, { "os", "OS" },
            { "ip", "IP" }, { "dns", "DNS" }, { "mac", "MAC" }, { "nvme", "NVMe" },
            { "pcie", "PCIe" }, { "ddr", "DDR" }, { "gb", "GB" }, { "mb", "MB" },
            { "mhz", "MHz" }, { "ghz", "GHz" }
        };

        public ScanResult Parse(string jsonContent, string reportPath, TimeSpan duration)
        {
            var result = new ScanResult
            {
                IsValid = true,
                RawReport = TextEncodingNormalizer.Normalize(jsonContent),
                ReportFilePath = reportPath,
                Summary = new ScanSummary
                {
                    ScanDate = DateTime.Now,
                    ScanDuration = duration
                }
            };

            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            result.Sections = BuildSections(root);
            PopulateSummary(root, result);
            if (DiagnosticLoggingEnabled)
                LogSectionsToTemp(root);

            return result;
        }

        private static List<ResultSection> BuildSections(JsonElement root)
        {
            var sections = new List<ResultSection>();

            if (root.ValueKind != JsonValueKind.Object)
            {
                var fallback = new ResultSection { Title = "Résultats" };
                fallback.Fields.Add(new ResultField { Key = "Valeur", Value = ToDisplayValue(root) });
                sections.Add(fallback);
                return sections;
            }

            foreach (var property in root.EnumerateObject())
            {
                var section = new ResultSection { Title = FormatTitle(property.Name) };
                ParseElement(property.Value, string.Empty, section);

                if (section.Fields.Count == 0 && section.Tables.Count == 0)
                {
                    section.Fields.Add(new ResultField { Key = "Valeur", Value = FallbackValue });
                }

                sections.Add(section);
            }

            return sections;
        }

        private static void ParseElement(JsonElement element, string prefix, ResultSection section)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in element.EnumerateObject())
                    {
                        var nextPrefix = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                        ParseElement(prop.Value, nextPrefix, section);
                    }
                    break;
                case JsonValueKind.Array:
                    AddArrayElement(element, prefix, section);
                    break;
                default:
                    var key = string.IsNullOrEmpty(prefix) ? "Valeur" : FormatKey(prefix);
                    section.Fields.Add(new ResultField
                    {
                        Key = key,
                        Value = ToDisplayValue(element, prefix)
                    });
                    break;
            }
        }

        private static void AddArrayElement(JsonElement element, string prefix, ResultSection section)
        {
            var items = element.EnumerateArray().ToList();
            var title = string.IsNullOrEmpty(prefix) ? "Liste" : FormatKey(prefix);

            if (items.Count == 0)
            {
                section.Fields.Add(new ResultField { Key = title, Value = FallbackValue });
                return;
            }

            var containsObjects = items.Any(i => i.ValueKind == JsonValueKind.Object);

            if (!containsObjects)
            {
                var table = new ResultTable { Title = title };
                table.Table.Columns.Add("Valeur");
                foreach (var item in items)
                {
                    table.Table.Rows.Add((object?)ToDisplayValue(item) ?? DBNull.Value);
                }
                section.Tables.Add(table);
                return;
            }

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var prop in item.EnumerateObject())
                {
                    columns.Add(prop.Name);
                }
            }

            var tableResult = new ResultTable { Title = title };
            foreach (var column in columns)
            {
                tableResult.Table.Columns.Add(FormatKey(column));
            }

            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    var row = tableResult.Table.NewRow();
                    if (tableResult.Table.Columns.Count == 0)
                    {
                        tableResult.Table.Columns.Add("Valeur");
                    }
                    row[0] = (object?)ToDisplayValue(item) ?? DBNull.Value;
                    tableResult.Table.Rows.Add(row);
                    continue;
                }

                var rowItem = tableResult.Table.NewRow();
                foreach (var column in columns)
                {
                    if (item.TryGetProperty(column, out var cell))
                    {
                        rowItem[FormatKey(column)] = (object?)ToDisplayValue(cell) ?? DBNull.Value;
                    }
                    else
                    {
                        // Missing column in this row → null (typed missing, not sentinel)
                        rowItem[FormatKey(column)] = DBNull.Value;
                    }
                }
                tableResult.Table.Rows.Add(rowItem);
            }

            section.Tables.Add(tableResult);
        }

        private static void PopulateSummary(JsonElement root, ScanResult result)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("summary", out var summaryEl))
            {
                if (summaryEl.ValueKind == JsonValueKind.Object)
                {
                    result.Summary.Score = summaryEl.TryGetProperty("score", out var scoreEl) ? scoreEl.GetInt32() : 0;
                    result.Summary.Grade = summaryEl.TryGetProperty("grade", out var gradeEl) ? gradeEl.GetString() ?? "N/A" : "N/A";
                    result.Summary.CriticalCount = summaryEl.TryGetProperty("criticalCount", out var critEl) ? critEl.GetInt32() : 0;
                    result.Summary.ErrorCount = summaryEl.TryGetProperty("errorCount", out var errEl) ? errEl.GetInt32() : 0;
                    result.Summary.WarningCount = summaryEl.TryGetProperty("warningCount", out var warnEl) ? warnEl.GetInt32() : 0;

                    if (summaryEl.TryGetProperty("scanDate", out var dateEl))
                    {
                        if (DateTimeOffset.TryParse(dateEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                        {
                            result.Summary.ScanDate = parsedDate.LocalDateTime;
                        }
                    }

                    return;
                }
            }

            var counts = FindCountValues(result.Sections);
            result.Summary.CriticalCount = counts.Critical;
            result.Summary.ErrorCount = counts.Error;
            result.Summary.WarningCount = counts.Warning;

            var score = 100 - (counts.Critical * 25) - (counts.Error * 10) - (counts.Warning * 5);
            score = Math.Max(0, Math.Min(100, score));
            result.Summary.Score = score;
            result.Summary.Grade = CalculateGrade(score);
        }

        private static (int Critical, int Error, int Warning) FindCountValues(IEnumerable<ResultSection> sections)
        {
            var critical = ExtractCount(sections, "criticalcount", "critical");
            var error = ExtractCount(sections, "errorcount", "error");
            var warning = ExtractCount(sections, "warningcount", "warning");
            return (critical, error, warning);
        }

        private static int ExtractCount(IEnumerable<ResultSection> sections, params string[] keys)
        {
            foreach (var section in sections)
            {
                foreach (var field in section.Fields)
                {
                    if (keys.Any(k => field.Key.Replace(" ", string.Empty).Equals(k, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (int.TryParse(field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        {
                            return parsed;
                        }
                    }
                }
            }

            return 0;
        }

        private static string CalculateGrade(int score) => Models.SchemaRegistry.ScoreToGrade(score);

        /// <summary>
        /// Converts a JSON element to a display string.
        /// Returns null for missing/null/undefined values — consumers must handle null and show "Indisponible".
        /// UI-1: When a raw JSON key is provided, appends appropriate unit suffixes (°C, %, GB, MB).
        /// </summary>
        private static string? ToDisplayValue(JsonElement element, string? rawKey = null)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    var str = element.GetString();
                    if (string.IsNullOrWhiteSpace(str)) return FallbackValue;
                    // Sanitize internal technical notes that should not be shown to end users
                    if (IsInternalTechnicalNote(str)) return FallbackValue;
                    return TextEncodingNormalizer.Normalize(str);
                case JsonValueKind.Number:
                    var numText = element.GetRawText();
                    if (!string.IsNullOrEmpty(rawKey))
                    {
                        // Temperature fields
                        if (rawKey.EndsWith("TempC", StringComparison.OrdinalIgnoreCase)
                            || (rawKey.Contains("Temp", StringComparison.OrdinalIgnoreCase)
                                && rawKey.EndsWith("C", StringComparison.OrdinalIgnoreCase)))
                            return numText + " °C";
                        // Percent/load fields
                        if (rawKey.EndsWith("Percent", StringComparison.OrdinalIgnoreCase)
                            || rawKey.EndsWith("UsedPercent", StringComparison.OrdinalIgnoreCase)
                            || rawKey.EndsWith("Load", StringComparison.OrdinalIgnoreCase))
                            return numText + " %";
                        // Storage size fields
                        if (rawKey.EndsWith("GB", StringComparison.OrdinalIgnoreCase)
                            || rawKey.EndsWith("SizeGB", StringComparison.OrdinalIgnoreCase)
                            || rawKey.EndsWith("FreeGB", StringComparison.OrdinalIgnoreCase)
                            || rawKey.EndsWith("TotalGB", StringComparison.OrdinalIgnoreCase))
                            return numText + " GB";
                        if (rawKey.EndsWith("MB", StringComparison.OrdinalIgnoreCase)
                            || rawKey.EndsWith("SizeMB", StringComparison.OrdinalIgnoreCase)
                            || rawKey.EndsWith("VramMB", StringComparison.OrdinalIgnoreCase))
                            return numText + " MB";
                    }
                    return numText;
                case JsonValueKind.True:
                    return App.CurrentLanguage == "en" ? "Yes" : "Oui";
                case JsonValueKind.False:
                    return App.CurrentLanguage == "en" ? "No" : "Non";
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return FallbackValue;
                default:
                    return TextEncodingNormalizer.Normalize(element.GetRawText());
            }
        }

        private static string FormatTitle(string raw)
        {
            return TextEncodingNormalizer.Normalize(FormatKey(raw));
        }

        private static string FormatKey(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return raw;

            // Step 1: split on _ and -
            var cleaned = raw.Replace("_", " ").Replace("-", " ");

            // Step 2: camelCase split — insert space before uppercase letters
            // that follow a lowercase letter or digit (not a space).
            var chars = cleaned.ToCharArray();
            var expanded = new List<char> { chars[0] };
            for (var i = 1; i < chars.Length; i++)
            {
                var cur = chars[i];
                var prev = chars[i - 1];
                if (char.IsUpper(cur) && char.IsLetterOrDigit(prev) && prev != ' ')
                    expanded.Add(' ');
                expanded.Add(cur);
            }

            // Step 3: capitalize first letter of each word, replace known acronyms.
            var words = new string(expanded.ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < words.Length; i++)
            {
                if (_acronyms.TryGetValue(words[i], out var acronym))
                    words[i] = acronym;
                else if (words[i].Length > 0)
                    words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
            }

            return string.Join(" ", words);
        }

        /// <summary>
        /// Detects internal technical notes that should not be displayed to end users.
        /// These are replaced with null (shown as "Indisponible" in UI).
        /// </summary>
        private static bool IsInternalTechnicalNote(string value)
        {
            // Patterns that indicate internal/technical text not meant for users
            if (value.Contains("limitation WMI", StringComparison.OrdinalIgnoreCase)) return true;
            if (value.Contains("collecte externalisee", StringComparison.OrdinalIgnoreCase)) return true;
            if (value.Contains("Neutralise_v7", StringComparison.OrdinalIgnoreCase)) return true;
            if (value.Contains("unavailable_os_version", StringComparison.OrdinalIgnoreCase)) return true;
            if (value.Contains("Keys checked in other collectors", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Migration adapter: treats legacy "Non disponible" sentinel as null.
        /// Call this when reading data from old scan JSON files (pre-2.3.0).
        /// Returns null when value is the legacy sentinel, otherwise returns the original string.
        /// </summary>
        public static string? MigrateLegacySentinel(string? value)
        {
            if (value == null) return null;
            return value.StartsWith("Non disponible", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("Indisponible", StringComparison.OrdinalIgnoreCase)
                ? null
                : value;
        }

        private static void LogSectionsToTemp(JsonElement root)
        {
            try
            {
                var logPath = Path.Combine(Path.GetTempPath(), "PCDiagnosticPro_PowerShellJsonMapper.log");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"=== PowerShellJsonMapper Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("sections", out var sections))
                {
                    sb.AppendLine("No sections found in PS JSON.");
                    File.AppendAllText(logPath, sb.ToString() + Environment.NewLine, System.Text.Encoding.UTF8);
                    return;
                }

                if (sections.ValueKind != JsonValueKind.Object)
                {
                    sb.AppendLine($"Sections present but unexpected type: {sections.ValueKind}");
                    File.AppendAllText(logPath, sb.ToString() + Environment.NewLine, System.Text.Encoding.UTF8);
                    return;
                }

                foreach (var section in sections.EnumerateObject())
                {
                    var name = section.Name;
                    var valueKind = section.Value.ValueKind.ToString();
                    var info = valueKind;
                    
                    if (section.Value.ValueKind == JsonValueKind.Object && section.Value.TryGetProperty("data", out var data))
                    {
                        if (data.ValueKind == JsonValueKind.Array)
                            info = $"data=array[{data.GetArrayLength()}]";
                        else if (data.ValueKind == JsonValueKind.Object)
                            info = $"data=object[{data.EnumerateObject().Count()}]";
                        else
                            info = $"data={data.ValueKind}";
                    }
                    else if (section.Value.ValueKind == JsonValueKind.Array)
                    {
                        info = $"array[{section.Value.GetArrayLength()}]";
                    }

                    sb.AppendLine($"- {name}: {info}");
                }

                File.AppendAllText(logPath, sb.ToString() + Environment.NewLine, System.Text.Encoding.UTF8);
            }
            catch
            {
                // ignore logging errors
            }
        }
    }
}

