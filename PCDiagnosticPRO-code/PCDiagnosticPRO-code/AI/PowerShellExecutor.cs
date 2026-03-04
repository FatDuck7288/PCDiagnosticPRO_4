using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using PCDiagnosticPro.AI.Models;

namespace PCDiagnosticPro.AI
{
    public sealed class PowerShellExecutor
    {
        private static readonly Regex RebootPattern = new(
            @"reboot\s+required|restart\s+required|redemarrage\s+requis|pending\s+reboot|REBOOT_REQUIRED=1",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public async Task<PowerShellExecutionResult> ExecuteAsync(
            string runId,
            string scriptBody,
            bool requiresAdmin,
            int timeoutSeconds,
            bool simulateOnly = false,
            CancellationToken ct = default)
        {
            var result = new PowerShellExecutionResult();
            var runFolder = PrepareWorkingDirectory(runId);
            var scriptPath = Path.Combine(runFolder, "Autofix.ps1");
            var executionLogPath = Path.Combine(runFolder, "ExecutionLog.txt");
            var transcriptPath = Path.Combine(runFolder, "Transcript.txt");

            result.WorkingDirectory = runFolder;
            result.ScriptPath = scriptPath;
            result.ExecutionLogPath = executionLogPath;
            result.TranscriptPath = transcriptPath;
            result.Elevated = requiresAdmin;

            var safeScriptBody = scriptBody ?? string.Empty;
            var scriptHash = ComputeSha256(safeScriptBody);
            Append(result, "info", $"Preparing PowerShell AutoFix execution. runId={runId} scriptHash={scriptHash}");
            File.WriteAllText(scriptPath, BuildWrappedScript(safeScriptBody, transcriptPath), new UTF8Encoding(false));
            File.WriteAllText(executionLogPath, string.Empty, new UTF8Encoding(false));

            if (simulateOnly)
            {
                Append(result, "info", "Simulation mode enabled. Script execution skipped.");
                var syntaxOk = ValidateScriptSyntax(scriptPath, out var simulationStdOut, out var simulationStdErr);
                result.Started = true;
                result.Completed = true;
                result.ExitCode = syntaxOk ? 0 : 1;
                result.StdOut = simulationStdOut;
                result.StdErr = simulationStdErr;
                AppendText(executionLogPath, simulationStdOut);
                AppendText(executionLogPath, simulationStdErr);
                PersistLog(result);
                return result;
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds)));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            Process? process = null;
            try
            {
                process = StartPowerShellProcess(scriptPath, executionLogPath, requiresAdmin);
                result.Started = true;
                Append(result, "info", requiresAdmin
                    ? "PowerShell started with elevation request (UAC)."
                    : "PowerShell started without elevation.");

                if (!requiresAdmin)
                {
                    var stdOutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
                    var stdErrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
                    await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);

                    result.StdOut = await stdOutTask.ConfigureAwait(false);
                    result.StdErr = await stdErrTask.ConfigureAwait(false);
                    AppendText(executionLogPath, result.StdOut);
                    AppendText(executionLogPath, result.StdErr);
                }
                else
                {
                    await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                }

                result.ExitCode = process.ExitCode;
                result.Completed = true;
                Append(result, "info", $"PowerShell finished with exit code {result.ExitCode}.");
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = ct.IsCancellationRequested;
                result.TimedOut = !result.Cancelled;
                Append(result, "warn", result.Cancelled
                    ? "Execution cancelled by user."
                    : "Execution timed out.");

                TryKill(process);
            }
            catch (Exception ex)
            {
                Append(result, "error", $"Execution failed: {ex.Message}");
            }
            finally
            {
                PersistLog(result);
            }

            var executionText = SafeRead(executionLogPath) + Environment.NewLine + SafeRead(transcriptPath);
            result.RebootRequired = DetectRebootRequired(result.ExitCode, executionText);
            if (result.RebootRequired)
            {
                Append(result, "warn", "Reboot requirement detected.");
                PersistLog(result);
            }

