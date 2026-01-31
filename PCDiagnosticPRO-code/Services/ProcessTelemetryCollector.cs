using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// P0.1: Robust C# fallback for process telemetry - ULTIMATE FALLBACK.
    /// Chain: 1) System.Diagnostics.Process, 2) Toolhelp32Snapshot (native API)
    /// Collects top 5 processes by RAM and top 5 by CPU with real-time CPU % calculation.
    /// </summary>
    public class ProcessTelemetryCollector
    {
        private const int TopRamCount = 5;
        private const int TopCpuCount = 5;
        private const int CpuSampleWindowMs = 750; // 750ms window for CPU calculation
        private const int TimeoutMs = 20000;

        public async Task<ProcessTelemetryResult> CollectAsync(CancellationToken ct = default)
        {
            var result = new ProcessTelemetryResult { Timestamp = DateTime.UtcNow };
            var sw = Stopwatch.StartNew();

            try
            {
                // Method A: System.Diagnostics.Process with real CPU % calculation
                var processes = await Task.Run(() => CollectViaSystemDiagnosticsWithCpu(ct), ct);

                if (processes.Count == 0)
                {
                    // Method B: Toolhelp32Snapshot (native fallback)
                    App.LogMessage("[ProcessTelemetry] System.Diagnostics failed, trying Toolhelp32Snapshot");
                    processes = await Task.Run(() => CollectViaToolhelp32WithCpu(ct), ct);
                    result.Source = "Toolhelp32Snapshot";
                }
                else
                {
                    result.Source = "System.Diagnostics.Process";
                }

                if (processes.Count == 0)
                {
                    result.Available = false;
                    result.Reason = "all_collection_methods_failed";
                    result.Source = "ProcessTelemetryCollector";
                    result.Confidence = 0;
                    return result;
                }

                // Top by memory (WorkingSet)
                result.TopByMemory = processes
                    .OrderByDescending(p => p.WorkingSetMB)
                    .Take(TopRamCount)
                    .ToList();

                // Top by real CPU %
                result.TopByCpu = processes
                    .Where(p => p.CpuPercent > 0.1) // Filter out idle processes
                    .OrderByDescending(p => p.CpuPercent)
                    .Take(TopCpuCount)
                    .ToList();

                // If no processes with CPU activity, take top by CPU time
                if (result.TopByCpu.Count == 0)
                {
                    result.TopByCpu = processes
                        .Where(p => p.CpuTimeMs > 0)
                        .OrderByDescending(p => p.CpuTimeMs)
                        .Take(TopCpuCount)
                        .ToList();
                }

                result.TotalProcessCount = processes.Count;
                result.AccessDeniedCount = processes.Count(p => p.AccessDenied);
                result.Available = true;
                
                // Confidence based on quality
                if (result.TotalProcessCount >= 50)
                    result.Confidence = 90;
                else if (result.TotalProcessCount >= 10)
                    result.Confidence = 70;
                else
                    result.Confidence = 40;
                    
                result.Quality = result.AccessDeniedCount > processes.Count / 2 ? "partial" : "ok";

                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;

                App.LogMessage($"[ProcessTelemetry] Collected {processes.Count} processes ({result.AccessDeniedCount} access denied) in {result.DurationMs}ms via {result.Source}");
            }
            catch (OperationCanceledException)
            {
                result.Available = false;
                result.Reason = "cancelled";
                result.Source = "ProcessTelemetryCollector";
                result.Confidence = 0;
            }
            catch (Exception ex)
            {
                result.Available = false;
                result.Reason = $"exception: {ex.GetType().Name}: {ex.Message}";
                result.Source = "ProcessTelemetryCollector";
                result.Confidence = 0;
                App.LogMessage($"[ProcessTelemetry] Error: {ex.Message}");
            }

            return result;
        }

        #region Method A: System.Diagnostics.Process with CPU %

        private List<ProcessInfo> CollectViaSystemDiagnosticsWithCpu(CancellationToken ct)
        {
            var result = new List<ProcessInfo>();
            Process[] processes;
            var logicalCores = Environment.ProcessorCount;

            try
            {
                processes = Process.GetProcesses();
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ProcessTelemetry] GetProcesses failed: {ex.Message}");
                return result;
            }

            // Phase 1: Collect initial CPU times
            var initialTimes = new Dictionary<int, (long userTime, long kernelTime, DateTime timestamp)>();
            foreach (var proc in processes)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    initialTimes[proc.Id] = (
                        (long)proc.UserProcessorTime.TotalMilliseconds,
                        (long)proc.TotalProcessorTime.TotalMilliseconds,
                        DateTime.UtcNow
                    );
                }
                catch { /* Access denied or process exited */ }
            }

            // Wait for CPU sample window
            Thread.Sleep(CpuSampleWindowMs);

            // Phase 2: Collect final CPU times and compute %
            foreach (var proc in processes)
            {
                if (ct.IsCancellationRequested) break;

                var info = new ProcessInfo
                {
                    Pid = proc.Id,
                    AccessDenied = false
                };

                try
                {
                    info.Name = proc.ProcessName;
                }
                catch
                {
                    info.Name = $"PID_{proc.Id}";
                    info.AccessDenied = true;
                }

                // WorkingSet (memory)
                try
                {
                    info.WorkingSetMB = Math.Round(proc.WorkingSet64 / (1024.0 * 1024.0), 2);
                    info.PrivateBytesMB = Math.Round(proc.PrivateMemorySize64 / (1024.0 * 1024.0), 2);
                }
                catch
                {
                    info.WorkingSetMB = 0;
                    info.PrivateBytesMB = 0;
                    info.AccessDenied = true;
                }

                // CPU time and percentage
                try
                {
                    var currentTotalMs = (long)proc.TotalProcessorTime.TotalMilliseconds;
                    info.CpuTimeMs = currentTotalMs;

                    // Calculate CPU % using delta
                    if (initialTimes.TryGetValue(proc.Id, out var initial))
                    {
                        var deltaCpu = currentTotalMs - initial.kernelTime;
                        var deltaWallMs = CpuSampleWindowMs;
                        
                        // CPU % = (deltaCpuTime / deltaWallTime) / logicalCores * 100
                        if (deltaWallMs > 0 && logicalCores > 0)
                        {
                            info.CpuPercent = Math.Round((double)deltaCpu / deltaWallMs / logicalCores * 100, 2);
                            info.CpuPercent = Math.Max(0, Math.Min(100, info.CpuPercent)); // Clamp
                        }
                    }
                }
                catch
                {
                    info.CpuTimeMs = 0;
                    info.CpuPercent = 0;
                    info.AccessDenied = true;
                }

                // Start time (best effort)
                try
                {
                    info.StartTime = proc.StartTime.ToString("o");
                }
                catch
                {
                    info.StartTime = null;
                }

                // Thread count
                try
                {
                    info.ThreadCount = proc.Threads.Count;
                }
                catch
                {
                    info.ThreadCount = 0;
                }

                result.Add(info);

                try { proc.Dispose(); } catch { }
            }

            return result;
        }

        #endregion

        #region Method B: Toolhelp32Snapshot (Native Windows API) with CPU %

        private List<ProcessInfo> CollectViaToolhelp32WithCpu(CancellationToken ct)
        {
            var result = new List<ProcessInfo>();
            var logicalCores = Environment.ProcessorCount;

            IntPtr snapshot = IntPtr.Zero;
            try
            {
                snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
                if (snapshot == IntPtr.Zero || snapshot == NativeMethods.INVALID_HANDLE_VALUE)
                {
                    App.LogMessage("[ProcessTelemetry] CreateToolhelp32Snapshot failed");
                    return result;
                }

                var entry = new NativeMethods.PROCESSENTRY32();
                entry.dwSize = (uint)Marshal.SizeOf(entry);

                if (!NativeMethods.Process32First(snapshot, ref entry))
                {
                    App.LogMessage("[ProcessTelemetry] Process32First failed");
                    return result;
                }

                // Phase 1: Enumerate and collect initial times
                var processEntries = new List<(uint pid, string name, uint threads)>();
                var initialTimes = new Dictionary<uint, long>();

                do
                {
                    if (ct.IsCancellationRequested) break;
                    processEntries.Add((entry.th32ProcessID, entry.szExeFile, entry.cntThreads));
                    
                    // Get initial CPU time
                    var cpuTime = GetProcessCpuTimeNative(entry.th32ProcessID);
                    if (cpuTime >= 0)
                        initialTimes[entry.th32ProcessID] = cpuTime;

                } while (NativeMethods.Process32Next(snapshot, ref entry));

                // Wait for CPU sample window
                Thread.Sleep(CpuSampleWindowMs);

                // Phase 2: Calculate CPU % and collect full info
                foreach (var (pid, name, threads) in processEntries)
                {
                    if (ct.IsCancellationRequested) break;

                    var info = new ProcessInfo
                    {
                        Pid = (int)pid,
                        Name = name,
                        ThreadCount = (int)threads,
                        AccessDenied = false
                    };

                    // Get memory info
                    TryGetProcessMemoryInfo(info);

                    // Get final CPU time and calculate %
                    var finalCpuTime = GetProcessCpuTimeNative(pid);
                    if (finalCpuTime >= 0)
                    {
                        info.CpuTimeMs = finalCpuTime;
                        
                        if (initialTimes.TryGetValue(pid, out var initialCpu))
                        {
                            var deltaCpu = finalCpuTime - initialCpu;
                            var deltaWallMs = CpuSampleWindowMs;
                            
                            if (deltaWallMs > 0 && logicalCores > 0)
                            {
                                info.CpuPercent = Math.Round((double)deltaCpu / deltaWallMs / logicalCores * 100, 2);
                                info.CpuPercent = Math.Max(0, Math.Min(100, info.CpuPercent));
                            }
                        }
                    }

                    // Get start time
                    TryGetProcessStartTime(info, pid);

                    result.Add(info);
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ProcessTelemetry] Toolhelp32 error: {ex.Message}");
            }
            finally
            {
                if (snapshot != IntPtr.Zero && snapshot != NativeMethods.INVALID_HANDLE_VALUE)
                {
                    NativeMethods.CloseHandle(snapshot);
                }
            }

            return result;
        }

        private long GetProcessCpuTimeNative(uint pid)
        {
            IntPtr hProcess = IntPtr.Zero;
            try
            {
                hProcess = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
                    false, pid);

                if (hProcess == IntPtr.Zero) return -1;

                if (NativeMethods.GetProcessTimes(hProcess,
                    out var creationTime, out var exitTime,
                    out var kernelTime, out var userTime))
                {
                    return FileTimeToMs(kernelTime) + FileTimeToMs(userTime);
                }
            }
            catch { }
            finally
            {
                if (hProcess != IntPtr.Zero)
                    NativeMethods.CloseHandle(hProcess);
            }
            return -1;
        }

        private void TryGetProcessMemoryInfo(ProcessInfo info)
        {
            IntPtr hProcess = IntPtr.Zero;
            try
            {
                hProcess = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_VM_READ,
                    false,
                    (uint)info.Pid);

                if (hProcess == IntPtr.Zero)
                {
                    info.AccessDenied = true;
                    return;
                }

                var memInfo = new NativeMethods.PROCESS_MEMORY_COUNTERS_EX();
                memInfo.cb = (uint)Marshal.SizeOf(memInfo);

                if (NativeMethods.GetProcessMemoryInfo(hProcess, out memInfo, memInfo.cb))
                {
                    info.WorkingSetMB = Math.Round(memInfo.WorkingSetSize / (1024.0 * 1024.0), 2);
                    info.PrivateBytesMB = Math.Round(memInfo.PrivateUsage / (1024.0 * 1024.0), 2);
                }
                else
                {
                    info.AccessDenied = true;
                }
            }
            catch
            {
                info.AccessDenied = true;
            }
            finally
            {
                if (hProcess != IntPtr.Zero)
                    NativeMethods.CloseHandle(hProcess);
            }
        }

        private void TryGetProcessStartTime(ProcessInfo info, uint pid)
        {
            IntPtr hProcess = IntPtr.Zero;
            try
            {
                hProcess = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
                    false, pid);

                if (hProcess == IntPtr.Zero) return;

                if (NativeMethods.GetProcessTimes(hProcess,
                    out var creationTime, out _, out _, out _))
                {
                    info.StartTime = DateTime.FromFileTime(
                        ((long)creationTime.dwHighDateTime << 32) | creationTime.dwLowDateTime
                    ).ToString("o");
                }
            }
            catch { }
            finally
            {
                if (hProcess != IntPtr.Zero)
                    NativeMethods.CloseHandle(hProcess);
            }
        }

        private long FileTimeToMs(NativeMethods.FILETIME ft)
        {
            long ticks = ((long)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
            return ticks / 10000; // 100ns to ms
        }

        #endregion

        #region Native Methods

        private static class NativeMethods
        {
            public const uint TH32CS_SNAPPROCESS = 0x00000002;
            public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

            public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            public const uint PROCESS_VM_READ = 0x0010;

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(IntPtr hObject);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool GetProcessTimes(IntPtr hProcess,
                out FILETIME lpCreationTime,
                out FILETIME lpExitTime,
                out FILETIME lpKernelTime,
                out FILETIME lpUserTime);

            [DllImport("psapi.dll", SetLastError = true)]
            public static extern bool GetProcessMemoryInfo(IntPtr hProcess,
                out PROCESS_MEMORY_COUNTERS_EX ppsmemCounters, uint cb);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
            public struct PROCESSENTRY32
            {
                public uint dwSize;
                public uint cntUsage;
                public uint th32ProcessID;
                public IntPtr th32DefaultHeapID;
                public uint th32ModuleID;
                public uint cntThreads;
                public uint th32ParentProcessID;
                public int pcPriClassBase;
                public uint dwFlags;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
                public string szExeFile;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct FILETIME
            {
                public uint dwLowDateTime;
                public uint dwHighDateTime;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct PROCESS_MEMORY_COUNTERS_EX
            {
                public uint cb;
                public uint PageFaultCount;
                public UIntPtr PeakWorkingSetSize;
                public UIntPtr WorkingSetSize;
                public UIntPtr QuotaPeakPagedPoolUsage;
                public UIntPtr QuotaPagedPoolUsage;
                public UIntPtr QuotaPeakNonPagedPoolUsage;
                public UIntPtr QuotaNonPagedPoolUsage;
                public UIntPtr PagefileUsage;
                public UIntPtr PeakPagefileUsage;
                public UIntPtr PrivateUsage;
            }
        }

        #endregion
    }

    public class ProcessTelemetryResult
    {
        public bool Available { get; set; }
        public string Source { get; set; } = "";
        public string? Reason { get; set; }
        public string Quality { get; set; } = "ok";
        public int Confidence { get; set; } = 100;
        public DateTime Timestamp { get; set; }
        public long DurationMs { get; set; }
        public int TotalProcessCount { get; set; }
        public int AccessDeniedCount { get; set; }
        public List<ProcessInfo> TopByMemory { get; set; } = new();
        public List<ProcessInfo> TopByCpu { get; set; } = new();
    }

    public class ProcessInfo
    {
        public string Name { get; set; } = "";
        public int Pid { get; set; }
        public double WorkingSetMB { get; set; }
        public double PrivateBytesMB { get; set; }
        public long CpuTimeMs { get; set; }
        public double CpuPercent { get; set; } // Real-time CPU %
        public string? StartTime { get; set; }
        public int ThreadCount { get; set; }
        public bool AccessDenied { get; set; }

        // For backwards compatibility
        public double CpuSeconds => CpuTimeMs / 1000.0;
    }
}
