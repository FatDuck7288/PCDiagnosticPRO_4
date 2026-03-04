using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Lists recent minidumps in %SystemRoot%\Minidump with file name, date, and optional BugCheck code.
    /// No full kernel analysis; lightweight exception stream read only.
    /// </summary>
    public static class MinidumpListCollector
    {
        public const int DefaultMaxDumps = 20;
        private const uint MinidumpSignature = 0x504d444d; // 'PMDM'

        /// <summary>
        /// Collect list of recent minidump files (name, date, optional BugCheck code).
        /// </summary>
        public static async Task<List<MinidumpEntry>?> CollectAsync(
            int maxDumps = DefaultMaxDumps,
            System.Threading.CancellationToken ct = default)
        {
            var results = new List<MinidumpEntry>();
            await Task.Run(() =>
            {
                try
                {
                    var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? "C:\\Windows";
                    var minidumpDir = Path.Combine(systemRoot, "Minidump");
                    if (!Directory.Exists(minidumpDir))
                    {
                        App.LogMessage("[MinidumpList] Dossier Minidump non trouvé");
                        return;
                    }

                    DirectoryInfo dir;
                    try
                    {
                        dir = new DirectoryInfo(minidumpDir);
                    }
                    catch (Exception ex)
                    {
                        App.LogMessage($"[MinidumpList] Accès répertoire: {ex.Message}");
                        return;
                    }

                    FileInfo[] files;
                    try
                    {
                        files = dir.GetFiles("*.dmp");
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        App.LogMessage($"[MinidumpList] Accès refusé: {ex.Message}");
                        return;
                    }

                    var ordered = files
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .Take(maxDumps)
                        .ToList();

                    foreach (var f in ordered)
                    {
                        if (ct.IsCancellationRequested) break;
                        var entry = new MinidumpEntry
                        {
                            FileName = f.Name,
                            LastWriteTimeUtc = f.LastWriteTimeUtc
                        };
                        TryReadBugCheckCode(f.FullName, entry);
                        results.Add(entry);
                    }

                    if (results.Count > 0)
                        App.LogMessage($"[MinidumpList] {results.Count} minidump(s) listé(s)");
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[MinidumpList] Erreur: {ex.Message}");
                }
            }, ct).ConfigureAwait(false);

            return results.Count > 0 ? results : null;
        }

        /// <summary>
        /// Lightweight read of Exception stream (type 6) to get ExceptionCode (BugCheck code for kernel dumps).
        /// </summary>
        private static void TryReadBugCheckCode(string filePath, MinidumpEntry entry)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Length < 32) return;

                var header = new byte[32];
                if (fs.Read(header, 0, 32) != 32) return;

                var sig = BitConverter.ToUInt32(header, 0);
                if (sig != MinidumpSignature) return;

                int numStreams = BitConverter.ToInt32(header, 8);
                uint streamDirRva = BitConverter.ToUInt32(header, 12);
                if (numStreams <= 0 || streamDirRva >= fs.Length) return;

                fs.Seek(streamDirRva, SeekOrigin.Begin);
                for (int i = 0; i < numStreams; i++)
                {
                    var dirEntry = new byte[12];
                    if (fs.Read(dirEntry, 0, 12) != 12) break;
                    int streamType = BitConverter.ToInt32(dirEntry, 0);
                    int dataSize = BitConverter.ToInt32(dirEntry, 4);
                    uint rva = BitConverter.ToUInt32(dirEntry, 8);
                    if (streamType != 6) continue; // ExceptionStream

                    if (rva >= fs.Length || rva + 12 > fs.Length) break;
                    fs.Seek(rva, SeekOrigin.Begin);
                    var streamData = new byte[12];
                    if (fs.Read(streamData, 0, 12) != 12) break;
                    // MINIDUMP_EXCEPTION_STREAM: ThreadId (4), __alignment (4), ExceptionRecord.ExceptionCode (4)
                    uint exceptionCode = BitConverter.ToUInt32(streamData, 8);
                    entry.BugCheckCode = exceptionCode;
                    break;
                }
            }
            catch (IOException)
            {
                // File in use or locked - skip BugCheck
            }
            catch (Exception ex)
            {
                App.LogMessage($"[MinidumpList] Lecture BugCheck {Path.GetFileName(filePath)}: {ex.Message}");
            }
        }
    }
}
