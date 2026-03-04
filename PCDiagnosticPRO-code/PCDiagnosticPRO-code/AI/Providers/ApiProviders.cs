using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PCDiagnosticPro.AI.Models;

namespace PCDiagnosticPro.AI.Providers
{
    public interface IAgentProvider
    {
        string ProviderKey { get; }
        string DisplayName { get; }
        int ResolveModelContextWindow(ApiProviderSettings settings, AiSettings owner);
    }

    public interface IChatProvider : IAgentProvider
    {
        bool RequiresBaseUrl { get; }
        bool SupportsOptionalBaseUrl { get; }
        ModelValidationResult Validate(ApiProviderSettings settings, AiSettings owner);
        HttpRequestMessage BuildRequest(ApiProviderSettings settings, string systemPrompt, string userPrompt, string apiKey, AiSettings owner);
        string ExtractAssistantText(string responseBody);
    }

    public static class ApiProviderCatalog
    {
        public const string OpenAi = "OpenAI";
        public const string Anthropic = "Anthropic";
        public const string Gemini = "Google Gemini";
        public const string Grok = "xAI Grok";
        public const string OpenAiCompatible = "OpenAI-Compatible";
        public const string Custom = "Custom";

        public static readonly string[] SupportedProviders =
        {
            OpenAi,
            Anthropic,
            Gemini,
            Grok,
            OpenAiCompatible,
            Custom
        };

        public static string NormalizeProviderName(string? provider)
        {
            var p = (provider ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(p))
            {
                return OpenAi;
            }

            if (p.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                return OpenAi;
            }

            if (p.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                || p.Equals("Anthropic (Claude)", StringComparison.OrdinalIgnoreCase)
                || p.Equals("Claude", StringComparison.OrdinalIgnoreCase))
            {
                return Anthropic;
            }

            if (p.Equals("Google Gemini", StringComparison.OrdinalIgnoreCase)
                || p.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
            {
                return Gemini;
            }

            if (p.Equals("xAI Grok", StringComparison.OrdinalIgnoreCase)
                || p.Equals("Grok", StringComparison.OrdinalIgnoreCase)
                || p.Equals("xAI", StringComparison.OrdinalIgnoreCase))
            {
                return Grok;
            }

            if (p.Equals("OpenAI-Compatible", StringComparison.OrdinalIgnoreCase)
                || p.Equals("OpenAI compatible", StringComparison.OrdinalIgnoreCase)
                || p.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
                || p.Equals("LM Studio OpenAI API", StringComparison.OrdinalIgnoreCase))
            {
                return OpenAiCompatible;
            }

            if (p.Equals("Custom", StringComparison.OrdinalIgnoreCase)
                || p.Equals("Localhost custom", StringComparison.OrdinalIgnoreCase))
            {
                return Custom;
            }

            return Custom;
        }

        public static bool RequiresBaseUrl(string provider)
        {
            return string.Equals(NormalizeProviderName(provider), Custom, StringComparison.OrdinalIgnoreCase);
        }

        public static bool SupportsOptionalBaseUrl(string provider)
        {
            return string.Equals(NormalizeProviderName(provider), OpenAiCompatible, StringComparison.OrdinalIgnoreCase);
        }

        public static string GetDefaultBaseUrl(string provider)
        {
            return NormalizeProviderName(provider) switch
            {
                OpenAi => "https://api.openai.com",
                Anthropic => "https://api.anthropic.com",
                Gemini => "https://generativelanguage.googleapis.com",
                Grok => "https://api.x.ai",
                OpenAiCompatible => "https://api.openai.com",
                _ => string.Empty
            };
        }

        public static IChatProvider Create(ApiProviderSettings settings)
        {
            var normalized = NormalizeProviderName(settings.Provider);
            return normalized switch
            {
                OpenAi => new OpenAiProvider(),
                Anthropic => new AnthropicProvider(),
                Gemini => new GeminiProvider(),
                Grok => new GrokProvider(),
                OpenAiCompatible => new OpenAiCompatibleProvider(),
                _ => new CustomProvider()
            };
        }
    }

