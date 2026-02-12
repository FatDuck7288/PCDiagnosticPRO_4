using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Builds HardwareProfile from JSON (scan_powershell + diagnostic_snapshot), DiagnosticSnapshot, and HardwareSensorsResult.
    /// Primary source: diagnostic_snapshot when present and metric.available = true. Sections/snapshot/sensors are fallback.
    /// </summary>
    public static class HardwareProfileBuilder
    {
        /// <summary>Normalize hardware name for matching: trim, collapse spaces, remove (R)/(TM), NVIDIA GeForce prefix, N-Core Processor suffix.</summary>
        public static string NormalizeHardwareName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            var s = name.Trim();
            while (s.IndexOf("  ", StringComparison.Ordinal) >= 0) s = s.Replace("  ", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\(R\)\s*", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\(TM\)\s*", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Remove "NVIDIA GeForce " prefix for matching (e.g. "NVIDIA GeForce RTX 3090" → "RTX 3090")
            if (s.StartsWith("NVIDIA GeForce ", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("NVIDIA GeForce ".Length).Trim();
            // Remove trailing " N-Core Processor" (e.g. "AMD Ryzen 9 5900X 12-Core Processor" → "AMD Ryzen 9 5900X")
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+\d+-Core\s+Processor\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return s.Trim();
        }

        /// <summary>Read numeric value from JSON: either raw number or object with "value" (and optionally "available"; if available=false, returns false).</summary>
        private static bool TryGetMetricDouble(JsonElement el, out double value)
        {
            value = 0;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value)) return true;
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out value))
            {
                if (el.TryGetProperty("available", out var av) && av.ValueKind == JsonValueKind.False) return false;
                return true;
            }
            return false;
        }

        private static bool TryGetMetricInt32(JsonElement el, out int value)
        {
            value = 0;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value)) return true;
            if (TryGetMetricDouble(el, out var d)) { value = (int)d; return true; }
            return false;
        }
        /// <summary>
        /// Build profile from combined root (scan_powershell + optional diagnostic_snapshot in JSON),
        /// optional snapshot object, and optional sensors. Prefer snapshot/sensors when available.
        /// </summary>
        public static HardwareProfile Build(
            JsonElement? combinedRoot,
            DiagnosticSnapshot? snapshot,
            HardwareSensorsResult? sensors)
        {
            // #region agent log
            try
            {
                var entryData = new Dictionary<string, object> { ["combinedRootHasValue"] = combinedRoot.HasValue, ["snapshotNull"] = snapshot == null, ["sensorsNull"] = sensors == null };
                File.AppendAllText(@"d:\Tennis\Os\Produits\PC_Repair\Test-codex-analyze-xaml-binding-exception-details\PCDiagnosticPRO-code\.cursor\debug.log", JsonSerializer.Serialize(new { hypothesisId = "H2", location = "HardwareProfileBuilder.Build", message = "Entry", data = entryData, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            }
            catch { }
            // #endregion
            var profile = new HardwareProfile();

            JsonElement? sections = null;
            JsonElement? diagnosticSnapshotEl = null;
            if (combinedRoot.HasValue)
            {
                var root = combinedRoot.Value;
                if (root.TryGetProperty("scan_powershell", out var ps) && ps.TryGetProperty("sections", out var sec))
                    sections = sec;
                else if (root.TryGetProperty("sections", out var sec2))
                    sections = sec2;
                if (root.TryGetProperty("diagnostic_snapshot", out var snap) && snap.ValueKind == JsonValueKind.Object)
                    diagnosticSnapshotEl = snap;
            }

            try { ExtractCpu(profile, sections, snapshot, diagnosticSnapshotEl); }
            catch (Exception exCpu) { LogExtractThrow("ExtractCpu", exCpu); /* continue for partial data */ }
            try { ExtractGpu(profile, sections, snapshot, sensors, diagnosticSnapshotEl); }
            catch (Exception exGpu) { LogExtractThrow("ExtractGpu", exGpu); /* continue */ }
            try { ExtractRam(profile, sections, snapshot, diagnosticSnapshotEl); }
            catch (Exception exRam) { LogExtractThrow("ExtractRam", exRam); /* continue */ }
            try { ExtractStorage(profile, sections); }
            catch (Exception exStor) { LogExtractThrow("ExtractStorage", exStor); /* continue */ }

            // Log normalized names before dataset lookup
            var cpuNorm = NormalizeHardwareName(profile.CpuModel);
            var gpuNorm = NormalizeHardwareName(profile.GpuModel);
            LogNormalizedNames(cpuNorm, gpuNorm);

            var (cpuTier, cpuMatched) = PerformanceTierTable.ResolveCpuTier(profile.CpuModel, profile.CpuCores, profile.CpuThreads);
            var (gpuTier, gpuMatched) = PerformanceTierTable.ResolveGpuTier(profile.GpuModel, profile.GpuVramMb);
            profile.CpuTier = cpuTier;
            profile.GpuTier = gpuTier;
            profile.CpuNameMatched = cpuMatched;
            profile.GpuNameMatched = gpuMatched;
            if (!cpuMatched && !string.IsNullOrEmpty(profile.CpuModel))
                LogUnmatched("CPU", profile.CpuModel, cpuTier);
            if (!gpuMatched && !string.IsNullOrEmpty(profile.GpuModel))
                LogUnmatched("GPU", profile.GpuModel, gpuTier);
            profile.RamTier = PerformanceTierTable.ResolveRamTier(profile.RamGb);
            profile.StorageTier = PerformanceTierTable.ResolveStorageTier(profile.StorageKind);

            // Sanity guard: if GPU contains "3090" OR VRAM >= 20GB OR CPU cores >= 12 → system cannot be Entry
            ApplyTierSanityFloor(profile);

            // #region agent log
            try
            {
                var outData = new Dictionary<string, object> { ["CpuModel"] = profile.CpuModel ?? "(null)", ["GpuModel"] = profile.GpuModel ?? "(null)", ["GpuVramMb"] = profile.GpuVramMb, ["RamGb"] = profile.RamGb, ["StorageKind"] = profile.StorageKind ?? "(null)" };
                File.AppendAllText(@"d:\Tennis\Os\Produits\PC_Repair\Test-codex-analyze-xaml-binding-exception-details\PCDiagnosticPRO-code\.cursor\debug.log", JsonSerializer.Serialize(new { hypothesisId = "H2", location = "HardwareProfileBuilder.Build", message = "Profile after extract", data = outData, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            }
            catch { }
            // #endregion
            return profile;
        }

        private static void ExtractCpu(HardwareProfile profile, JsonElement? sections, DiagnosticSnapshot? snapshot, JsonElement? diagnosticSnapshotEl)
        {
            // Primary: diagnostic_snapshot (machine.cpuName, metrics.cpu)
            if (diagnosticSnapshotEl.HasValue)
            {
                var snap = diagnosticSnapshotEl.Value;
                if (snap.TryGetProperty("machine", out var machine) && machine.TryGetProperty("cpuName", out var cn) && cn.ValueKind == JsonValueKind.String)
                    profile.CpuModel = cn.GetString();
                if (snap.TryGetProperty("metrics", out var metrics) && metrics.TryGetProperty("cpu", out var cpuM) && cpuM.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kv in cpuM.EnumerateObject())
                    {
                        if (kv.Name.Equals("model", StringComparison.OrdinalIgnoreCase) && kv.Value.ValueKind == JsonValueKind.String) profile.CpuModel = kv.Value.GetString();
                        if (kv.Name.Equals("cores", StringComparison.OrdinalIgnoreCase) && TryGetMetricInt32(kv.Value, out var c)) profile.CpuCores = c;
                        if (kv.Name.Equals("threads", StringComparison.OrdinalIgnoreCase) && TryGetMetricInt32(kv.Value, out var t)) profile.CpuThreads = t;
                    }
                }
            }
            if (snapshot?.Machine != null && profile.CpuModel == null)
                profile.CpuModel = snapshot.Machine.CpuName;
            if (snapshot?.Metrics != null && snapshot.Metrics.TryGetValue("cpu", out var cpuMetrics))
            {
                if (profile.CpuModel == null && cpuMetrics.TryGetValue("model", out var m) && m.Available && m.Value != null)
                    profile.CpuModel = m.Value.ToString();
                if (profile.CpuCores == 0 && cpuMetrics.TryGetValue("cores", out var c) && c.Available && c.Value is double cd)
                    profile.CpuCores = (int)cd;
                if (profile.CpuThreads == 0 && cpuMetrics.TryGetValue("threads", out var t) && t.Available && t.Value is double td)
                    profile.CpuThreads = (int)td;
            }
            // Fallback: sections.CPU.data.cpus[0] (sections.CPU.data.cpus.name per spec)
            if (sections.HasValue && sections.Value.TryGetProperty("CPU", out var cpuSec) && cpuSec.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("cpus", out var cpus) && cpus.ValueKind == JsonValueKind.Array)
                {
                    var first = cpus.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Object)
                    {
                        if (profile.CpuModel == null && first.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                            profile.CpuModel = n.GetString();
                        if (profile.CpuCores == 0 && first.TryGetProperty("coreCount", out var cc) && cc.TryGetInt32(out var ccVal)) profile.CpuCores = ccVal;
                        if (profile.CpuCores == 0 && first.TryGetProperty("cores", out var co) && co.TryGetInt32(out var coVal)) profile.CpuCores = coVal;
                        if (profile.CpuThreads == 0 && first.TryGetProperty("threadCount", out var tc) && tc.TryGetInt32(out var tcVal)) profile.CpuThreads = tcVal;
                        if (profile.CpuThreads == 0 && first.TryGetProperty("threads", out var th) && th.TryGetInt32(out var thVal)) profile.CpuThreads = thVal;
                        if (first.TryGetProperty("baseClockGHz", out var bcg) && bcg.TryGetDouble(out var bcgVal)) profile.CpuBaseGhz = bcgVal;
                        else if (profile.CpuBaseGhz <= 0 && first.TryGetProperty("baseClock", out var bc) && bc.TryGetDouble(out var bcVal)) profile.CpuBaseGhz = bcVal;
                        if (first.TryGetProperty("maxClockGHz", out var mcg) && mcg.TryGetDouble(out var mcgVal)) profile.CpuBoostGhz = mcgVal;
                        else if (profile.CpuBoostGhz <= 0 && first.TryGetProperty("maxClock", out var mc) && mc.TryGetDouble(out var mcVal)) profile.CpuBoostGhz = mcVal;
                    }
                }
            }
            if (profile.CpuModel != null) profile.CpuModel = profile.CpuModel.Trim();
            if (profile.CpuThreads == 0 && profile.CpuCores > 0) profile.CpuThreads = profile.CpuCores * 2;
            if (profile.CpuBoostGhz <= 0 && profile.CpuBaseGhz > 0) profile.CpuBoostGhz = profile.CpuBaseGhz;
        }

        private static void ExtractGpu(HardwareProfile profile, JsonElement? sections, DiagnosticSnapshot? snapshot, HardwareSensorsResult? sensors, JsonElement? diagnosticSnapshotEl)
        {
            // Primary: diagnostic_snapshot.metrics.gpu (name, model, vramTotalMB). If available=true and value>0, never show Unknown.
            if (diagnosticSnapshotEl.HasValue && diagnosticSnapshotEl.Value.TryGetProperty("metrics", out var metrics) && metrics.TryGetProperty("gpu", out var gpuM) && gpuM.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in gpuM.EnumerateObject())
                {
                    if ((kv.Name.Equals("model", StringComparison.OrdinalIgnoreCase) || kv.Name.Equals("name", StringComparison.OrdinalIgnoreCase)) && kv.Value.ValueKind == JsonValueKind.String)
                        profile.GpuModel = kv.Value.GetString();
                    if (kv.Name.Equals("vramTotalMB", StringComparison.OrdinalIgnoreCase) && TryGetMetricDouble(kv.Value, out var v) && v > 0)
                        profile.GpuVramMb = v;
                }
            }
            if (snapshot?.Metrics != null && snapshot.Metrics.TryGetValue("gpu", out var gpuMetrics))
            {
                if (profile.GpuModel == null && gpuMetrics.TryGetValue("model", out var gm) && gm.Available && gm.Value != null)
                    profile.GpuModel = gm.Value.ToString();
                if (profile.GpuVramMb <= 0 && gpuMetrics.TryGetValue("vramTotalMB", out var v) && v.Available && v.Value is double vd)
                    profile.GpuVramMb = vd;
            }
            if (sensors?.Gpu != null)
            {
                if (profile.GpuModel == null && sensors.Gpu.Name.Available && !string.IsNullOrEmpty(sensors.Gpu.Name.Value))
                    profile.GpuModel = sensors.Gpu.Name.Value;
                if (profile.GpuVramMb <= 0 && sensors.Gpu.VramTotalMB.Available && sensors.Gpu.VramTotalMB.Value > 0)
                    profile.GpuVramMb = sensors.Gpu.VramTotalMB.Value;
            }
            // Fallback: sections.GPU or sections.HARDWARE.GPU — do not prefer over diagnostic_snapshot
            TryGpuFromSections(sections, profile, "GPU");
            if ((profile.GpuModel == null || profile.GpuVramMb <= 0) && sections.HasValue)
                TryGpuFromSections(sections, profile, "HARDWARE");
            if (profile.GpuModel != null) profile.GpuModel = profile.GpuModel.Trim();
        }

        private static void TryGpuFromSections(JsonElement? sections, HardwareProfile profile, string sectionKey)
        {
            if (!sections.HasValue || !sections.Value.TryGetProperty(sectionKey, out var sec)) return;
            JsonElement? dataEl = null;
            if (sec.TryGetProperty("data", out var dataProp)) dataEl = dataProp;
            else if (sec.TryGetProperty("GPU", out var gpuChild) && gpuChild.TryGetProperty("data", out var gpuData)) dataEl = gpuData;
            if (!dataEl.HasValue) return;
            var data = dataEl.Value;
            if (profile.GpuModel == null && (data.TryGetProperty("gpuList", out var list) || data.TryGetProperty("gpus", out list)) && list.ValueKind == JsonValueKind.Array)
            {
                var first = list.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    profile.GpuModel = n.GetString();
            }
            if (profile.GpuVramMb <= 0 && (data.TryGetProperty("gpuList", out var list2) || data.TryGetProperty("gpus", out list2)) && list2.ValueKind == JsonValueKind.Array)
            {
                var first = list2.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("vramTotalMB", out var v) && v.TryGetDouble(out var vd))
                    profile.GpuVramMb = vd;
            }
        }

        private static void TryRamFromSections(JsonElement? sections, HardwareProfile profile, string sectionKey)
        {
            if (!sections.HasValue || profile.RamGb > 0) return;
            if (!sections.Value.TryGetProperty(sectionKey, out var sec) || !sec.TryGetProperty("data", out var data)) return;
            if (data.TryGetProperty("totalGB", out var tg) && tg.TryGetDouble(out var val))
                profile.RamGb = val;
        }

        private static void ExtractRam(HardwareProfile profile, JsonElement? sections, DiagnosticSnapshot? snapshot, JsonElement? diagnosticSnapshotEl)
        {
            // Primary: diagnostic_snapshot.machine.totalRamGB
            if (profile.RamGb <= 0 && diagnosticSnapshotEl.HasValue && diagnosticSnapshotEl.Value.TryGetProperty("machine", out var machine) && machine.TryGetProperty("totalRamGB", out var tr))
            {
                if (tr.TryGetDouble(out var rg)) profile.RamGb = rg;
                else if (TryGetMetricDouble(tr, out var rgm)) profile.RamGb = rgm;
            }
            if (profile.RamGb <= 0 && snapshot?.Machine?.TotalRamGB != null && snapshot.Machine.TotalRamGB > 0)
                profile.RamGb = snapshot.Machine.TotalRamGB.Value;
            // Fallback: sections.Memory or sections.MEMOIRE (MEMOIRE RAM)
            TryRamFromSections(sections, profile, "Memory");
            if (profile.RamGb <= 0 && sections.HasValue) TryRamFromSections(sections, profile, "MEMOIRE");
            if (profile.RamGb <= 0 && sections.HasValue && sections.Value.TryGetProperty("MEMOIRE RAM", out var memRam) && memRam.TryGetProperty("data", out var memRamData) && memRamData.TryGetProperty("totalGB", out var tr2) && tr2.TryGetDouble(out var rg2))
                profile.RamGb = rg2;

            if (sections.HasValue && sections.Value.TryGetProperty("Memory", out var memSec) && memSec.TryGetProperty("data", out var data))
            {
                if (profile.RamGb <= 0 && data.TryGetProperty("totalGB", out var tg) && tg.TryGetDouble(out var tgVal))
                    profile.RamGb = tgVal;
                if (data.TryGetProperty("modules", out var mods) && mods.ValueKind == JsonValueKind.Array)
                {
                    var arr = mods.EnumerateArray().ToList();
                    profile.DualChannel = arr.Count >= 2;
                    int maxSpeed = 0;
                    foreach (var m in arr)
                    {
                        if (m.TryGetProperty("speedMHz", out var sp) && sp.TryGetInt32(out var speed))
                            maxSpeed = Math.Max(maxSpeed, speed);
                        if (m.TryGetProperty("Speed", out var sp2) && sp2.TryGetInt32(out var speed2))
                            maxSpeed = Math.Max(maxSpeed, speed2);
                    }
                    if (maxSpeed > 0) profile.RamSpeedMhz = maxSpeed;
                }
            }

            if (profile.RamGb <= 0 && snapshot?.Metrics != null && snapshot.Metrics.TryGetValue("memory", out var memM))
            {
                if (memM.TryGetValue("totalGB", out var tg) && tg.Available && tg.Value is double tgd)
                    profile.RamGb = tgd;
            }
        }

        private static void ExtractStorage(HardwareProfile profile, JsonElement? sections)
        {
            profile.StorageKind = PerformanceTierTable.StorageHdd;

            if (!sections.HasValue) return;
            if (!sections.Value.TryGetProperty("Storage", out var storSec) || !storSec.TryGetProperty("data", out var data))
                return;
            if (!data.TryGetProperty("physicalDisks", out var disks) && !data.TryGetProperty("disks", out disks))
                return;
            if (disks.ValueKind != JsonValueKind.Array) return;

            bool hasNvme = false;
            bool hasSataSsd = false;
            foreach (var disk in disks.EnumerateArray())
            {
                var type = GetString(disk, "type") ?? GetString(disk, "mediaType") ?? "";
                var iface = GetString(disk, "interface") ?? "";
                var model = GetString(disk, "model") ?? "";
                if (type.Equals("NVMe", StringComparison.OrdinalIgnoreCase) || iface.Equals("NVMe", StringComparison.OrdinalIgnoreCase) ||
                    model.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
                {
                    hasNvme = true;
                    break;
                }
                if (type.Equals("SSD", StringComparison.OrdinalIgnoreCase) || model.Contains("SSD", StringComparison.OrdinalIgnoreCase))
                    hasSataSsd = true;
            }

            if (hasNvme) profile.StorageKind = PerformanceTierTable.StorageNvme;
            else if (hasSataSsd) profile.StorageKind = PerformanceTierTable.StorageSataSsd;
        }

        /// <summary>Sanity guard: if GPU name contains "3090" OR VRAM >= 20GB OR CPU cores >= 12 → system category cannot be Entry.</summary>
        private static void ApplyTierSanityFloor(HardwareProfile profile)
        {
            const double TwentyGbMb = 20 * 1024;
            var gpuNorm = NormalizeHardwareName(profile.GpuModel);
            bool highEnd = gpuNorm.Contains("3090", StringComparison.OrdinalIgnoreCase)
                || profile.GpuVramMb >= TwentyGbMb
                || profile.CpuCores >= 12;
            if (!highEnd) return;
            bool correctedCpu = profile.CpuTier == PerformanceTierTable.TierEntry;
            bool correctedGpu = profile.GpuTier == PerformanceTierTable.TierEntry;
            if (correctedCpu) profile.CpuTier = PerformanceTierTable.TierUpperMid;
            if (correctedGpu) profile.GpuTier = PerformanceTierTable.TierUpperMid;
            if (correctedCpu || correctedGpu)
                LogTierSanityCorrected(profile, correctedCpu, correctedGpu);
        }

        private static void LogNormalizedNames(string cpuNormalized, string gpuNormalized)
        {
            try
            {
                var logLine = JsonSerializer.Serialize(new { message = "NormalizedNamesBeforeLookup", cpuNormalized, gpuNormalized, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n";
                File.AppendAllText(@"d:\Tennis\Os\Produits\PC_Repair\Test-codex-analyze-xaml-binding-exception-details\PCDiagnosticPRO-code\.cursor\debug.log", logLine);
            }
            catch { }
        }

        private static void LogTierSanityCorrected(HardwareProfile profile, bool cpuCorrected, bool gpuCorrected)
        {
            try
            {
                var logLine = JsonSerializer.Serialize(new { message = "TierSanityFloor", reason = "Dataset returned Entry for known high-end config; applied heuristic override.", cpuCorrected, gpuCorrected, profile = new { profile.CpuModel, profile.GpuModel, profile.CpuCores, profile.GpuVramMb }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n";
                File.AppendAllText(@"d:\Tennis\Os\Produits\PC_Repair\Test-codex-analyze-xaml-binding-exception-details\PCDiagnosticPRO-code\.cursor\debug.log", logLine);
            }
            catch { }
        }

        private static void LogUnmatched(string component, string name, string resolvedTier)
        {
            try
            {
                var logLine = JsonSerializer.Serialize(new { message = "Unmatched", component, name, resolvedTier, reason = "No dataset name pattern matched; tier from heuristic.", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n";
                File.AppendAllText(@"d:\Tennis\Os\Produits\PC_Repair\Test-codex-analyze-xaml-binding-exception-details\PCDiagnosticPRO-code\.cursor\debug.log", logLine);
            }
            catch { }
        }

        private static void LogExtractThrow(string extractName, Exception ex)
        {
            try
            {
                var logLine = JsonSerializer.Serialize(new { hypothesisId = "H2", location = "HardwareProfileBuilder.Build", message = "Extract threw", data = new Dictionary<string, object> { ["extract"] = extractName, ["exception"] = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n";
                File.AppendAllText(@"d:\Tennis\Os\Produits\PC_Repair\Test-codex-analyze-xaml-binding-exception-details\PCDiagnosticPRO-code\.cursor\debug.log", logLine);
            }
            catch { }
        }

        private static string? GetString(JsonElement el, string name)
        {
            if (el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String) return p.GetString();
            var lower = name.ToLowerInvariant();
            foreach (var prop in el.EnumerateObject())
                if (prop.Name.Equals(lower, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();
            return null;
        }
    }
}
