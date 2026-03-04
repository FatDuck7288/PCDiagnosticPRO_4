using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.AI.Interfaces
{
    /// <summary>
    /// Abstraction over a local LLM inference engine.
    /// Implementations must be safe to call from any thread (UI dispatching is caller's responsibility).
    /// </summary>
    public interface ILlmClient
    {
        /// <summary>Returns true if the model is loaded and ready.</summary>
        bool IsReady { get; }

        /// <summary>Short human-readable status (e.g. "Ready", "Model not found", "Loading…").</summary>
        string StatusMessage { get; }

        /// <summary>
        /// Single-shot generation — returns the full response as one string.
        /// </summary>
        Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);

        /// <summary>
        /// Streaming generation — yields tokens as they are produced.
        /// Caller appends tokens to a StringBuilder or UI element.
        /// </summary>
        IAsyncEnumerable<string> StreamAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);

        /// <summary>
        /// Dry-run ping: send a minimal prompt and verify the model responds without error.
        /// Returns true on success.
        /// </summary>
        Task<bool> PingAsync(CancellationToken ct = default);

        /// <summary>Dispose the underlying model and release memory.</summary>
        void Unload();
    }
}
