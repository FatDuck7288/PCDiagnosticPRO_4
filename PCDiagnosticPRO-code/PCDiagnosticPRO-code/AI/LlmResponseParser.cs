using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCDiagnosticPro.AI.Models;

namespace PCDiagnosticPro.AI
{
    /// <summary>
    /// Parses LLM chat output into <see cref="LlmStructuredResponse"/>.
    /// Three-attempt strategy: direct → fence-extracted → bare JSON.
    /// Falls back to raw text as <see cref="LlmStructuredResponse.UserResponse"/> on failure.
    /// </summary>
    public static class LlmResponseParser
    {
        private static readonly Regex ThinkBlockRegex = new(
            @"<think>[\s\S]*?</think>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex UnclosedThinkRegex = new(
            @"<think>[\s\S]*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex JsonFenceRegex = new(
            @"```(?:json)?\s*(\{[\s\S]*?\})\s*```",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex BareJsonRegex = new(
            @"\{[\s\S]*\}",
            RegexOptions.Compiled);

        private static readonly JsonSerializerOptions ParseOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        /// <summary>
        /// Parse raw LLM output into a structured response envelope.
        /// Always returns a non-null result. On failure, <see cref="LlmStructuredResponse.UserResponse"/>
        /// contains the raw text and <see cref="LlmStructuredResponse.ParseSuccess"/> is false.
        /// </summary>
        public static LlmStructuredResponse Parse(string? raw, string language = "fr")
        {
            var result = new LlmStructuredResponse { RawInput = raw ?? string.Empty };

            if (string.IsNullOrWhiteSpace(raw))
            {
                result.ParseError = "Empty raw response";
                App.LogMessage($"[StructuredParser] FAIL: empty response");
                return ApplyFallback(result, language);
            }

            App.LogMessage($"[StructuredParser] raw_len={raw.Length}");

            // Strip <think>...</think> reasoning blocks emitted by Qwen3/DeepSeek models
            var stripped = ThinkBlockRegex.Replace(raw, string.Empty);
            stripped = UnclosedThinkRegex.Replace(stripped, string.Empty);
            stripped = stripped.Trim();

            if (stripped.Length != raw.Length)
            {
                App.LogMessage($"[StructuredParser] stripped <think> blocks: {raw.Length} -> {stripped.Length} chars");
            }

            if (string.IsNullOrWhiteSpace(stripped))
            {
                result.ParseError = "Response was only <think> content";
                App.LogMessage("[StructuredParser] FAIL: only think-block content");
                return ApplyFallback(result, language);
            }

            raw = stripped;

            // Attempt 1: direct parse
            if (TryDeserialize(raw, out var parsed, out var err))
            {
                if (HasUserResponse(parsed))
                {
                    App.LogMessage("[StructuredParser] OK: direct parse");
                    return Finalize(result, parsed!);
                }

                err = "Parsed but user_response missing or empty";
            }

            App.LogMessage($"[StructuredParser] direct failed: {err}");

            // Attempt 2: extract from ```json ... ``` fence
            var fenceMatch = JsonFenceRegex.Match(raw);
            if (fenceMatch.Success)
            {
                var fenced = fenceMatch.Groups[1].Value;
                if (TryDeserialize(fenced, out parsed, out err) && HasUserResponse(parsed))
                {
                    App.LogMessage("[StructuredParser] OK: fence-extracted");
                    return Finalize(result, parsed!);
                }

                App.LogMessage($"[StructuredParser] fence failed: {err}");
            }

            // Attempt 3: extract bare JSON object (first { to last })
            var bareMatch = BareJsonRegex.Match(raw);
            if (bareMatch.Success)
            {
                var bare = bareMatch.Value;
                if (TryDeserialize(bare, out parsed, out err) && HasUserResponse(parsed))
                {
                    App.LogMessage("[StructuredParser] OK: bare JSON");
                    return Finalize(result, parsed!);
                }

                App.LogMessage($"[StructuredParser] bare failed: {err}");
            }

            result.ParseError = err ?? "No valid JSON with user_response found";
            App.LogMessage($"[StructuredParser] FALLBACK: {result.ParseError} | preview={SafeTrim(raw, 200)}");
            return ApplyFallback(result, language);
        }

        private static bool TryDeserialize(string text, out LlmStructuredResponse? result, out string? error)
        {
            result = null;
            error = null;
            try
            {
                result = JsonSerializer.Deserialize<LlmStructuredResponse>(text, ParseOptions);
                return result != null;
            }
            catch (JsonException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool HasUserResponse(LlmStructuredResponse? r) =>
            r != null && !string.IsNullOrWhiteSpace(r.UserResponse);

        private static LlmStructuredResponse Finalize(LlmStructuredResponse shell, LlmStructuredResponse parsed)
        {
            shell.UserResponse = parsed.UserResponse;
            shell.AgentPayload = parsed.AgentPayload;
            shell.ParseSuccess = true;

            App.LogMessage(
                $"[StructuredParser] user_response_len={parsed.UserResponse?.Length ?? 0} " +
                $"has_payload={parsed.AgentPayload != null} " +
                $"trigger={parsed.AgentPayload?.TriggerPipeline == true}");

            return shell;
        }

        private static LlmStructuredResponse ApplyFallback(LlmStructuredResponse result, string language)
        {
            // If the raw input looks like plain-text diagnostic content (not JSON), treat it as
            // a successful parse with the raw text as user_response. This avoids the aggressive
            // sanitizer fallback path for valid non-JSON LLM output.
            var raw = result.RawInput?.Trim() ?? string.Empty;
            if (raw.Length > 20 && !raw.StartsWith("{", StringComparison.Ordinal))
            {
                result.UserResponse = raw;
                result.ParseSuccess = true;
                App.LogMessage($"[StructuredParser] PlainText accepted as user_response len={raw.Length} lang={language}");
                return result;
            }

            result.UserResponse = result.RawInput;
            result.ParseSuccess = false;
            App.LogMessage($"[StructuredParser] Fallback: raw→user_response lang={language}");
            return result;
        }

        private static string SafeTrim(string? s, int max) =>
            s == null ? "(null)" : s.Length <= max ? s : s[..max] + "...";
    }
}
