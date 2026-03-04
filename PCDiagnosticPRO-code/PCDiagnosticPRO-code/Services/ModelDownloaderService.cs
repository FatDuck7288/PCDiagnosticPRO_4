using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.Services
{
    public sealed class ModelDownloaderService
    {
        public const string Qwen3Q4FileName = "Qwen3-8B-Q4_K_M.gguf";
        public const string Qwen3Q4DownloadUrl = "https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/main/Qwen3-8B-Q4_K_M.gguf";

        public const string QwenCoderQ4FileName = "qwen2.5-coder-14b-instruct-q4_k_m.gguf";
        public const string QwenCoderQ4DownloadUrl = "https://huggingface.co/Qwen/Qwen2.5-Coder-14B-Instruct-GGUF/resolve/main/qwen2.5-coder-14b-instruct-q4_k_m.gguf";

        private static readonly HttpClient SharedHttpClient = CreateHttpClient();

        public static readonly string[] KnownQwen3FileNames =
        {
            Qwen3Q4FileName,
            "Qwen3-8B-Q5_K_M.gguf",
            "Qwen3-8B-Q6_K.gguf",
            "Qwen3-8B-Q8_0.gguf"
        };

        public static readonly string[] KnownQwenCoderFileNames =
        {
            QwenCoderQ4FileName,
            "qwen2.5-coder-14b-instruct-q5_k_m.gguf",
            "qwen2.5-coder-14b-instruct-q3_k_m.gguf"
        };

        private readonly HttpClient _httpClient;

        public ModelDownloaderService()
            : this(SharedHttpClient)
        {
        }

        internal ModelDownloaderService(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<ModelDownloadResult> DownloadQwen3Q4Async(
            string targetDirectory,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return ModelDownloadResult.CreateFailure("Target directory is required.");
            }

            var targetPath = Path.Combine(targetDirectory.Trim(), Qwen3Q4FileName);
            return await DownloadFileAsync(Qwen3Q4DownloadUrl, targetPath, progress, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ModelDownloadResult> DownloadQwenCoderQ4Async(
            string targetDirectory,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return ModelDownloadResult.CreateFailure("Target directory is required.");
            }

            var targetPath = Path.Combine(targetDirectory.Trim(), QwenCoderQ4FileName);
            return await DownloadFileAsync(QwenCoderQ4DownloadUrl, targetPath, progress, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ModelDownloadResult> DownloadFileAsync(
            string sourceUrl,
            string destinationPath,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                return ModelDownloadResult.CreateFailure("Source URL is required.");
            }

            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                return ModelDownloadResult.CreateFailure("Destination path is required.");
            }

            var normalizedDestination = destinationPath.Trim();
            var targetDirectory = Path.GetDirectoryName(normalizedDestination);
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return ModelDownloadResult.CreateFailure("Unable to resolve destination directory.");
            }

            Directory.CreateDirectory(targetDirectory);

            var tempPath = normalizedDestination + ".part";

            try
            {
                // Resume support: check if a partial download already exists.
                long resumeFrom = 0;
                if (File.Exists(tempPath))
                {
                    try
                    {
                        // Probe for exclusive access. If the .part file is held by another
                        // process (e.g. stale handle from a previous crash), discard it and
                        // start fresh rather than failing the entire download.
                        using var probe = new FileStream(
                            tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        resumeFrom = probe.Length;
                    }
                    catch (IOException)
                    {
                        TryDeleteFile(tempPath);
                        resumeFrom = 0;
                    }
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, sourceUrl.Trim());
                request.Headers.Accept.ParseAdd("application/octet-stream");
                if (resumeFrom > 0)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);
                }

                using var response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                // If the server doesn't support range requests it returns 200 — restart from scratch.
                bool resuming = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (!resuming && resumeFrom > 0)
                {
                    TryDeleteFile(tempPath);
                    resumeFrom = 0;
                }

                if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                {
                    return ModelDownloadResult.CreateFailure(
                        $"Download failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
                }

                // Guard against HTML error pages returned as HTTP 200 (e.g. HuggingFace login redirect).
                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    return ModelDownloadResult.CreateFailure(
                        "Server returned an HTML page instead of the model file. " +
                        "The download URL may require authentication or may have changed.");
                }

                var totalBytes = (response.Content.Headers.ContentLength is long cl && cl > 0)
                    ? (long?)(resumeFrom + cl)
                    : null;

                await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                long downloadedBytes = resumeFrom;
                progress?.Report(new ModelDownloadProgress(downloadedBytes, totalBytes));

                await using (var targetStream = new FileStream(
                    tempPath,
                    resuming ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 1024,
                    useAsync: true))
                {
                    var buffer = new byte[1024 * 1024];
                    while (true)
                    {
                        var bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                        if (bytesRead <= 0)
                        {
                            break;
                        }

                        await targetStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                        downloadedBytes += bytesRead;
                        progress?.Report(new ModelDownloadProgress(downloadedBytes, totalBytes));
                    }

                    await targetStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (File.Exists(normalizedDestination))
                {
                    try
                    {
                        File.Delete(normalizedDestination);
                    }
                    catch (IOException)
                    {
                        // The destination file is locked (model loaded by LLamaSharp).
                        // Remove the .part file so the next attempt can download cleanly.
                        TryDeleteFile(tempPath);
                        return ModelDownloadResult.CreateFailure(
                            "The model file is in use by another process. " +
                            "Unload the AI model before downloading.");
                    }
                }

                File.Move(tempPath, normalizedDestination);
                return ModelDownloadResult.CreateSuccess(normalizedDestination, downloadedBytes, totalBytes);
            }
            catch (OperationCanceledException)
            {
                // Keep the .part file so the next attempt can resume.
                return ModelDownloadResult.CreateCancelled();
            }
            catch (Exception ex)
            {
                TryDeleteFile(tempPath);
                return ModelDownloadResult.CreateFailure(ex.Message);
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            };

            var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PCDiagnosticPro/1.0");
            return client;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }

    public sealed class ModelDownloadResult
    {
        public bool Success { get; init; }
        public bool Cancelled { get; init; }
        public string FilePath { get; init; } = string.Empty;
        public string ErrorMessage { get; init; } = string.Empty;
        public long BytesDownloaded { get; init; }
        public long? TotalBytes { get; init; }

        public static ModelDownloadResult CreateSuccess(string path, long downloaded, long? totalBytes)
        {
            return new ModelDownloadResult
            {
                Success = true,
                FilePath = path,
                BytesDownloaded = downloaded,
                TotalBytes = totalBytes
            };
        }

        public static ModelDownloadResult CreateFailure(string message)
        {
            return new ModelDownloadResult
            {
                Success = false,
                ErrorMessage = message ?? "Unknown download error."
            };
        }

        public static ModelDownloadResult CreateCancelled()
        {
            return new ModelDownloadResult
            {
                Success = false,
                Cancelled = true,
                ErrorMessage = "Download cancelled."
            };
        }
    }

    public readonly struct ModelDownloadProgress
    {
        public ModelDownloadProgress(long bytesDownloaded, long? totalBytes)
        {
            BytesDownloaded = Math.Max(0, bytesDownloaded);
            TotalBytes = totalBytes;
        }

        public long BytesDownloaded { get; }
        public long? TotalBytes { get; }
    }
}
