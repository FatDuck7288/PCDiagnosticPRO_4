using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// P0.2: Centralized WMI query runner with detailed error capture.
    /// NO MORE "Unknown / No message" - always captures namespace, query, HRESULT, duration, exception.
    /// </summary>
    public class WmiQueryRunner
    {
        private static readonly List<WmiErrorInfo> _errors = new();
        private static readonly object _lock = new();
        private const int DefaultTimeoutMs = 30000;

        /// <summary>
        /// Get all collected WMI errors for this session
        /// </summary>
        public static List<WmiErrorInfo> GetErrors()
        {
            lock (_lock)
            {
                return new List<WmiErrorInfo>(_errors);
            }
        }

        /// <summary>
        /// Clear error collection (call at start of scan)
        /// </summary>
        public static void ClearErrors()
        {
            lock (_lock)
            {
                _errors.Clear();
            }
        }

        /// <summary>
        /// Execute a WMI query with full error capture
        /// </summary>
        public static WmiQueryResult<T> Query<T>(
            string wmiNamespace,
            string query,
            Func<ManagementObject, T> mapper,
            int timeoutMs = DefaultTimeoutMs)
        {
            var result = new WmiQueryResult<T>
            {
                Namespace = wmiNamespace,
                Query = query
            };

            var sw = Stopwatch.StartNew();

            try
            {
                var scope = new ManagementScope(wmiNamespace);
                scope.Options.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
                
                var objectQuery = new ObjectQuery(query);
                using var searcher = new ManagementObjectSearcher(scope, objectQuery);
                searcher.Options.Timeout = TimeSpan.FromMilliseconds(timeoutMs);

                var items = new List<T>();
                foreach (ManagementObject obj in searcher.Get())
                {
                    try
                    {
                        items.Add(mapper(obj));
                    }
                    catch (Exception ex)
                    {
                        App.LogMessage($"[WmiQueryRunner] Mapper error for {query}: {ex.Message}");
                    }
                    finally
                    {
                        obj?.Dispose();
                    }
                }

                result.Items = items;
                result.Success = true;
                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;

                App.LogMessage($"[WmiQueryRunner] OK: {wmiNamespace} | {query} | {items.Count} items in {result.DurationMs}ms");
            }
            catch (ManagementException mex)
            {
                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;
                result.Success = false;

                var error = CreateError(mex, wmiNamespace, query, "WMI", result.DurationMs);
                RecordError(error);
                result.Error = error;

                App.LogMessage($"[WmiQueryRunner] ERROR: {error}");
            }
            catch (COMException comEx)
            {
                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;
                result.Success = false;

                var error = CreateError(comEx, wmiNamespace, query, "COM", result.DurationMs);
                RecordError(error);
                result.Error = error;

                App.LogMessage($"[WmiQueryRunner] COM ERROR: {error}");
            }
            catch (UnauthorizedAccessException uaEx)
            {
                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;
                result.Success = false;

                var error = WmiErrorInfo.FromAccessDenied(wmiNamespace, query, "WMI");
                error.DurationMs = result.DurationMs;
                error.TopStackFrame = GetTopStackFrame(uaEx);
                RecordError(error);
                result.Error = error;

                App.LogMessage($"[WmiQueryRunner] ACCESS DENIED: {error}");
            }
            catch (TimeoutException)
            {
                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;
                result.Success = false;

                var error = WmiErrorInfo.FromTimeout(wmiNamespace, query, "WMI", result.DurationMs);
                RecordError(error);
                result.Error = error;

                App.LogMessage($"[WmiQueryRunner] TIMEOUT: {error}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;
                result.Success = false;

                var error = CreateError(ex, wmiNamespace, query, "WMI", result.DurationMs);
                RecordError(error);
                result.Error = error;

                App.LogMessage($"[WmiQueryRunner] EXCEPTION: {error}");
            }

            return result;
        }

        /// <summary>
        /// Execute a WMI query asynchronously with timeout
        /// </summary>
        public static async Task<WmiQueryResult<T>> QueryAsync<T>(
            string wmiNamespace,
            string query,
            Func<ManagementObject, T> mapper,
            CancellationToken ct = default,
            int timeoutMs = DefaultTimeoutMs)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            try
            {
                return await Task.Run(() => Query(wmiNamespace, query, mapper, timeoutMs), cts.Token);
            }
            catch (OperationCanceledException)
            {
                var error = WmiErrorInfo.FromTimeout(wmiNamespace, query, "WMI", timeoutMs);
                RecordError(error);

                return new WmiQueryResult<T>
                {
                    Namespace = wmiNamespace,
                    Query = query,
                    Success = false,
                    Error = error,
                    DurationMs = timeoutMs
                };
            }
        }

        /// <summary>
        /// Execute a single-value WMI query
        /// </summary>
        public static WmiSingleResult<T> QuerySingle<T>(
            string wmiNamespace,
            string query,
            Func<ManagementObject, T> mapper,
            int timeoutMs = DefaultTimeoutMs)
        {
            var result = new WmiSingleResult<T>
            {
                Namespace = wmiNamespace,
                Query = query
            };

            var sw = Stopwatch.StartNew();

            try
            {
                var scope = new ManagementScope(wmiNamespace);
                scope.Options.Timeout = TimeSpan.FromMilliseconds(timeoutMs);

                var objectQuery = new ObjectQuery(query);
                using var searcher = new ManagementObjectSearcher(scope, objectQuery);
                searcher.Options.Timeout = TimeSpan.FromMilliseconds(timeoutMs);

                foreach (ManagementObject obj in searcher.Get())
                {
                    try
                    {
                        result.Value = mapper(obj);
                        result.Success = true;
                        break; // Only need first result
                    }
                    catch (Exception ex)
                    {
                        App.LogMessage($"[WmiQueryRunner] Mapper error for single {query}: {ex.Message}");
                    }
                    finally
                    {
                        obj?.Dispose();
                    }
                }

                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;

                if (!result.Success)
                {
                    result.Reason = "no_results_returned";
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;
                result.Success = false;

                var error = CreateError(ex, wmiNamespace, query, "WMI", result.DurationMs);
                RecordError(error);
                result.Error = error;
                result.Reason = error.Message;
            }

            return result;
        }

        /// <summary>
        /// Invoke a WMI method with error capture
        /// </summary>
        public static WmiMethodResult InvokeMethod(
            string wmiNamespace,
            string className,
            string methodName,
            Dictionary<string, object>? parameters = null,
            int timeoutMs = DefaultTimeoutMs)
        {
            var result = new WmiMethodResult
            {
                Namespace = wmiNamespace,
                ClassName = className,
                MethodName = methodName
            };

            var sw = Stopwatch.StartNew();

            try
            {
                var scope = new ManagementScope(wmiNamespace);
                scope.Options.Timeout = TimeSpan.FromMilliseconds(timeoutMs);

                var classPath = new ManagementPath(className);
                using var managementClass = new ManagementClass(scope, classPath, null);

                ManagementBaseObject? inParams = null;
                if (parameters != null && parameters.Count > 0)
                {
                    inParams = managementClass.GetMethodParameters(methodName);
                    foreach (var kvp in parameters)
                    {
                        inParams[kvp.Key] = kvp.Value;
                    }
                }

                var outParams = managementClass.InvokeMethod(methodName, inParams, null);

                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;
                result.Success = true;
                result.ReturnValue = outParams?["ReturnValue"];

                App.LogMessage($"[WmiQueryRunner] Method OK: {wmiNamespace}.{className}.{methodName} in {result.DurationMs}ms");
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;
                result.Success = false;

                var query = $"{className}.{methodName}()";
                var error = CreateError(ex, wmiNamespace, query, "WMI_METHOD", result.DurationMs);
                RecordError(error);
                result.Error = error;

                App.LogMessage($"[WmiQueryRunner] Method ERROR: {error}");
            }

            return result;
        }

        #region Helper Methods

        private static WmiErrorInfo CreateError(Exception ex, string wmiNamespace, string query, string method, long durationMs)
        {
            var error = WmiErrorInfo.FromException(ex, wmiNamespace, query, method, durationMs);
            
            // Extract specific ManagementException info
            if (ex is ManagementException mex)
            {
                error.Code = $"WBEM_{mex.ErrorCode}";
                error.HResult = (int)mex.ErrorCode;
            }
            
            return error;
        }

        private static string? GetTopStackFrame(Exception ex)
        {
            if (string.IsNullOrEmpty(ex.StackTrace)) return null;
            var lines = ex.StackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 ? lines[0].Trim() : null;
        }

        private static void RecordError(WmiErrorInfo error)
        {
            lock (_lock)
            {
                _errors.Add(error);
                
                // Also log to signals log if available
                try
                {
                    var logPath = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), 
                        "PCDiagnosticPro_wmi_errors.log");
                    System.IO.File.AppendAllText(logPath, 
                        $"{DateTime.Now:o} | {error}\n");
                }
                catch { /* Ignore log failures */ }
            }
        }

        #endregion
    }

    #region Result Classes

    public class WmiQueryResult<T>
    {
        public string Namespace { get; set; } = "";
        public string Query { get; set; } = "";
        public bool Success { get; set; }
        public List<T> Items { get; set; } = new();
        public WmiErrorInfo? Error { get; set; }
        public long DurationMs { get; set; }

        public int Count => Items?.Count ?? 0;
        public bool HasItems => Count > 0;
    }

    public class WmiSingleResult<T>
    {
        public string Namespace { get; set; } = "";
        public string Query { get; set; } = "";
        public bool Success { get; set; }
        public T? Value { get; set; }
        public string? Reason { get; set; }
        public WmiErrorInfo? Error { get; set; }
        public long DurationMs { get; set; }
    }

    public class WmiMethodResult
    {
        public string Namespace { get; set; } = "";
        public string ClassName { get; set; } = "";
        public string MethodName { get; set; } = "";
        public bool Success { get; set; }
        public object? ReturnValue { get; set; }
        public WmiErrorInfo? Error { get; set; }
        public long DurationMs { get; set; }
    }

    #endregion
}