            return result;
        }

        public static void RestartNow()
        {
            var psi = new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/r /t 0",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi);
        }

        private static string PrepareWorkingDirectory(string runId)
        {
            var safeRunId = string.IsNullOrWhiteSpace(runId) ? "unknown" : MakePathSafe(runId);
            var baseFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PCDiagnosticPro",
                "AiAutofix",
                safeRunId);
            Directory.CreateDirectory(baseFolder);
            return baseFolder;
        }

        private static Process StartPowerShellProcess(string scriptPath, string executionLogPath, bool requiresAdmin)
        {
            var escapedScript = scriptPath.Replace("'", "''");
            var escapedLog = executionLogPath.Replace("'", "''");

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory
            };

            if (requiresAdmin)
            {
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"& '{escapedScript}' *>> '{escapedLog}'\"";
            }
            else
            {
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"";
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
            }

            return Process.Start(psi) ?? throw new InvalidOperationException("PowerShell process failed to start.");
        }

        private static string BuildWrappedScript(string scriptBody, string transcriptPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# PCDiagnosticPro AutoFix wrapper");
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            sb.AppendLine("$ProgressPreference = 'SilentlyContinue'");
            sb.AppendLine($"$__transcriptPath = '{transcriptPath.Replace("'", "''")}'");
            sb.AppendLine("try { Start-Transcript -Path $__transcriptPath -Force | Out-Null } catch {}");
            sb.AppendLine("try {");
            sb.AppendLine("  $__autofix = {");
            sb.AppendLine(scriptBody ?? string.Empty);
            sb.AppendLine("  }");
            sb.AppendLine("  & $__autofix");
            sb.AppendLine("} finally {");
            sb.AppendLine("  try { Stop-Transcript | Out-Null } catch {}");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static bool ValidateScriptSyntax(string scriptPath, out string stdOut, out string stdErr)
        {
            stdOut = string.Empty;
            stdErr = string.Empty;

            try
            {
                var escapedPath = scriptPath.Replace("'", "''");
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -NonInteractive -Command \"$ErrorActionPreference='Stop'; [void][scriptblock]::Create((Get-Content -Raw -Path '{escapedPath}')); Write-Output 'SIMULATION_SYNTAX_OK'\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                const int SyntaxCheckTimeoutMs = 15_000;

                using var process = Process.Start(psi);
                if (process == null)
                {
                    stdErr = "Simulation syntax check could not start PowerShell.";
                    return false;
                }

                // Async read to avoid deadlock when both stdout and stderr fill their OS pipe buffers.
                var stdOutTask = process.StandardOutput.ReadToEndAsync();
                var stdErrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(SyntaxCheckTimeoutMs))
                {
                    try { process.Kill(true); } catch { /* ignore kill errors */ }
                    stdErr = $"Syntax check timed out after {SyntaxCheckTimeoutMs / 1000}s.";
                    App.LogMessage($"[AI][AutoFix] ValidateScriptSyntax timed out for {scriptPath}");
                    return false;
                }

                stdOut = stdOutTask.GetAwaiter().GetResult();
                stdErr = stdErrTask.GetAwaiter().GetResult();
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                stdErr = $"Simulation syntax check error: {ex.Message}";
                return false;
            }
        }

        private static bool DetectRebootRequired(int exitCode, string executionText)
        {
            if (exitCode == 3010)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(executionText) && RebootPattern.IsMatch(executionText))
            {
                return true;
            }

            return IsRebootPendingInRegistry();
        }

        private static bool IsRebootPendingInRegistry()
        {
            try
            {
                using var key1 = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
                if (key1 != null) return true;

                using var key2 = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
                if (key2 != null) return true;

                using var key3 = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager");
                var pending = key3?.GetValue("PendingFileRenameOperations");
                return pending is string[] values && values.Length > 0;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[AI][AutoFix] IsRebootPendingInRegistry error: {ex.Message}");
                return false;
            }
        }

        private static void TryKill(Process? process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[AI][AutoFix] TryKill error: {ex.Message}");
            }
        }

        private static string MakePathSafe(string value)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(c, '_');
            }
            return value;
        }

        private static void Append(PowerShellExecutionResult result, string level, string message)
        {
            result.Logs.Add(new AiExecutionLog
            {
                TimestampUtc = DateTime.UtcNow,
                Level = level,
                Message = message
            });
        }

        private static void PersistLog(PowerShellExecutionResult result)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var line in result.Logs)
                {
                    sb.AppendLine($"[{line.TimestampUtc:O}] [{line.Level}] {line.Message}");
                }
                File.AppendAllText(result.ExecutionLogPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                App.LogMessage($"[AI][AutoFix] PersistLog error: {ex.Message}");
            }
        }

        private static void AppendText(string path, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            File.AppendAllText(path, value + Environment.NewLine, new UTF8Encoding(false));
        }

        private static string SafeRead(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[AI][AutoFix] SafeRead error path={path} message={ex.Message}");
                return string.Empty;
            }
        }

        private static string ComputeSha256(string content)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(content);
            return Convert.ToHexString(sha.ComputeHash(bytes));
        }
    }
}
