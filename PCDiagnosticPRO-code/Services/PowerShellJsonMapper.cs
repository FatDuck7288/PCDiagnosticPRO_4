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
        private const string FallbackValue = "Non disponible";

        public ScanResult Parse(string jsonContent, string reportPath, TimeSpan duration)
        {
            var result = new ScanResult
            {
                IsValid = true,
                RawReport = jsonContent,
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
                        Value = ToDisplayValue(element)
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
                    table.Table.Rows.Add(ToDisplayValue(item));
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
                    row[0] = ToDisplayValue(item);
                    tableResult.Table.Rows.Add(row);
                    continue;
                }

                var rowItem = tableResult.Table.NewRow();
                foreach (var column in columns)
                {
                    if (item.TryGetProperty(column, out var cell))
                    {
                        rowItem[FormatKey(column)] = ToDisplayValue(cell);
                    }
                    else
                    {
                        rowItem[FormatKey(column)] = FallbackValue;
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

        private static string CalculateGrade(int score)
        {
            if (score >= 90) return "A";
            if (score >= 75) return "B";
            if (score >= 60) return "C";
            if (score >= 40) return "D";
            return "F";
        }

        private static string ToDisplayValue(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    var str = element.GetString();
                    return string.IsNullOrWhiteSpace(str) ? FallbackValue : str;
                case JsonValueKind.Number:
                    return element.GetRawText();
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return element.GetBoolean().ToString();
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return FallbackValue;
                default:
                    return element.GetRawText();
            }
        }

        private static string FormatTitle(string raw)
        {
            return FormatKey(raw);
        }

        private static string FormatKey(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return raw;
            }

            var cleaned = raw.Replace("_", " ").Replace("-", " ");
            var chars = cleaned.ToCharArray();
            var result = new List<char> { chars[0] };

            for (var i = 1; i < chars.Length; i++)
            {
                var current = chars[i];
                var previous = chars[i - 1];
                if (char.IsUpper(current) && char.IsLetterOrDigit(previous) && previous != ' ')
                {
                    result.Add(' ');
                }
                result.Add(current);
            }

            return new string(result.ToArray());
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
                    File.AppendAllText(logPath, sb.ToString() + Environment.NewLine);
                    return;
                }

                if (sections.ValueKind != JsonValueKind.Object)
                {
                    sb.AppendLine($"Sections present but unexpected type: {sections.ValueKind}");
                    File.AppendAllText(logPath, sb.ToString() + Environment.NewLine);
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

                File.AppendAllText(logPath, sb.ToString() + Environment.NewLine);
            }
            catch
            {
                // ignore logging errors
            }
        }
    }
}