    internal static class ProviderPayloadHelpers
    {
        public static int ResolveContextWindow(ApiProviderSettings settings, AiSettings owner)
        {
            return settings.ContextWindow > 0
                ? Math.Max(512, settings.ContextWindow)
                : Math.Max(512, owner.ContextWindow);
        }

        public static int ResolveMaxOutputTokens(ApiProviderSettings settings, AiSettings owner)
        {
            return settings.MaxOutputTokens > 0
                ? Math.Max(64, settings.MaxOutputTokens)
                : Math.Max(64, owner.MaxTokens);
        }

        public static float ResolveTemperature(ApiProviderSettings settings, AiSettings owner)
        {
            var raw = settings.Temperature > 0f ? settings.Temperature : owner.Temperature;
            return Math.Clamp(raw, 0f, 2f);
        }

        public static string BuildOpenAiChatCompletionsUrl(string baseUrl)
        {
            var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (trimmed.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed + "/chat/completions";
            }

            return trimmed + "/v1/chat/completions";
        }

        public static string BuildAbsoluteUrl(string baseUrl, string suffix)
        {
            var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return string.Empty;
            }

            if (suffix.StartsWith("/", StringComparison.Ordinal))
            {
                return trimmed + suffix;
            }

            return trimmed + "/" + suffix;
        }

        public static string ExtractOpenAiText(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return string.Empty;
            }

            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (!doc.RootElement.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0)
                {
                    return string.Empty;
                }

                var first = choices[0];
                if (first.TryGetProperty("message", out var msg)
                    && msg.ValueKind == JsonValueKind.Object
                    && msg.TryGetProperty("content", out var content))
                {
                    return content.GetString() ?? string.Empty;
                }

                if (first.TryGetProperty("text", out var text))
                {
                    return text.GetString() ?? string.Empty;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        public static string ExtractAnthropicText(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return string.Empty;
            }

            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (!doc.RootElement.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array)
                {
                    return string.Empty;
                }

                var lines = new List<string>();
                foreach (var item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        var chunk = text.GetString();
                        if (!string.IsNullOrWhiteSpace(chunk))
                        {
                            lines.Add(chunk);
                        }
                    }
                }

                return string.Join(Environment.NewLine, lines).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string ExtractGeminiText(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return string.Empty;
            }

            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (!doc.RootElement.TryGetProperty("candidates", out var candidates)
                    || candidates.ValueKind != JsonValueKind.Array
                    || candidates.GetArrayLength() == 0)
                {
                    return string.Empty;
                }

                var first = candidates[0];
                if (!first.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Object
                    || !content.TryGetProperty("parts", out var parts)
                    || parts.ValueKind != JsonValueKind.Array)
                {
                    return string.Empty;
                }

