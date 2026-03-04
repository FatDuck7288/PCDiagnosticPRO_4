using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PCDiagnosticPro.AI
{
    /// <summary>
    /// Writes AutoFix pipeline artifacts and structured stage logs under:
    /// %TEMP%\PCXray\autofix\{TraceId}
    /// </summary>
    public sealed class AutoFixTraceWriter
    {
        private const int DefaultMaxArtifactBytes = 100 * 1024;
        private static readonly UTF8Encoding Utf8NoBom = new(false);

        public AutoFixTraceWriter(string traceId, string runId)
        {
            TraceId = string.IsNullOrWhiteSpace(traceId)
                ? Guid.NewGuid().ToString("N")[..8]
                : traceId.Trim();
            RunId = runId?.Trim() ?? string.Empty;

            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "PCXray",
                "autofix",
                TraceId);

            Directory.CreateDirectory(DirectoryPath);
        }

        public string TraceId { get; }
        public string RunId { get; }
        public string DirectoryPath { get; }
        public string PipelineStagesPath => Path.Combine(DirectoryPath, "pipeline_stages.jsonl");

        public string ResolvePath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("Artifact file name cannot be empty.", nameof(fileName));
            }

            return Path.Combine(DirectoryPath, fileName);
        }

        public void WriteArtifact(string fileName, string? content, int maxBytes = DefaultMaxArtifactBytes)
        {
            try
            {
                var text = content ?? string.Empty;
                var bytes = Utf8NoBom.GetBytes(text);
                if (bytes.Length > maxBytes)
                {
                    text = Utf8NoBom.GetString(bytes, 0, maxBytes)
                        + Environment.NewLine
                        + "...[TRUNCATED]";
                }

                File.WriteAllText(ResolvePath(fileName), text, Utf8NoBom);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[AI][TraceWriter] WriteArtifact failed ({fileName}): {ex.Message}");
            }
        }

        public void WriteStageSnapshot(string stage, string? content)
        {
            var text = content ?? string.Empty;
            WriteStage(stage, new
            {
                chars = text.Length,
                head200 = Head(text, 200),
                tail200 = Tail(text, 200)
            });
        }

        public void WriteStageChars(string stage, int chars, string? note = null)
        {
            WriteStage(stage, new
            {
                chars,
                note = note ?? string.Empty
            });
        }

        public void WriteStage(string stage, object payload)
        {
            try
            {
                var entry = new
                {
                    tsUtc = DateTime.UtcNow.ToString("O"),
                    traceId = TraceId,
                    runId = RunId,
                    stage,
                    payload
                };

                var json = JsonSerializer.Serialize(entry);
                File.AppendAllText(PipelineStagesPath, json + Environment.NewLine, Utf8NoBom);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[AI][TraceWriter] WriteStage failed ({stage}): {ex.Message}");
            }
        }

        private static string Head(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Length <= maxChars ? text : text[..maxChars];
        }

        private static string Tail(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Length <= maxChars ? text : text[^maxChars..];
        }
    }
}
