using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Canonical scan storage service.
    /// Contract: %LOCALAPPDATA%\PCDiagnosticPRO\Scans\{RunId}\
    ///   - meta.json                 -> lightweight metadata (always written)
    ///   - scan_meta.json            -> legacy metadata mirror
    ///   - scan_result_combined.json -> full data copy (written atomically)
    ///   - snapshot.json             -> diagnostic snapshot extract
    ///   - unified_report.txt        -> normalized unified TXT copy
    /// </summary>
    public static class ScanStorageService
    {
        public const string MetaFileName = "meta.json";
        public const string LegacyMetaFileName = "scan_meta.json";
        public const string CombinedFileName = "scan_result_combined.json";
        public const string SnapshotFileName = "snapshot.json";
        public const string UnifiedReportFileName = "unified_report.txt";
        public const string IndexFileName = "scans_index.json";

        /// <summary>Base directory for all run folders.</summary>
        public static readonly string BaseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCDiagnosticPRO",
            "Scans");

        private static readonly JsonSerializerOptions _opts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static string GetRunFolder(string runId) => Path.Combine(BaseDir, runId);

        public static string GetMetaPath(string runId) => Path.Combine(GetRunFolder(runId), MetaFileName);

        public static string GetLegacyMetaPath(string runId) => Path.Combine(GetRunFolder(runId), LegacyMetaFileName);

        public static string GetCombinedJsonPath(string runId) => Path.Combine(GetRunFolder(runId), CombinedFileName);

        public static string GetSnapshotPath(string runId) => Path.Combine(GetRunFolder(runId), SnapshotFileName);

        public static string GetUnifiedReportPath(string runId) => Path.Combine(GetRunFolder(runId), UnifiedReportFileName);

        public static string GetIndexPath() => Path.Combine(BaseDir, IndexFileName);

        public static string EnsureRunFolder(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId))
                throw new ArgumentException("RunId is required.", nameof(runId));

            var folder = GetRunFolder(runId);
            Directory.CreateDirectory(folder);
            return folder;
        }

        public static void SaveMeta(ScanMeta meta)
        {
            if (meta == null) throw new ArgumentNullException(nameof(meta));
            if (string.IsNullOrWhiteSpace(meta.RunId))
                throw new ArgumentException("ScanMeta.RunId is required.", nameof(meta));

            try
            {
                EnsureRunFolder(meta.RunId);
                var target = GetMetaPath(meta.RunId);
                var legacyTarget = GetLegacyMetaPath(meta.RunId);
                var json = JsonSerializer.Serialize(meta, _opts);
                WriteTextAtomic(target, json);
                WriteTextAtomic(legacyTarget, json);
                UpsertIndex(meta);
                App.LogMessage($"[ScanStorage][RunId:{meta.RunId}] Meta saved status={meta.Status} score={meta.Score} path={target}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ScanStorage][RunId:{meta.RunId}] SaveMeta failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Writes scan_result_combined.json atomically in the canonical run folder.
        /// </summary>
        public static string SaveCombinedJson(string runId, string combinedJson)
        {
            if (string.IsNullOrWhiteSpace(runId))
                throw new ArgumentException("RunId is required.", nameof(runId));
            if (combinedJson == null)
                throw new ArgumentNullException(nameof(combinedJson));

            try
            {
                EnsureRunFolder(runId);
                var target = GetCombinedJsonPath(runId);
                WriteTextAtomic(target, combinedJson);
                App.LogMessage($"[ScanStorage][RunId:{runId}] Combined saved atomically: {target}");
                return target;
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ScanStorage][RunId:{runId}] SaveCombinedJson failed: {ex.Message}");
                throw;
            }
        }

        public static Task<string> SaveCombinedJsonAsync(string runId, string combinedJson)
        {
            return Task.Run(() => SaveCombinedJson(runId, combinedJson));
        }

        public static string SaveSnapshotJson(string runId, string snapshotJson)
        {
            if (string.IsNullOrWhiteSpace(runId))
                throw new ArgumentException("RunId is required.", nameof(runId));
            if (snapshotJson == null)
                throw new ArgumentNullException(nameof(snapshotJson));

            EnsureRunFolder(runId);
            var target = GetSnapshotPath(runId);
            WriteTextAtomic(target, snapshotJson);
            App.LogMessage($"[ScanStorage][RunId:{runId}] Snapshot saved atomically: {target}");
            return target;
        }

        public static string SaveUnifiedReportCopy(string runId, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(runId))
                throw new ArgumentException("RunId is required.", nameof(runId));
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("Source unified report path is required.", nameof(sourcePath));
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("Unified report source file not found.", sourcePath);

            EnsureRunFolder(runId);
            var target = GetUnifiedReportPath(runId);
            File.Copy(sourcePath, target, overwrite: true);
            App.LogMessage($"[ScanStorage][RunId:{runId}] Unified report copied: {target}");
            return target;
        }

        /// <summary>
        /// Backward-compatible helper used by older callers.
        /// </summary>
        public static async Task CopyCombinedJsonAsync(string runId, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                App.LogMessage($"[ScanStorage][RunId:{runId}] CopyCombinedJson skipped: source not found ({sourcePath})");
                return;
            }

            var content = await File.ReadAllTextAsync(sourcePath, Encoding.UTF8).ConfigureAwait(false);
            SaveCombinedJson(runId, content);
        }

        public static void CleanupRunTempFiles(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId))
                return;

            try
            {
                var folder = GetRunFolder(runId);
                if (!Directory.Exists(folder))
                    return;

                var tmpCandidates = Directory.GetFiles(folder, "*.tmp", SearchOption.TopDirectoryOnly);
                foreach (var tmp in tmpCandidates)
                {
                    try { File.Delete(tmp); }
                    catch (Exception ex)
                    {
                        App.LogMessage($"[ScanStorage][RunId:{runId}] Cleanup temp failed ({tmp}): {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ScanStorage][RunId:{runId}] CleanupRunTempFiles failed: {ex.Message}");
            }
        }

        public static void DeleteCombinedJsonIfExists(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId))
                return;

            try
            {
                var combinedPath = GetCombinedJsonPath(runId);
                if (File.Exists(combinedPath))
                {
                    File.Delete(combinedPath);
                    App.LogMessage($"[ScanStorage][RunId:{runId}] Combined removed after cancel/failure.");
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ScanStorage][RunId:{runId}] DeleteCombinedJsonIfExists failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Enumerates scan metadata sorted newest-first.
        /// Repair mode:
        /// - if meta missing but combined JSON exists, rebuild best-effort meta and persist it.
        /// </summary>
        public static List<ScanMeta> EnumerateScans()
        {
            var result = new List<ScanMeta>();
            try
            {
                if (!Directory.Exists(BaseDir))
                {
                    App.LogMessage($"[ScanStorage] BaseDir not found: {BaseDir}");
                    return result;
                }

                var loadedRunIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var fromIndex = LoadFromIndex();
                foreach (var meta in fromIndex)
                {
                    if (string.IsNullOrWhiteSpace(meta.RunId))
                        continue;

                    result.Add(meta);
                    loadedRunIds.Add(meta.RunId);
                }

                var folders = Directory.GetDirectories(BaseDir);
                App.LogMessage($"[ScanStorage] EnumerateScans baseDir={BaseDir} folders={folders.Length}");

                var loaded = 0;
                var repaired = 0;
                var parseFailures = 0;
                var totalMissingMeta = 0;
                foreach (var folder in folders)
                {
                    var runId = Path.GetFileName(folder) ?? "unknown";
                    if (loadedRunIds.Contains(runId))
                        continue;

                    var metaPath = Path.Combine(folder, MetaFileName);
                    if (!File.Exists(metaPath))
                        metaPath = Path.Combine(folder, LegacyMetaFileName);
                    var combinedPath = Path.Combine(folder, CombinedFileName);

                    if (File.Exists(metaPath))
                    {
                        if (TryReadMeta(metaPath, out var meta, out var parseError) && meta != null)
                        {
                            if (string.IsNullOrWhiteSpace(meta.RunId))
                                meta.RunId = runId;

                            if (File.Exists(combinedPath))
                                meta.CombinedJsonPath = combinedPath;
                            meta.SnapshotPath ??= File.Exists(Path.Combine(folder, SnapshotFileName))
                                ? Path.Combine(folder, SnapshotFileName)
                                : null;
                            meta.UnifiedTxtPath ??= File.Exists(Path.Combine(folder, UnifiedReportFileName))
                                ? Path.Combine(folder, UnifiedReportFileName)
                                : null;

                            result.Add(meta);
                            loaded++;
                        }
                        else
                        {
                            parseFailures++;
                            App.LogMessage($"[ScanStorage][RunId:{runId}] Meta parse failed ({metaPath}): {parseError}");
                            result.Add(BuildCorruptMetaPlaceholder(runId, folder, parseError));
                        }
                        continue;
                    }

                    totalMissingMeta++;
                    if (!File.Exists(combinedPath))
                        continue;

                    if (TryBuildMetaFromCombined(runId, folder, combinedPath, out var rebuilt, out var repairError) && rebuilt != null)
                    {
                        rebuilt.CombinedJsonPath = combinedPath;
                        rebuilt.SnapshotPath = File.Exists(Path.Combine(folder, SnapshotFileName))
                            ? Path.Combine(folder, SnapshotFileName)
                            : null;
                        rebuilt.UnifiedTxtPath = File.Exists(Path.Combine(folder, UnifiedReportFileName))
                            ? Path.Combine(folder, UnifiedReportFileName)
                            : null;
                        try
                        {
                            SaveMeta(rebuilt);
                        }
                        catch (Exception ex)
                        {
                            App.LogMessage($"[ScanStorage][RunId:{runId}] Meta repair persist failed: {ex.Message}");
                        }

                        result.Add(rebuilt);
                        repaired++;
                        App.LogMessage($"[ScanStorage][RunId:{runId}] Meta repaired from combined JSON.");
                    }
                    else
                    {
                        parseFailures++;
                        App.LogMessage($"[ScanStorage][RunId:{runId}] Meta repair failed: {repairError}");
                        result.Add(BuildCorruptMetaPlaceholder(runId, folder, repairError));
                    }
                }

                App.LogMessage(
                    $"[ScanStorage] EnumerateScans done total={result.Count} loaded={loaded} repaired={repaired} parseFailures={parseFailures} missingMeta={totalMissingMeta} fromIndex={fromIndex.Count}");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ScanStorage] EnumerateScans fatal error: {ex.Message}");
            }

            return result
                .OrderByDescending(m => m.StartTime)
                .ThenByDescending(m => m.EndTime ?? DateTime.MinValue)
                .ToList();
        }

        private static bool TryReadMeta(string metaPath, out ScanMeta? meta, out string error)
        {
            meta = null;
            error = string.Empty;
            try
            {
                var json = File.ReadAllText(metaPath, Encoding.UTF8);
                meta = JsonSerializer.Deserialize<ScanMeta>(json, _opts);
                if (meta == null)
                {
                    error = "Meta deserialization returned null.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static ScanMeta BuildCorruptMetaPlaceholder(string runId, string folder, string details)
        {
            var started = SafeGetCreationUtc(folder);
            var ended = SafeGetWriteUtc(folder);
            return new ScanMeta
            {
                RunId = runId,
                StartTime = started,
                EndTime = ended > started ? ended : null,
                MachineName = Environment.MachineName,
                Status = ScanStatus.Failed,
                Grade = "?",
                ErrorSummary = $"Meta file invalid: {details}",
                StatusReason = "meta_corrupt"
            };
        }

        private static bool TryBuildMetaFromCombined(
            string runId,
            string folder,
            string combinedPath,
            out ScanMeta? meta,
            out string error)
        {
            meta = null;
            error = string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(combinedPath, Encoding.UTF8));
                var root = doc.RootElement;
                var now = DateTime.UtcNow;
                var started = SafeGetCreationUtc(folder);
                var ended = SafeGetWriteUtc(combinedPath);

                var rebuilt = new ScanMeta
                {
                    RunId = runId,
                    StartTime = started,
                    EndTime = ended >= started ? ended : now,
                    MachineName = Environment.MachineName,
                    Status = ScanStatus.Partial,
                    Grade = "N/A",
                    AppVersion = typeof(ScanStorageService).Assembly.GetName().Version?.ToString() ?? "unknown",
                    ErrorSummary = "Meta repaired from combined JSON.",
                    StatusReason = "meta_repaired_from_combined"
                };

                if (root.TryGetProperty("metadata", out var metaEl) && metaEl.ValueKind == JsonValueKind.Object)
                {
                    if (metaEl.TryGetProperty("runId", out var runIdEl))
                    {
                        var parsedRunId = runIdEl.GetString();
                        if (!string.IsNullOrWhiteSpace(parsedRunId))
                            rebuilt.RunId = parsedRunId;
                    }

                    if (metaEl.TryGetProperty("timestamp", out var tsEl) && TryParseDateTime(tsEl.GetString(), out var parsedTs))
                        rebuilt.StartTime = parsedTs;

                    if (metaEl.TryGetProperty("durationSeconds", out var durEl) && durEl.TryGetDouble(out var dur))
                        rebuilt.DurationSeconds = dur;

                    if (metaEl.TryGetProperty("partialFailure", out var partialEl) &&
                        partialEl.ValueKind == JsonValueKind.True)
                    {
                        rebuilt.Status = ScanStatus.Partial;
                    }
                }

                if (root.TryGetProperty("scan_powershell", out var psEl) &&
                    psEl.ValueKind == JsonValueKind.Object &&
                    psEl.TryGetProperty("scoreV2", out var scoreV2) &&
                    scoreV2.ValueKind == JsonValueKind.Object)
                {
                    if (scoreV2.TryGetProperty("score", out var scoreEl) && scoreEl.TryGetInt32(out var score))
                        rebuilt.Score = score;
                    if (scoreV2.TryGetProperty("grade", out var gradeEl))
                        rebuilt.Grade = gradeEl.GetString() ?? rebuilt.Grade;
                }

                if (root.TryGetProperty("run_status", out var runStatusEl) &&
                    runStatusEl.ValueKind == JsonValueKind.Object &&
                    runStatusEl.TryGetProperty("state", out var stateEl))
                {
                    var state = stateEl.GetString() ?? string.Empty;
                    rebuilt.Status = MapRunState(state, rebuilt.Status);

                    if (runStatusEl.TryGetProperty("reasonCodes", out var reasonsEl) && reasonsEl.ValueKind == JsonValueKind.Array)
                    {
                        rebuilt.StatusReason = string.Join(
                            "|",
                            reasonsEl.EnumerateArray()
                                .Where(x => x.ValueKind == JsonValueKind.String)
                                .Select(x => x.GetString())
                                .Where(x => !string.IsNullOrWhiteSpace(x)));
                    }
                }
                else if (rebuilt.Score > 0)
                {
                    rebuilt.Status = ScanStatus.Success;
                }

                try
                {
                    rebuilt.CombinedSizeBytes = new FileInfo(combinedPath).Length;
                }
                catch
                {
                    rebuilt.CombinedSizeBytes = 0;
                }

                meta = rebuilt;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static ScanStatus MapRunState(string state, ScanStatus fallback)
        {
            if (string.IsNullOrWhiteSpace(state))
                return fallback;

            return state.Trim().ToLowerInvariant() switch
            {
                "ok" => ScanStatus.Success,
                "success" => ScanStatus.Success,
                "partial" => ScanStatus.Partial,
                "incomplete" => ScanStatus.Partial,
                "failed" => ScanStatus.Failed,
                "error" => ScanStatus.Failed,
                "cancelled" => ScanStatus.Cancelled,
                "canceled" => ScanStatus.Cancelled,
                _ => fallback
            };
        }

        private static bool TryParseDateTime(string? raw, out DateTime parsed)
        {
            if (!string.IsNullOrWhiteSpace(raw) && DateTime.TryParse(raw, out parsed))
            {
                parsed = parsed.ToUniversalTime();
                return true;
            }

            parsed = default;
            return false;
        }

        private static DateTime SafeGetCreationUtc(string path)
        {
            try { return Directory.GetCreationTimeUtc(path); }
            catch { return DateTime.UtcNow; }
        }

        private static DateTime SafeGetWriteUtc(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch { return DateTime.UtcNow; }
        }

        private static List<ScanMeta> LoadFromIndex()
        {
            var result = new List<ScanMeta>();
            try
            {
                var indexPath = GetIndexPath();
                if (!File.Exists(indexPath))
                    return result;

                var json = File.ReadAllText(indexPath, Encoding.UTF8);
                var entries = JsonSerializer.Deserialize<List<ScanIndexEntry>>(json, _opts) ?? new List<ScanIndexEntry>();
                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.RunId))
                        continue;

                    var combinedPath = !string.IsNullOrWhiteSpace(entry.CombinedJsonPath)
                        ? entry.CombinedJsonPath
                        : GetCombinedJsonPath(entry.RunId);

                    var mapped = new ScanMeta
                    {
                        RunId = entry.RunId,
                        StartTime = entry.StartTime,
                        EndTime = entry.EndTime,
                        MachineName = entry.MachineName,
                        Status = entry.Status,
                        Score = entry.Score,
                        Grade = string.IsNullOrWhiteSpace(entry.Grade) ? "N/A" : entry.Grade,
                        DurationSeconds = entry.DurationSeconds,
                        ErrorSummary = entry.ErrorSummary,
                        StatusReason = entry.StatusReason,
                        CombinedSizeBytes = entry.CombinedSizeBytes,
                        AppVersion = entry.AppVersion,
                        SnapshotPath = entry.SnapshotPath,
                        UnifiedTxtPath = entry.UnifiedTxtPath,
                        CombinedJsonPath = File.Exists(combinedPath) ? combinedPath : null
                    };

                    result.Add(mapped);
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ScanStorage] LoadFromIndex failed: {ex.Message}");
            }

            return result
                .OrderByDescending(m => m.StartTime)
                .ToList();
        }

        private static void UpsertIndex(ScanMeta meta)
        {
            try
            {
                Directory.CreateDirectory(BaseDir);
                var indexPath = GetIndexPath();
                var entries = new List<ScanIndexEntry>();
                if (File.Exists(indexPath))
                {
                    try
                    {
                        var existingJson = File.ReadAllText(indexPath, Encoding.UTF8);
                        entries = JsonSerializer.Deserialize<List<ScanIndexEntry>>(existingJson, _opts) ?? new List<ScanIndexEntry>();
                    }
                    catch (Exception ex)
                    {
                        App.LogMessage($"[ScanStorage][RunId:{meta.RunId}] Index parse warning: {ex.Message}");
                    }
                }

                entries.RemoveAll(e => string.Equals(e.RunId, meta.RunId, StringComparison.OrdinalIgnoreCase));

                var canonicalCombinedPath = GetCombinedJsonPath(meta.RunId);
                var combinedSizeBytes = meta.CombinedSizeBytes;
                if (combinedSizeBytes <= 0 && File.Exists(canonicalCombinedPath))
                {
                    try { combinedSizeBytes = new FileInfo(canonicalCombinedPath).Length; }
                    catch { combinedSizeBytes = 0; }
                }

                entries.Add(new ScanIndexEntry
                {
                    RunId = meta.RunId,
                    StartTime = meta.StartTime,
                    EndTime = meta.EndTime,
                    Status = meta.Status,
                    Score = meta.Score,
                    Grade = string.IsNullOrWhiteSpace(meta.Grade) ? "N/A" : meta.Grade,
                    DurationSeconds = meta.DurationSeconds,
                    ErrorSummary = meta.ErrorSummary,
                    StatusReason = meta.StatusReason,
                    CombinedJsonPath = canonicalCombinedPath,
                    SnapshotPath = string.IsNullOrWhiteSpace(meta.SnapshotPath) ? GetSnapshotPath(meta.RunId) : meta.SnapshotPath,
                    UnifiedTxtPath = string.IsNullOrWhiteSpace(meta.UnifiedTxtPath) ? GetUnifiedReportPath(meta.RunId) : meta.UnifiedTxtPath,
                    CombinedSizeBytes = combinedSizeBytes,
                    MachineName = meta.MachineName ?? string.Empty,
                    AppVersion = meta.AppVersion ?? string.Empty
                });

                var ordered = entries
                    .OrderByDescending(e => e.StartTime)
                    .ThenByDescending(e => e.EndTime ?? DateTime.MinValue)
                    .Take(500)
                    .ToList();

                var json = JsonSerializer.Serialize(ordered, _opts);
                WriteTextAtomic(indexPath, json);
            }
            catch (Exception ex)
            {
                App.LogMessage($"[ScanStorage][RunId:{meta.RunId}] UpsertIndex failed: {ex.Message}");
            }
        }

        private static void WriteTextAtomic(string targetPath, string content)
        {
            var directory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException($"Target directory cannot be resolved for {targetPath}");

            Directory.CreateDirectory(directory);
            var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            try
            {
                if (File.Exists(targetPath))
                    File.Replace(tempPath, targetPath, null, ignoreMetadataErrors: true);
                else
                    File.Move(tempPath, targetPath);
            }
            catch
            {
                try
                {
                    // Fallback for cross-volume or uncommon replace errors.
                    File.Copy(tempPath, targetPath, overwrite: true);
                    File.Delete(tempPath);
                }
                catch
                {
                    try
                    {
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }
                    catch
                    {
                        // No-op: final cleanup best effort.
                    }

                    throw;
                }
            }
        }
    }
}