                var chunks = new List<string>();
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        var chunk = text.GetString();
                        if (!string.IsNullOrWhiteSpace(chunk))
                        {
                            chunks.Add(chunk);
                        }
                    }
                }

                return string.Join(Environment.NewLine, chunks).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        public static ModelValidationResult BuildMissingModelValidation(string message)
        {
            return new ModelValidationResult
            {
                Status = ModelStatus.InvalidPath,
                Message = message
            };
        }

        public static ModelValidationResult BuildReadyValidation(string message, string normalizedPath)
        {
            return new ModelValidationResult
            {
                Status = ModelStatus.Ready,
                Message = message,
                NormalizedPath = normalizedPath
            };
        }

        public static bool TryValidateAbsoluteHttpUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;
        }
    }

    internal abstract class OpenAiStyleProviderBase : IChatProvider
    {
        public abstract string ProviderKey { get; }
        public abstract string DisplayName { get; }
        public abstract bool RequiresBaseUrl { get; }
        public abstract bool SupportsOptionalBaseUrl { get; }

        public virtual int ResolveModelContextWindow(ApiProviderSettings settings, AiSettings owner)
        {
            return ProviderPayloadHelpers.ResolveContextWindow(settings, owner);
        }

        public virtual ModelValidationResult Validate(ApiProviderSettings settings, AiSettings owner)
        {
            if (string.IsNullOrWhiteSpace(settings.ModelName))
            {
                return ProviderPayloadHelpers.BuildMissingModelValidation($"{DisplayName}: model name is required.");
            }

            if (string.IsNullOrWhiteSpace(settings.EncryptedApiKey))
            {
                return ProviderPayloadHelpers.BuildMissingModelValidation($"{DisplayName}: API key is required.");
            }

            var baseUrl = ResolveBaseUrl(settings);
            if (!ProviderPayloadHelpers.TryValidateAbsoluteHttpUrl(baseUrl))
            {
                return ProviderPayloadHelpers.BuildMissingModelValidation($"{DisplayName}: API URL is invalid.");
            }

            return ProviderPayloadHelpers.BuildReadyValidation(
                $"API configured ({DisplayName}).",
                baseUrl);
        }

        public virtual HttpRequestMessage BuildRequest(ApiProviderSettings settings, string systemPrompt, string userPrompt, string apiKey, AiSettings owner)
        {
            var endpoint = ProviderPayloadHelpers.BuildOpenAiChatCompletionsUrl(ResolveBaseUrl(settings));
            var payload = new
            {
                model = settings.ModelName,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt ?? string.Empty },
                    new { role = "user", content = userPrompt ?? string.Empty }
                },
                temperature = ProviderPayloadHelpers.ResolveTemperature(settings, owner),
                max_tokens = ProviderPayloadHelpers.ResolveMaxOutputTokens(settings, owner),
                stream = false
            };

            var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            return request;
        }

        public virtual string ExtractAssistantText(string responseBody)
        {
            return ProviderPayloadHelpers.ExtractOpenAiText(responseBody);
        }

        protected abstract string ResolveBaseUrl(ApiProviderSettings settings);
    }

    internal sealed class OpenAiProvider : OpenAiStyleProviderBase
    {
        public override string ProviderKey => ApiProviderCatalog.OpenAi;
        public override string DisplayName => "OpenAI";
        public override bool RequiresBaseUrl => false;
        public override bool SupportsOptionalBaseUrl => false;

        protected override string ResolveBaseUrl(ApiProviderSettings settings)
        {
            return ApiProviderCatalog.GetDefaultBaseUrl(ProviderKey);
        }
    }

    internal sealed class GrokProvider : OpenAiStyleProviderBase
    {
        public override string ProviderKey => ApiProviderCatalog.Grok;
        public override string DisplayName => "xAI Grok";
        public override bool RequiresBaseUrl => false;
        public override bool SupportsOptionalBaseUrl => false;

        protected override string ResolveBaseUrl(ApiProviderSettings settings)
        {
            return ApiProviderCatalog.GetDefaultBaseUrl(ProviderKey);
        }
    }

    internal sealed class OpenAiCompatibleProvider : OpenAiStyleProviderBase
    {
        public override string ProviderKey => ApiProviderCatalog.OpenAiCompatible;
        public override string DisplayName => "OpenAI-Compatible";
        public override bool RequiresBaseUrl => false;
        public override bool SupportsOptionalBaseUrl => true;

        protected override string ResolveBaseUrl(ApiProviderSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.BaseUrl))
            {
                return settings.BaseUrl.Trim();
            }

            return ApiProviderCatalog.GetDefaultBaseUrl(ProviderKey);
        }
    }

    internal sealed class CustomProvider : OpenAiStyleProviderBase
    {
        public override string ProviderKey => ApiProviderCatalog.Custom;
        public override string DisplayName => "Custom";
        public override bool RequiresBaseUrl => true;
        public override bool SupportsOptionalBaseUrl => false;

        protected override string ResolveBaseUrl(ApiProviderSettings settings)
        {
            return settings.BaseUrl?.Trim() ?? string.Empty;
        }
    }

    internal sealed class AnthropicProvider : IChatProvider
    {
        public string ProviderKey => ApiProviderCatalog.Anthropic;
        public string DisplayName => "Anthropic";
        public bool RequiresBaseUrl => false;
        public bool SupportsOptionalBaseUrl => false;

        public int ResolveModelContextWindow(ApiProviderSettings settings, AiSettings owner)
        {
            return ProviderPayloadHelpers.ResolveContextWindow(settings, owner);
        }

        public ModelValidationResult Validate(ApiProviderSettings settings, AiSettings owner)
        {
            if (string.IsNullOrWhiteSpace(settings.ModelName))
            {
                return ProviderPayloadHelpers.BuildMissingModelValidation("Anthropic: model name is required.");
            }

            if (string.IsNullOrWhiteSpace(settings.EncryptedApiKey))
            {
                return ProviderPayloadHelpers.BuildMissingModelValidation("Anthropic: API key is required.");
            }

            var baseUrl = ApiProviderCatalog.GetDefaultBaseUrl(ProviderKey);
            if (!ProviderPayloadHelpers.TryValidateAbsoluteHttpUrl(baseUrl))
            {
                return ProviderPayloadHelpers.BuildMissingModelValidation("Anthropic: endpoint URL is invalid.");
            }

            return ProviderPayloadHelpers.BuildReadyValidation("API configured (Anthropic).", baseUrl);
        }

        public HttpRequestMessage BuildRequest(ApiProviderSettings settings, string systemPrompt, string userPrompt, string apiKey, AiSettings owner)
        {
            var endpoint = ProviderPayloadHelpers.BuildAbsoluteUrl(ApiProviderCatalog.GetDefaultBaseUrl(ProviderKey), "/v1/messages");
            var payload = new
            {
                model = settings.ModelName,
                system = systemPrompt ?? string.Empty,
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = userPrompt ?? string.Empty }
                        }
                    }
                },
                max_tokens = ProviderPayloadHelpers.ResolveMaxOutputTokens(settings, owner),
                temperature = ProviderPayloadHelpers.ResolveTemperature(settings, owner)
            };

            var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            }

            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            return request;
        }

        public string ExtractAssistantText(string responseBody)
        {
            return ProviderPayloadHelpers.ExtractAnthropicText(responseBody);
        }
    }

    internal sealed class GeminiProvider : IChatProvider
    {
        public string ProviderKey => ApiProviderCatalog.Gemini;
        public string DisplayName => "Google Gemini";
        public bool RequiresBaseUrl => false;
        public bool SupportsOptionalBaseUrl => false;

        public int ResolveModelContextWindow(ApiProviderSettings settings, AiSettings owner)
        {
            return ProviderPayloadHelpers.ResolveContextWindow(settings, owner);
        }

        public ModelValidationResult Validate(ApiProviderSettings settings, AiSettings owner)
        {
            if (string.IsNullOrWhiteSpace(settings.ModelName))
            {
                return ProviderPayloadHelpers.BuildMissingModelValidation("Gemini: model name is required.");
            }

            if (string.IsNullOrWhiteSpace(settings.EncryptedApiKey))
            {
                return ProviderPayloadHelpers.BuildMissingModelValidation("Gemini: API key is required.");
            }

            var baseUrl = ApiProviderCatalog.GetDefaultBaseUrl(ProviderKey);
            if (!ProviderPayloadHelpers.TryValidateAbsoluteHttpUrl(baseUrl))
            {
                return ProviderPayloadHelpers.BuildMissingModelValidation("Gemini: endpoint URL is invalid.");
            }

            return ProviderPayloadHelpers.BuildReadyValidation("API configured (Google Gemini).", baseUrl);
        }

        public HttpRequestMessage BuildRequest(ApiProviderSettings settings, string systemPrompt, string userPrompt, string apiKey, AiSettings owner)
        {
            var baseUrl = ApiProviderCatalog.GetDefaultBaseUrl(ProviderKey).TrimEnd('/');
            var model = Uri.EscapeDataString(settings.ModelName);
            var key = Uri.EscapeDataString(apiKey ?? string.Empty);
            var endpoint = $"{baseUrl}/v1beta/models/{model}:generateContent?key={key}";
            var payload = new
            {
                systemInstruction = new
                {
                    parts = new[]
                    {
                        new { text = systemPrompt ?? string.Empty }
                    }
                },
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[]
                        {
                            new { text = userPrompt ?? string.Empty }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = ProviderPayloadHelpers.ResolveTemperature(settings, owner),
                    maxOutputTokens = ProviderPayloadHelpers.ResolveMaxOutputTokens(settings, owner)
                }
            };

            return new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
        }

        public string ExtractAssistantText(string responseBody)
        {
            return ProviderPayloadHelpers.ExtractGeminiText(responseBody);
        }
    }
}
