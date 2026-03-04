using System;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// P0-3: Detailed WMI/CIM error capture - NO MORE "Unknown / No message"
    /// </summary>
    public class WmiErrorInfo
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = "WMI_ERROR";

        [JsonPropertyName("namespace")]
        public string Namespace { get; set; } = "";

        [JsonPropertyName("query")]
        public string Query { get; set; } = "";

        [JsonPropertyName("method")]
        public string Method { get; set; } = "WMI"; // WMI, CIM, PowerShell

        [JsonPropertyName("durationMs")]
        public long DurationMs { get; set; }

        [JsonPropertyName("exceptionType")]
        public string ExceptionType { get; set; } = "";

        [JsonPropertyName("hresult")]
        public int HResult { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("topStackFrame")]
        public string? TopStackFrame { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = "error";

        [JsonPropertyName("isLimitation")]
        public bool IsLimitation { get; set; }

        /// <summary>
        /// Create from exception with full context
        /// </summary>
        public static WmiErrorInfo FromException(Exception ex, string wmiNamespace, string query, string method, long durationMs)
        {
            var error = new WmiErrorInfo
            {
                Namespace = wmiNamespace ?? "unknown",
                Query = query ?? "unknown",
                Method = method ?? "WMI",
                DurationMs = durationMs,
                ExceptionType = ex.GetType().FullName ?? ex.GetType().Name,
                HResult = ex.HResult,
                Message = GetNonEmptyMessage(ex),
                Timestamp = DateTime.UtcNow.ToString("o")
            };

            // Extract top stack frame
            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                var lines = ex.StackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0)
                {
                    error.TopStackFrame = lines[0].Trim();
                }
            }

            // Determine severity and if it's a limitation
            error.DetermineSeverityAndLimitation();

            return error;
        }

        /// <summary>
        /// Create a timeout error
        /// </summary>
        public static WmiErrorInfo FromTimeout(string wmiNamespace, string query, string method, long durationMs)
        {
            return new WmiErrorInfo
            {
                Code = "WMI_TIMEOUT",
                Namespace = wmiNamespace ?? "unknown",
                Query = query ?? "unknown",
                Method = method ?? "WMI",
                DurationMs = durationMs,
                ExceptionType = "TimeoutException",
                HResult = unchecked((int)0x80070079), // ERROR_SEM_TIMEOUT
                Message = $"WMI query timed out after {durationMs}ms",
                Severity = "warning",
                IsLimitation = true
            };
        }

        /// <summary>
        /// Create an access denied error
        /// </summary>
        public static WmiErrorInfo FromAccessDenied(string wmiNamespace, string query, string method)
        {
            return new WmiErrorInfo
            {
                Code = "WMI_ACCESS_DENIED",
                Namespace = wmiNamespace ?? "unknown",
                Query = query ?? "unknown",
                Method = method ?? "WMI",
                ExceptionType = "UnauthorizedAccessException",
                HResult = unchecked((int)0x80070005), // E_ACCESSDENIED
                Message = "Access denied. Admin rights may be required.",
                Severity = "error",
                IsLimitation = false
            };
        }

        private static string GetNonEmptyMessage(Exception ex)
        {
            // NEVER return "Unknown" or empty message
            if (!string.IsNullOrWhiteSpace(ex.Message))
                return ex.Message;

            if (ex.InnerException != null && !string.IsNullOrWhiteSpace(ex.InnerException.Message))
                return $"Inner: {ex.InnerException.Message}";

            // Build message from exception type and HResult
            return $"{ex.GetType().Name} (HResult: 0x{ex.HResult:X8})";
        }

        private void DetermineSeverityAndLimitation()
        {
            // Check for known limitation HResults
            var knownLimitations = new[]
            {
                unchecked((int)0x80041010), // WBEM_E_INVALID_CLASS
                unchecked((int)0x80041002), // WBEM_E_NOT_FOUND
                unchecked((int)0x80041003), // WBEM_E_ACCESS_DENIED (WMI specific)
                unchecked((int)0x80070005), // E_ACCESSDENIED
                unchecked((int)0x80041006), // WBEM_E_OUT_OF_MEMORY
            };

            IsLimitation = Array.Exists(knownLimitations, h => h == HResult);

            // Determine severity
            if (IsLimitation)
            {
                Severity = "warning";
            }
            else if (Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            {
                Severity = "warning";
            }
            else
            {
                Severity = "error";
            }
        }

        public override string ToString()
        {
            return $"[{Code}] {Method}:{Namespace} - {Query} => {ExceptionType} (0x{HResult:X8}): {Message}";
        }
    }
}
