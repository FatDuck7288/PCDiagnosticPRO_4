using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Builds HardwareProfile from JSON (scan_powershell + diagnostic_snapshot), DiagnosticSnapshot, and HardwareSensorsResult.
    /// Primary source: diagnostic_snapshot when present and metric.available = true. Sections/snapshot/sensors are fallback.
    /// 
    /// MULTI-SOURCE PRIORITY (per field):
    ///   1. diagnostic_snapshot JSON (machine.cpuName, metrics.cpu.model/cores/threads)
    ///   2. DiagnosticSnapshot C# object (snapshot.Machine.CpuName, snapshot.Metrics["cpu"])
    ///   3. HardwareSensorsResult (sensors.Gpu for GPU info)
    ///   4. scan_powershell.sections (CPU/GPU/Memory/Storage sections)
    ///   5. Heuristic derivation (cores from model name suffix, threads = cores * 2)
    /// </summary>
    public static class HardwareProfileBuilder
    {
        /// <summary>Normalize hardware name for matching: trim, collapse spaces, remove (R)/(TM), NVIDIA GeForce prefix, N-Core Processor suffix.</summary>
        public static string NormalizeHardwareName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            var s = name.Trim();
            while (s.IndexOf("  ", StringComparison.Ordinal) >= 0) s = s.Replace("  ", " ");
            s = Regex.Replace(s, @"\s*\(R\)\s*", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s*\(TM\)\s*", " ", RegexOptions.IgnoreCase);
            // Remove "NVIDIA GeForce " prefix for matching (e.g. "NVIDIA GeForce RTX 3090" → "RTX 3090")
            if (s.StartsWith("NVIDIA GeForce ", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("NVIDIA GeForce ".Length).Trim();
            // Remove trailing " N-Core Processor" (e.g. "AMD Ryzen 9 5900X 12-Core Processor" → "AMD Ryzen 9 5900X")
            s = Regex.Replace(s, @"\s+\d+-Core\s+Processor\s*$", "", RegexOptions.IgnoreCase);
            return s.Trim();
        }

        /// <summary>Try to extract core count from CPU model name (e.g., "AMD Ryzen 9 5900X 12-Core Processor" → 12).</summary>
        private static int ExtractCoresFromModelName(string? modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return 0;
            var match = Regex.Match(modelName, @"(\d+)-Core", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int cores))
                return cores;
            return 0;
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
        /// When dataset is provided, tier resolution uses dataset patterns/thresholds.
        /// </summary>
        public static HardwareProfile Build(
            JsonElement? combinedRoot,
            DiagnosticSnapshot? snapshot,
            HardwareSensorsResult? sensors,
            PerformanceDataset? dataset = null)
        {
            var profile = new HardwareProfile();

            JsonElement? sections = null;
            JsonElement? diagnosticSnapshotEl = null;
            if (combinedRoot.HasValue)
            {
                var root = combinedRoot.Value;
                // Try multiple paths for sections
                if (root.TryGetProperty("scan_powershell", out var ps))
                {
                    if (ps.TryGetProperty("sections", out var sec) && sec.ValueKind == JsonValueKind.Object)
                        sections = sec;
                    else if (ps.ValueKind == JsonValueKind.Object)
                        sections = ps; // scan_powershell itself might contain sections directly
                }
                // Validate sections: reject if it looks like speed-test or non-PS data
                if (sections.HasValue && sections.Value.ValueKind == JsonValueKind.Object)
                {
                    bool looksLikePsSections = false;
                    string[] expectedKeys = { "CPU", "GPU", "Memory", "MEMOIRE", "Processor", "CPUInfo", "MemoryInfo", "Storage", "Disk", "Network", "Audio", "Security", "DevicesDrivers" };
                    foreach (var prop in sections.Value.EnumerateObject())
                    {
                        foreach (var ek in expectedKeys)
                        {
                            if (string.Equals(prop.Name, ek, StringComparison.OrdinalIgnoreCase))
                            { looksLikePsSections = true; break; }
                        }
                        if (looksLikePsSections) break;
                    }
                    if (!looksLikePsSections)
                    {
                        App.LogMessage($"[HardwareProfileBuilder] Sections rejected (no PS keys found), nullifying");
                        sections = null;
                    }
                }

                // Fallback: try root.sections AFTER validation (in case scan_powershell was speed-test data that got rejected)
                if (!sections.HasValue && root.TryGetProperty("sections", out var sec2) && sec2.ValueKind == JsonValueKind.Object)
                {
                    // Quick validate root.sections too
                    bool rootSectionsValid = false;
                    string[] expectedKeysFallback = { "CPU", "GPU", "Memory", "MEMOIRE", "Storage", "Network", "Security" };
                    foreach (var prop in sec2.EnumerateObject())
                    {
                        foreach (var ek in expectedKeysFallback)
                        {
                            if (string.Equals(prop.Name, ek, StringComparison.OrdinalIgnoreCase))
                            { rootSectionsValid = true; break; }
                        }
                        if (rootSectionsValid) break;
                    }
                    if (rootSectionsValid)
                        sections = sec2;
                    else
                        App.LogMessage($"[HardwareProfileBuilder] root.sections also rejected (no PS keys found)");
                }

                // diagnostic_snapshot in JSON
                if (root.TryGetProperty("diagnostic_snapshot", out var snap) && snap.ValueKind == JsonValueKind.Object)
                    diagnosticSnapshotEl = snap;
            }

            // Extract each component with detailed error logging
            try { ExtractCpu(profile, sections, snapshot, diagnosticSnapshotEl, combinedRoot); }
            catch (Exception ex) { App.LogMessage($"[HardwareProfileBuilder] ExtractCpu failed: {ex.Message}"); }

            try { ExtractGpu(profile, sections, snapshot, sensors, diagnosticSnapshotEl); }
            catch (Exception ex) { App.LogMessage($"[HardwareProfileBuilder] ExtractGpu failed: {ex.Message}"); }

            try { ExtractRam(profile, sections, snapshot, diagnosticSnapshotEl, sensors); }
            catch (Exception ex) { App.LogMessage($"[HardwareProfileBuilder] ExtractRam failed: {ex.Message}"); }

            try { ExtractStorage(profile, sections, diagnosticSnapshotEl); }
            catch (Exception ex) { App.LogMessage($"[HardwareProfileBuilder] ExtractStorage failed: {ex.Message}"); }

            // Resolve tiers
            var (cpuTier, cpuMatched) = PerformanceTierTable.ResolveCpuTier(profile.CpuModel, profile.CpuCores, profile.CpuThreads, dataset);
            var (gpuTier, gpuMatched) = PerformanceTierTable.ResolveGpuTier(profile.GpuModel, profile.GpuVramMb, dataset);
            profile.CpuTier = cpuTier;
            profile.GpuTier = gpuTier;
            profile.CpuNameMatched = cpuMatched;
            profile.GpuNameMatched = gpuMatched;
            profile.RamTier = PerformanceTierTable.ResolveRamTier(profile.RamGb, dataset);
            profile.StorageTier = PerformanceTierTable.ResolveStorageTier(profile.StorageKind, dataset);

            // Sanity guard: if GPU contains "3090" OR VRAM >= 20GB OR CPU cores >= 12 → system cannot be Entry
            ApplyTierSanityFloor(profile);

            // === COMPREHENSIVE LOGGING for debugging ===
            LogProfileForDebugging(profile);

            return profile;
        }

        /// <summary>
        /// Logs the final hardware profile values for debugging low scores.
        /// Single log line with all relevant values for easy inspection.
        /// </summary>
        private static void LogProfileForDebugging(HardwareProfile profile)
        {
            try
            {
                var logData = new Dictionary<string, object>
                {
                    ["message"] = "HardwareProfile built",
                    ["resolvedCpu"] = profile.CpuModel ?? "(null)",
                    ["resolvedRamGb"] = profile.RamGb,
                    ["resolvedVramMb"] = profile.GpuVramMb,
                    ["resolvedStorage"] = profile.StorageKind ?? "(null)",
                    ["CpuModel"] = profile.CpuModel ?? "(null)",
                    ["CpuCores"] = profile.CpuCores,
                    ["CpuThreads"] = profile.CpuThreads,
                    ["CpuBaseGhz"] = profile.CpuBaseGhz,
                    ["CpuBoostGhz"] = profile.CpuBoostGhz,
                    ["CpuTier"] = profile.CpuTier,
                    ["CpuNameMatched"] = profile.CpuNameMatched,
                    ["GpuModel"] = profile.GpuModel ?? "(null)",
                    ["GpuVramMb"] = profile.GpuVramMb,
                    ["GpuTier"] = profile.GpuTier,
                    ["GpuNameMatched"] = profile.GpuNameMatched,
                    ["RamGb"] = profile.RamGb,
                    ["RamSpeedMhz"] = profile.RamSpeedMhz,
                    ["DualChannel"] = profile.DualChannel,
                    ["RamTier"] = profile.RamTier,
                    ["StorageKind"] = profile.StorageKind ?? "(null)",
                    ["StorageTier"] = profile.StorageTier,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                App.LogMessage($"[HardwareProfileBuilder] {JsonSerializer.Serialize(logData)}");
            }
            catch { /* Ignore logging errors */ }
        }

        private static void ExtractCpu(HardwareProfile profile, JsonElement? sections, DiagnosticSnapshot? snapshot, JsonElement? diagnosticSnapshotEl, JsonElement? combinedRoot = null)
        {
            // === SOURCE 1: diagnostic_snapshot JSON (machine.cpuName, metrics.cpu) ===
            if (diagnosticSnapshotEl.HasValue)
            {
                try
                {
                    var snap = diagnosticSnapshotEl.Value;
                    if (snap.ValueKind == JsonValueKind.Object)
                    {
                        // machine.cpuName (and variants)
                        if (snap.TryGetProperty("machine", out var machine) && machine.ValueKind == JsonValueKind.Object)
                        {
                            if (TryGetStringPropertyCaseInsensitive(machine, out var cpuName, "cpuName", "CpuName", "processorName", "ProcessorName"))
                                if (string.IsNullOrEmpty(profile.CpuModel)) profile.CpuModel = cpuName;
                        }
                        // metrics.cpu: accept Object OR Array (take first element if array)
                        if (snap.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object &&
                            TryGetPropertyCaseInsensitive(metrics, out var cpuM, "cpu"))
                        {
                            JsonElement cpuObj = cpuM;
                            if (cpuM.ValueKind == JsonValueKind.Array)
                            {
                                var first = cpuM.EnumerateArray().FirstOrDefault();
                                if (first.ValueKind == JsonValueKind.Object) cpuObj = first;
                            }
                            if (cpuObj.ValueKind == JsonValueKind.Object)
                            {
                                // Use broad key matching (name, model, processorname, cpuname, cores, corecount, etc.)
                                ExtractCpuFromJsonObject(cpuObj, profile);
                                // Some snapshots nest CPU metadata in metrics.cpu.data
                                if (TryGetPropertyCaseInsensitive(cpuObj, out var nestedData, "data") && nestedData.ValueKind == JsonValueKind.Object)
                                    ExtractCpuFromJsonObject(nestedData, profile);
                                // Also try metrics.cpu.value as a nested object
                                if (TryGetPropertyCaseInsensitive(cpuObj, out var nestedValue, "value") && nestedValue.ValueKind == JsonValueKind.Object)
                                    ExtractCpuFromJsonObject(nestedValue, profile);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[HardwareProfileBuilder.ExtractCpu] Source 1 (diagnostic_snapshot JSON) error: {ex.Message}");
                }
            }

            // === SOURCE 2: DiagnosticSnapshot C# object ===
            if (snapshot?.Machine != null && string.IsNullOrEmpty(profile.CpuModel))
                profile.CpuModel = snapshot.Machine.CpuName;

            if (snapshot?.Metrics != null && snapshot.Metrics.TryGetValue("cpu", out var cpuMetrics))
            {
                if (string.IsNullOrEmpty(profile.CpuModel) && cpuMetrics.TryGetValue("model", out var m) && m.Available && m.Value != null)
                    profile.CpuModel = m.Value.ToString();
                if (profile.CpuCores == 0 && cpuMetrics.TryGetValue("cores", out var c) && c.Available && c.Value is double cd)
                    profile.CpuCores = (int)cd;
                if (profile.CpuThreads == 0 && cpuMetrics.TryGetValue("threads", out var t) && t.Available && t.Value is double td)
                    profile.CpuThreads = (int)td;
            }

            // === SOURCE 3: sections.CPU.data.cpus[0] (PowerShell scan) ===
            if (sections.HasValue)
            {
                try
                {
                    TryCpuFromSections(sections.Value, profile);
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[HardwareProfileBuilder.ExtractCpu] Source 3 (sections) error: {ex.Message}");
                }
            }

            // === SOURCE 4: cpuList at root level (alternative JSON structure) ===
            if (string.IsNullOrEmpty(profile.CpuModel) || profile.CpuCores == 0)
            {
                try
                {
                    // Some JSON structures have cpuList directly at the root (like CpuProfileAnalyzer expects)
                    if (diagnosticSnapshotEl.HasValue)
                    {
                        TryCpuFromCpuList(diagnosticSnapshotEl.Value, profile);
                    }
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[HardwareProfileBuilder.ExtractCpu] Source 4 (cpuList) error: {ex.Message}");
                }
            }

            // === SOURCE 5: Direct path fallback (scan_powershell.sections.CPU.data.cpus[0] or sections.CPU.data.cpus[0]) ===
            if ((string.IsNullOrEmpty(profile.CpuModel) || profile.CpuCores == 0) && combinedRoot.HasValue)
            {
                try { TryCpuFromDirectPath(combinedRoot, profile); }
                catch (Exception ex) { App.LogMessage($"[HardwareProfileBuilder.ExtractCpu] Source 5 (direct path) error: {ex.Message}"); }
            }

            // === FALLBACKS: derive missing values ===
            if (profile.CpuModel != null) profile.CpuModel = profile.CpuModel.Trim();

            // Derive cores from model name if still missing (e.g., "AMD Ryzen 9 5900X 12-Core Processor" → 12)
            if (profile.CpuCores == 0 && !string.IsNullOrEmpty(profile.CpuModel))
            {
                int derivedCores = ExtractCoresFromModelName(profile.CpuModel);
                if (derivedCores > 0)
                {
                    profile.CpuCores = derivedCores;
                    App.LogMessage($"[HardwareProfileBuilder] Derived CpuCores={derivedCores} from model name '{profile.CpuModel}'");
                }
            }

            // Threads = Cores * 2 if missing (most modern CPUs support SMT/HyperThreading)
            if (profile.CpuThreads == 0 && profile.CpuCores > 0)
            {
                profile.CpuThreads = profile.CpuCores * 2;
            }

            // Boost GHz fallback
            if (profile.CpuBoostGhz <= 0 && profile.CpuBaseGhz > 0)
                profile.CpuBoostGhz = profile.CpuBaseGhz;
        }

        /// <summary>Try to extract CPU info from sections.CPU.data.cpus[] or similar structures.</summary>
        private static void TryCpuFromSections(JsonElement sections, HardwareProfile profile)
        {
            if (sections.ValueKind != JsonValueKind.Object)
            {
                App.LogMessage($"[HardwareProfileBuilder.TryCpuFromSections] SKIP: sections.ValueKind={sections.ValueKind}");
                return;
            }

            // Log all available section keys
            var sectionKeys = new List<string>();
            foreach (var p in sections.EnumerateObject()) sectionKeys.Add(p.Name);
            App.LogMessage($"[HardwareProfileBuilder.TryCpuFromSections] Available sections: [{string.Join(", ", sectionKeys)}]");

            // Section key may be "CPU", "Cpu", "cpu", etc. — use case-insensitive lookup
            string[] sectionNames = { "CPU", "CPUInfo", "PROCESSEUR", "Processor" };
            foreach (var sectionName in sectionNames)
            {
                if (!TryGetPropertyCaseInsensitive(sections, out var cpuSec, sectionName)) continue;
                App.LogMessage($"[HardwareProfileBuilder.TryCpuFromSections] Found section '{sectionName}', ValueKind={cpuSec.ValueKind}");

                // Handle direct data or wrapped in "data" property
                JsonElement data = cpuSec;
                if (cpuSec.ValueKind == JsonValueKind.Object && TryGetPropertyCaseInsensitive(cpuSec, out var dataEl, "data"))
                {
                    data = dataEl;
                    App.LogMessage($"[HardwareProfileBuilder.TryCpuFromSections] Unwrapped 'data' property, ValueKind={data.ValueKind}");
                }

                if (data.ValueKind != JsonValueKind.Object)
                {
                    App.LogMessage($"[HardwareProfileBuilder.TryCpuFromSections] SKIP: data.ValueKind={data.ValueKind}, expected Object");
                    continue;
                }

                // Log data properties
                var dataKeys = new List<string>();
                foreach (var p in data.EnumerateObject()) dataKeys.Add($"{p.Name}:{p.Value.ValueKind}");
                App.LogMessage($"[HardwareProfileBuilder.TryCpuFromSections] Data properties: [{string.Join(", ", dataKeys)}]");

                // Try cpus array OR object (PowerShell serializes single-element collections as Object)
                string[] cpuArrayNames = { "cpus", "Cpus", "processors", "Processors", "cpuList", "CpuList" };
                foreach (var arrName in cpuArrayNames)
                {
                    if (!TryGetPropertyCaseInsensitive(data, out var cpuArray, arrName)) continue;

                    JsonElement first = default;
                    if (cpuArray.ValueKind == JsonValueKind.Array)
                    {
                        var len = cpuArray.GetArrayLength();
                        App.LogMessage($"[HardwareProfileBuilder.TryCpuFromSections] Found array '{arrName}' with {len} elements");
                        if (len == 0) { App.LogMessage($"[HardwareProfileBuilder.TryCpuFromSections] SKIP: array is empty"); continue; }
                        foreach (var el in cpuArray.EnumerateArray()) { first = el; break; }
                    }
                    else if (cpuArray.ValueKind == JsonValueKind.Object)
                    {
                        // PowerShell serializes single-element collections as Object (same as ComprehensiveEvidenceExtractor.GetFirstItemFromArrayOrObject)
                        App.LogMessage($"[HardwareProfileBuilder.TryCpuFromSections] Found '{arrName}' as single Object (PS single-element collection)");
                        first = cpuArray;
                    }
                    else
                    {
                        App.LogMessage($"[HardwareProfileBuilder.TryCpuFromSections] SKIP: '{arrName}' has ValueKind={cpuArray.ValueKind}");
                        continue;
                    }

                    if (first.ValueKind != JsonValueKind.Object)
                    {
                        App.LogMessage($"[HardwareProfileBuilder.TryCpuFromSections] SKIP: first element is not an Object (ValueKind={first.ValueKind})");
                        continue;
                    }

                    ExtractCpuFromJsonObject(first, profile);
                    if (!string.IsNullOrEmpty(profile.CpuModel))
                    {
                        App.LogMessage($"[HardwareProfileBuilder.TryCpuFromSections] SUCCESS: Found CPU from array '{arrName}'");
                        return; // Found valid data
                    }
                }

                // Try direct properties (flat structure)
                App.LogMessage($"[HardwareProfileBuilder.TryCpuFromSections] Trying flat structure extraction on data");
                ExtractCpuFromJsonObject(data, profile);
                if (profile.CpuCores == 0 && TryGetPropertyCaseInsensitive(data, out var cpuCountEl, "cpuCount") && TryGetInt(cpuCountEl, out var cpuCount) && cpuCount > 0)
                {
                    // cpuCount can be the physical core count in some PS payloads
                    profile.CpuCores = cpuCount;
                }
                if (!string.IsNullOrEmpty(profile.CpuModel))
                {
                    App.LogMessage($"[HardwareProfileBuilder.TryCpuFromSections] SUCCESS: Found CPU from flat structure");
                    return;
                }

                // Some payloads expose identity in CPU.summary and not in data.cpus.
                if (TryGetPropertyCaseInsensitive(cpuSec, out var summaryEl, "summary") && summaryEl.ValueKind == JsonValueKind.Object)
                {
                    App.LogMessage($"[HardwareProfileBuilder.TryCpuFromSections] Trying summary extraction");
                    ExtractCpuFromJsonObject(summaryEl, profile);
                    if (!string.IsNullOrEmpty(profile.CpuModel) || profile.CpuCores > 0)
                        return;
                }
            }

            // Fallback: some PowerShell outputs use "SectionData" wrapper (e.g. sections.SectionData.CPU)
            if (string.IsNullOrEmpty(profile.CpuModel) && TryGetPropertyCaseInsensitive(sections, out var sectionData, "SectionData") && sectionData.ValueKind == JsonValueKind.Object)
            {
                App.LogMessage($"[HardwareProfileBuilder.TryCpuFromSections] Trying SectionData fallback");
                TryCpuFromSections(sectionData, profile);
            }
            
            App.LogMessage($"[HardwareProfileBuilder.TryCpuFromSections] END: CpuModel={profile.CpuModel ?? "(null)"}, CpuCores={profile.CpuCores}, CpuThreads={profile.CpuThreads}");
        }

        /// <summary>Get property by name with case-insensitive match (exact key first, then EnumerateObject).</summary>
        private static bool TryGetPropertyCaseInsensitive(JsonElement element, out JsonElement value, params string[] names)
        {
            value = default;
            if (element.ValueKind != JsonValueKind.Object) return false;
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out value)) return true;
            }
            foreach (var prop in element.EnumerateObject())
            {
                foreach (var name in names)
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Navigate from root via property names (case-insensitive) and optional array index. Returns true if the full path exists.</summary>
        private static bool TryGetNested(JsonElement root, out JsonElement result, params object[] path)
        {
            result = root;
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] is int index)
                {
                    if (result.ValueKind != JsonValueKind.Array) return false;
                    var arr = result.EnumerateArray().ToList();
                    if (index < 0 || index >= arr.Count) return false;
                    result = arr[index];
                }
                else if (path[i] is string name)
                {
                    if (result.ValueKind != JsonValueKind.Object) return false;
                    if (!TryGetPropertyCaseInsensitive(result, out result, name)) return false;
                }
            }
            return true;
        }

        /// <summary>Direct fallback: read CPU from scan_powershell.sections.CPU.data.cpus[0] or sections.CPU.data.cpus[0] when root is the scan_powershell blob.</summary>
        private static void TryCpuFromDirectPath(JsonElement? combinedRoot, HardwareProfile profile)
        {
            if (!combinedRoot.HasValue || combinedRoot.Value.ValueKind != JsonValueKind.Object) return;
            var root = combinedRoot.Value;

            // Path 1: scan_powershell.sections.CPU.data.cpus[0] (same as ComprehensiveEvidenceExtractor / section Processeur)
            if (TryGetNested(root, out var firstCpu, "scan_powershell", "sections", "CPU", "data", "cpus", 0) && firstCpu.ValueKind == JsonValueKind.Object)
            {
                App.LogMessage("[HardwareProfileBuilder.TryCpuFromDirectPath] Found CPU via scan_powershell.sections.CPU.data.cpus[0]");
                ExtractCpuFromJsonObject(firstCpu, profile);
                if (!string.IsNullOrEmpty(profile.CpuModel)) return;
            }

            // Path 2: sections.CPU.data.cpus[0]
            if (TryGetNested(root, out firstCpu, "sections", "CPU", "data", "cpus", 0) && firstCpu.ValueKind == JsonValueKind.Object)
            {
                App.LogMessage("[HardwareProfileBuilder.TryCpuFromDirectPath] Found CPU via sections.CPU.data.cpus[0]");
                ExtractCpuFromJsonObject(firstCpu, profile);
                if (!string.IsNullOrEmpty(profile.CpuModel)) return;
            }

            // Path 3: scan_powershell.sections.CPU.data.cpuList[0] (alternative array name)
            if (TryGetNested(root, out firstCpu, "scan_powershell", "sections", "CPU", "data", "cpuList", 0) && firstCpu.ValueKind == JsonValueKind.Object)
            {
                App.LogMessage("[HardwareProfileBuilder.TryCpuFromDirectPath] Found CPU via scan_powershell.sections.CPU.data.cpuList[0]");
                ExtractCpuFromJsonObject(firstCpu, profile);
                if (!string.IsNullOrEmpty(profile.CpuModel)) return;
            }

            // Path 4: scan_powershell.sections.CPUInfo.data (flat structure with Name, NumberOfCores, NumberOfLogicalProcessors)
            // This matches UnifiedReportBuilder.BuildSection3_MaterielPrincipal pattern
            if (TryGetNested(root, out var cpuInfoData, "scan_powershell", "sections", "CPUInfo", "data") && cpuInfoData.ValueKind == JsonValueKind.Object)
            {
                App.LogMessage("[HardwareProfileBuilder.TryCpuFromDirectPath] Found CPU via scan_powershell.sections.CPUInfo.data");
                ExtractCpuFromCpuInfoData(cpuInfoData, profile);
                if (!string.IsNullOrEmpty(profile.CpuModel)) return;
            }

            // Path 5: sections.CPUInfo.data
            if (TryGetNested(root, out cpuInfoData, "sections", "CPUInfo", "data") && cpuInfoData.ValueKind == JsonValueKind.Object)
            {
                App.LogMessage("[HardwareProfileBuilder.TryCpuFromDirectPath] Found CPU via sections.CPUInfo.data");
                ExtractCpuFromCpuInfoData(cpuInfoData, profile);
            }
        }

        /// <summary>Extract CPU from CPUInfo.data structure (flat: Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed).</summary>
        private static void ExtractCpuFromCpuInfoData(JsonElement data, HardwareProfile profile)
        {
            if (data.ValueKind != JsonValueKind.Object) return;

            // Model name: "Name" key (capital N as in UnifiedReportBuilder)
            if (string.IsNullOrEmpty(profile.CpuModel))
            {
                string[] nameKeys = { "Name", "name", "ProcessorName", "processorName", "Caption", "caption" };
                foreach (var nk in nameKeys)
                {
                    if (TryGetPropertyCaseInsensitive(data, out var nameEl, nk))
                    {
                        var name = GetStringFromJsonValue(nameEl);
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            profile.CpuModel = name.Trim();
                            App.LogMessage($"[HardwareProfileBuilder.ExtractCpuFromCpuInfoData] SET CpuModel={profile.CpuModel} from {nk}");
                            break;
                        }
                    }
                }
            }

            // Cores: "NumberOfCores" key
            if (profile.CpuCores == 0)
            {
                string[] coreKeys = { "NumberOfCores", "numberOfCores", "Cores", "cores", "CoreCount", "coreCount" };
                foreach (var ck in coreKeys)
                {
                    if (TryGetPropertyCaseInsensitive(data, out var coreEl, ck) && TryGetInt(coreEl, out var cores) && cores > 0)
                    {
                        profile.CpuCores = cores;
                        App.LogMessage($"[HardwareProfileBuilder.ExtractCpuFromCpuInfoData] SET CpuCores={cores} from {ck}");
                        break;
                    }
                }
            }

            // Threads: "NumberOfLogicalProcessors" key
            if (profile.CpuThreads == 0)
            {
                string[] threadKeys = { "NumberOfLogicalProcessors", "numberOfLogicalProcessors", "LogicalProcessors", "logicalProcessors", "ThreadCount", "threadCount" };
                foreach (var tk in threadKeys)
                {
                    if (TryGetPropertyCaseInsensitive(data, out var threadEl, tk) && TryGetInt(threadEl, out var threads) && threads > 0)
                    {
                        profile.CpuThreads = threads;
                        App.LogMessage($"[HardwareProfileBuilder.ExtractCpuFromCpuInfoData] SET CpuThreads={threads} from {tk}");
                        break;
                    }
                }
            }

            // MaxClockSpeed (MHz -> GHz)
            if (profile.CpuBaseGhz <= 0)
            {
                string[] speedKeys = { "MaxClockSpeed", "maxClockSpeed", "CurrentClockSpeed", "currentClockSpeed" };
                foreach (var sk in speedKeys)
                {
                    if (TryGetPropertyCaseInsensitive(data, out var speedEl, sk) && TryGetMetricDouble(speedEl, out var speedMhz) && speedMhz > 0)
                    {
                        profile.CpuBaseGhz = speedMhz / 1000.0;
                        App.LogMessage($"[HardwareProfileBuilder.ExtractCpuFromCpuInfoData] SET CpuBaseGhz={profile.CpuBaseGhz:F2} from {sk}");
                        break;
                    }
                }
            }
        }

        /// <summary>Try to extract CPU info from root-level cpuList array (CpuProfileAnalyzer format).</summary>
        private static void TryCpuFromCpuList(JsonElement root, HardwareProfile profile)
        {
            if (root.ValueKind != JsonValueKind.Object) return;

            if (!root.TryGetProperty("cpuList", out var cpuList) || cpuList.ValueKind != JsonValueKind.Array)
                return;

            var first = cpuList.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
                ExtractCpuFromJsonObject(first, profile);
        }

        /// <summary>Extract CPU properties from a JSON object (case-insensitive keys; supports NormalizedMetric { "value": ... }).</summary>
        private static void ExtractCpuFromJsonObject(JsonElement obj, HardwareProfile profile)
        {
            if (obj.ValueKind != JsonValueKind.Object)
            {
                App.LogMessage($"[HardwareProfileBuilder.ExtractCpuFromJsonObject] SKIP: obj.ValueKind={obj.ValueKind}, expected Object");
                return;
            }

            // Log all properties in the object for debugging
            var propNames = new List<string>();
            foreach (var p in obj.EnumerateObject()) propNames.Add($"{p.Name}:{p.Value.ValueKind}");
            App.LogMessage($"[HardwareProfileBuilder.ExtractCpuFromJsonObject] Processing object with {propNames.Count} properties: [{string.Join(", ", propNames)}]");

            // CPU Model Name — case-insensitive property match; accept direct string or { "value": "..." }
            // Include WMI-style keys (Caption, Description) used by some PowerShell/WMI collectors
            if (string.IsNullOrEmpty(profile.CpuModel))
            {
                foreach (var prop in obj.EnumerateObject())
                {
                    if (!MatchesAny(prop.Name, "name", "model", "processorname", "cpuname")) continue;
                    var str = GetStringFromJsonValue(prop.Value);
                    App.LogMessage($"[HardwareProfileBuilder.ExtractCpuFromJsonObject] Found CPU name candidate: {prop.Name}={str ?? "(null)"}");
                    if (!string.IsNullOrWhiteSpace(str))
                    {
                        profile.CpuModel = str;
                        App.LogMessage($"[HardwareProfileBuilder.ExtractCpuFromJsonObject] SET CpuModel={str}");
                        break;
                    }
                }
            }

            // CPU Cores — case-insensitive
            if (profile.CpuCores == 0)
            {
                foreach (var prop in obj.EnumerateObject())
                {
                    if (!MatchesAny(prop.Name, "cores", "corecount", "numberofcores", "physicalcores")) continue;
                    App.LogMessage($"[HardwareProfileBuilder.ExtractCpuFromJsonObject] Found cores candidate: {prop.Name}, ValueKind={prop.Value.ValueKind}, Raw={prop.Value.GetRawText()}");
                    if (TryGetInt(prop.Value, out int cores) && cores > 0)
                    {
                        profile.CpuCores = cores;
                        App.LogMessage($"[HardwareProfileBuilder.ExtractCpuFromJsonObject] SET CpuCores={cores}");
                        break;
                    }
                }
            }

            // CPU Threads — case-insensitive
            if (profile.CpuThreads == 0)
            {
                foreach (var prop in obj.EnumerateObject())
                {
                    if (!MatchesAny(prop.Name, "threads", "threadcount", "numberoflogicalprocessors", "logicalprocessors")) continue;
                    App.LogMessage($"[HardwareProfileBuilder.ExtractCpuFromJsonObject] Found threads candidate: {prop.Name}, ValueKind={prop.Value.ValueKind}, Raw={prop.Value.GetRawText()}");
                    if (TryGetInt(prop.Value, out int threads) && threads > 0)
                    {
                        profile.CpuThreads = threads;
                        App.LogMessage($"[HardwareProfileBuilder.ExtractCpuFromJsonObject] SET CpuThreads={threads}");
                        break;
                    }
                }
            }

            // Base Clock (GHz) — maxClockSpeed in MHz from PowerShell
            if (profile.CpuBaseGhz <= 0)
            {
                foreach (var prop in obj.EnumerateObject())
                {
                    if (!string.Equals(prop.Name, "maxClockSpeed", StringComparison.OrdinalIgnoreCase)) continue;
                    if (prop.Value.TryGetInt32(out var mhz) && mhz > 0)
                    {
                        profile.CpuBaseGhz = mhz / 1000.0;
                        break;
                    }
                }
                if (profile.CpuBaseGhz <= 0)
                {
                    foreach (var prop in obj.EnumerateObject())
                    {
                        if (!MatchesAny(prop.Name, "baseclockghz", "baseclock", "baseghz")) continue;
                        if (prop.Value.TryGetDouble(out var ghz) && ghz > 0)
                        {
                            profile.CpuBaseGhz = ghz;
                            break;
                        }
                    }
                }
            }

            // Boost Clock (GHz)
            if (profile.CpuBoostGhz <= 0)
            {
                foreach (var prop in obj.EnumerateObject())
                {
                    if (!MatchesAny(prop.Name, "maxclockghz", "boostclockghz", "boostclock", "turboclock", "maxclock")) continue;
                    if (prop.Value.TryGetDouble(out var ghz) && ghz > 0)
                    {
                        profile.CpuBoostGhz = ghz;
                        break;
                    }
                }
            }

        }

        private static bool MatchesAny(string name, params string[] keys)
        {
            foreach (var k in keys)
                if (string.Equals(name, k, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Get string from JSON value: direct string or object with "value" property (NormalizedMetric).</summary>
        private static string? GetStringFromJsonValue(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.String)
                return el.GetString();
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("value", out var v))
                return GetStringFromJsonValue(v);
            return null;
        }

        /// <summary>Try to get an integer from JSON element (handles both direct numbers and NormalizedMetric format).</summary>
        private static bool TryGetInt(JsonElement el, out int value)
        {
            value = 0;
            if (el.ValueKind == JsonValueKind.Number) return el.TryGetInt32(out value);
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out value)) return true;
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("value", out var v))
            {
                if (el.TryGetProperty("available", out var av) && av.ValueKind == JsonValueKind.False) return false;
                return TryGetInt(v, out value);
            }
            return false;
        }

        /// <summary>Try to get a string property with case-insensitive key matching.</summary>
        private static bool TryGetStringPropertyCaseInsensitive(JsonElement obj, out string? value, params string[] keys)
        {
            value = null;
            if (obj.ValueKind != JsonValueKind.Object) return false;

            foreach (var key in keys)
            {
                if (obj.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    value = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return true;
                }
            }

            // Case-insensitive fallback
            foreach (var prop in obj.EnumerateObject())
            {
                foreach (var key in keys)
                {
                    if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        value = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) return true;
                    }
                }
            }
            return false;
        }

        private static void ExtractGpu(HardwareProfile profile, JsonElement? sections, DiagnosticSnapshot? snapshot, HardwareSensorsResult? sensors, JsonElement? diagnosticSnapshotEl)
        {
            App.LogMessage($"[HardwareProfileBuilder.ExtractGpu] Starting GPU extraction. sensors.Gpu.Available={sensors?.Gpu != null}");
            
            // Primary: diagnostic_snapshot.metrics.gpu (name, model, vramTotalMB). If available=true and value>0, never show Unknown.
            if (diagnosticSnapshotEl.HasValue && diagnosticSnapshotEl.Value.TryGetProperty("metrics", out var metrics) && metrics.TryGetProperty("gpu", out var gpuM) && gpuM.ValueKind == JsonValueKind.Object)
            {
                App.LogMessage($"[HardwareProfileBuilder.ExtractGpu] Found diagnostic_snapshot.metrics.gpu");
                foreach (var kv in gpuM.EnumerateObject())
                {
                    if ((kv.Name.Equals("model", StringComparison.OrdinalIgnoreCase) || kv.Name.Equals("name", StringComparison.OrdinalIgnoreCase)) && kv.Value.ValueKind == JsonValueKind.String)
                    {
                        profile.GpuModel = kv.Value.GetString();
                        App.LogMessage($"[HardwareProfileBuilder.ExtractGpu] Source1: SET GpuModel={profile.GpuModel}");
                    }
                    if (kv.Name.Equals("vramTotalMB", StringComparison.OrdinalIgnoreCase) && TryGetMetricDouble(kv.Value, out var v) && v > 0)
                    {
                        profile.GpuVramMb = v;
                        App.LogMessage($"[HardwareProfileBuilder.ExtractGpu] Source1: SET GpuVramMb={profile.GpuVramMb}");
                    }
                }
            }
            if (snapshot?.Metrics != null && snapshot.Metrics.TryGetValue("gpu", out var gpuMetrics))
            {
                App.LogMessage($"[HardwareProfileBuilder.ExtractGpu] Found snapshot.Metrics['gpu']");
                if (profile.GpuModel == null && gpuMetrics.TryGetValue("model", out var gm) && gm.Available && gm.Value != null)
                {
                    profile.GpuModel = gm.Value.ToString();
                    App.LogMessage($"[HardwareProfileBuilder.ExtractGpu] Source2: SET GpuModel={profile.GpuModel}");
                }
                if (profile.GpuVramMb <= 0 && gpuMetrics.TryGetValue("vramTotalMB", out var v) && v.Available && v.Value is double vd)
                {
                    profile.GpuVramMb = vd;
                    App.LogMessage($"[HardwareProfileBuilder.ExtractGpu] Source2: SET GpuVramMb={profile.GpuVramMb}");
                }
            }
            if (sensors?.Gpu != null)
            {
                App.LogMessage($"[HardwareProfileBuilder.ExtractGpu] Sensors.Gpu: Name.Available={sensors.Gpu.Name.Available}, Name.Value={sensors.Gpu.Name.Value ?? "(null)"}, VramTotalMB.Available={sensors.Gpu.VramTotalMB.Available}, VramTotalMB.Value={sensors.Gpu.VramTotalMB.Value}");
                if (profile.GpuModel == null && sensors.Gpu.Name.Available && !string.IsNullOrEmpty(sensors.Gpu.Name.Value))
                {
                    profile.GpuModel = sensors.Gpu.Name.Value;
                    App.LogMessage($"[HardwareProfileBuilder.ExtractGpu] Source3: SET GpuModel={profile.GpuModel}");
                }
                if (profile.GpuVramMb <= 0 && sensors.Gpu.VramTotalMB.Available && sensors.Gpu.VramTotalMB.Value > 0)
                {
                    profile.GpuVramMb = sensors.Gpu.VramTotalMB.Value;
                    App.LogMessage($"[HardwareProfileBuilder.ExtractGpu] Source3: SET GpuVramMb={profile.GpuVramMb}");
                }
            }
            // Fallback: sections.GPU or sections.HARDWARE.GPU — do not prefer over diagnostic_snapshot
            TryGpuFromSections(sections, profile, "GPU");
            if ((profile.GpuModel == null || profile.GpuVramMb <= 0) && sections.HasValue)
                TryGpuFromSections(sections, profile, "HARDWARE");
            if (profile.GpuModel != null) profile.GpuModel = profile.GpuModel.Trim();
            
            App.LogMessage($"[HardwareProfileBuilder.ExtractGpu] FINAL RESULT: GpuModel={profile.GpuModel ?? "(null)"}, GpuVramMb={profile.GpuVramMb}");
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
            if (!sections.Value.TryGetProperty(sectionKey, out var sec)) return;

            // Handle direct data or wrapped in "data" property
            JsonElement data = sec;
            if (sec.ValueKind == JsonValueKind.Object && sec.TryGetProperty("data", out var dataEl))
                data = dataEl;
            if (data.ValueKind != JsonValueKind.Object) return;

            // Try multiple keys for total RAM (align with UnifiedReportBuilder: TotalPhysicalMemoryGB, etc.)
            string[] totalKeys = { "totalGB", "TotalGB", "TotalMemoryGB", "totalMemoryGB", "total_gb", "TotalPhysicalMemoryGB", "TotalPhysicalMemory", "RAM_GB", "RamGb" };
            foreach (var key in totalKeys)
            {
                if (!data.TryGetProperty(key, out var tg) || !tg.TryGetDouble(out var val) || val <= 0) continue;
                // TotalPhysicalMemory from WMI is often in bytes; convert if value looks like bytes (> 1e6)
                if (key.Equals("TotalPhysicalMemory", StringComparison.OrdinalIgnoreCase) && val > 1e6)
                    val = val / (1024.0 * 1024.0 * 1024.0);
                profile.RamGb = val;
                return;
            }
        }

        private static void ExtractRam(HardwareProfile profile, JsonElement? sections, DiagnosticSnapshot? snapshot, JsonElement? diagnosticSnapshotEl, HardwareSensorsResult? sensors)
        {
            // === SOURCE 1: diagnostic_snapshot JSON (machine.totalRamGB) ===
            if (profile.RamGb <= 0 && diagnosticSnapshotEl.HasValue)
            {
                try
                {
                    var snap = diagnosticSnapshotEl.Value;
                    if (snap.ValueKind == JsonValueKind.Object && snap.TryGetProperty("machine", out var machine) && machine.ValueKind == JsonValueKind.Object)
                    {
                        string[] ramKeys = { "totalRamGB", "TotalRamGB", "totalRAMGB", "TotalRAMGB", "ramGb", "RamGb" };
                        foreach (var key in ramKeys)
                        {
                            if (machine.TryGetProperty(key, out var tr))
                            {
                                if (tr.TryGetDouble(out var rg) && rg > 0) { profile.RamGb = rg; break; }
                                else if (TryGetMetricDouble(tr, out var rgm) && rgm > 0) { profile.RamGb = rgm; break; }
                            }
                        }
                    }
                    // Also try metrics.memory with broad key matching
                    if (profile.RamGb <= 0 && snap.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object)
                    {
                        if (metrics.TryGetProperty("memory", out var memMetrics) && memMetrics.ValueKind == JsonValueKind.Object)
                        {
                            // Try direct keys on metrics.memory
                            string[] memKeys = { "totalGB", "TotalGB", "total", "Total", "totalPhysicalMemoryGB", "TotalPhysicalMemoryGB",
                                "physicalMemoryGB", "PhysicalMemoryGB", "totalMemoryGB", "TotalMemoryGB", "installedMemoryGB", "InstalledMemoryGB",
                                "capacityGB", "CapacityGB", "ramGb", "RamGb", "RAM_GB" };
                            foreach (var mk in memKeys)
                            {
                                if (TryGetPropertyCaseInsensitive(memMetrics, out var mkEl, mk) && TryGetMetricDouble(mkEl, out var mkVal) && mkVal > 0)
                                { profile.RamGb = mkVal; break; }
                            }
                            // Some structures wrap in metrics.memory.value or metrics.memory.data
                            if (profile.RamGb <= 0 && TryGetPropertyCaseInsensitive(memMetrics, out var memValEl, "value"))
                            {
                                if (TryGetMetricDouble(memValEl, out var mvd) && mvd > 0)
                                    profile.RamGb = mvd;
                                else if (memValEl.ValueKind == JsonValueKind.Object)
                                {
                                    foreach (var mk in memKeys)
                                    {
                                        if (TryGetPropertyCaseInsensitive(memValEl, out var mkEl2, mk) && TryGetMetricDouble(mkEl2, out var mkVal2) && mkVal2 > 0)
                                        { profile.RamGb = mkVal2; break; }
                                    }
                                }
                            }
                            if (profile.RamGb <= 0 && TryGetPropertyCaseInsensitive(memMetrics, out var memDataEl, "data"))
                            {
                                if (memDataEl.ValueKind == JsonValueKind.Object)
                                {
                                    foreach (var mk in memKeys)
                                    {
                                        if (TryGetPropertyCaseInsensitive(memDataEl, out var mkEl3, mk) && TryGetMetricDouble(mkEl3, out var mkVal3) && mkVal3 > 0)
                                        { profile.RamGb = mkVal3; break; }
                                    }
                                }
                            }
                        }
                        // Fallback: try metrics.memoryPressure (might have totalGB or similar)
                        if (profile.RamGb <= 0 && TryGetPropertyCaseInsensitive(metrics, out var memPressure, "memoryPressure") && memPressure.ValueKind == JsonValueKind.Object)
                        {
                            string[] mpKeys = { "totalGB", "total", "physicalMemoryGB", "installedGB" };
                            foreach (var mk in mpKeys)
                            {
                                if (TryGetPropertyCaseInsensitive(memPressure, out var mkEl, mk) && TryGetMetricDouble(mkEl, out var mkVal) && mkVal > 0)
                                { profile.RamGb = mkVal; break; }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[HardwareProfileBuilder.ExtractRam] Source 1 error: {ex.Message}");
                }
            }

            // === SOURCE 2: DiagnosticSnapshot C# object ===
            if (profile.RamGb <= 0 && snapshot?.Machine?.TotalRamGB != null && snapshot.Machine.TotalRamGB > 0)
                profile.RamGb = snapshot.Machine.TotalRamGB.Value;

            if (profile.RamGb <= 0 && snapshot?.Metrics != null && snapshot.Metrics.TryGetValue("memory", out var memM))
            {
                if (memM.TryGetValue("totalGB", out var tg) && tg.Available && tg.Value is double tgd && tgd > 0)
                    profile.RamGb = tgd;
            }

            // === SOURCE 3: sections.Memory, MEMOIRE, MemoryInfo (PowerShell scan) ===
            string[] memorySections = { "Memory", "MemoryInfo", "MEMOIRE", "MEMOIRE RAM", "RAM" };
            foreach (var secName in memorySections)
            {
                TryRamFromSections(sections, profile, secName);
                if (profile.RamGb > 0) break;
            }

            // === Extract modules info for speed and dual channel ===
            if (sections.HasValue)
            {
                try
                {
                    foreach (var secName in memorySections)
                    {
                        if (!sections.Value.TryGetProperty(secName, out var memSec)) continue;

                        JsonElement data = memSec;
                        if (memSec.ValueKind == JsonValueKind.Object && memSec.TryGetProperty("data", out var dataEl))
                            data = dataEl;
                        if (data.ValueKind != JsonValueKind.Object) continue;

                        // Get total RAM if still missing (multiple key names for compatibility)
                        if (profile.RamGb <= 0)
                        {
                            string[] ramKeys = { "totalGB", "TotalGB", "TotalPhysicalMemoryGB", "TotalMemoryGB", "RAM_GB", "RamGb", "TotalRAM_GB", "totalRam" };
                            foreach (var key in ramKeys)
                            {
                                if (data.TryGetProperty(key, out var tgEl) && tgEl.TryGetDouble(out var tgVal) && tgVal > 0)
                                { profile.RamGb = tgVal; break; }
                            }
                        }

                        // Extract modules for speed and dual channel
                        string[] moduleKeys = { "modules", "Modules", "memoryModules", "MemoryModules" };
                        foreach (var modKey in moduleKeys)
                        {
                            if (!data.TryGetProperty(modKey, out var mods) || mods.ValueKind != JsonValueKind.Array) continue;

                            var arr = mods.EnumerateArray().ToList();
                            profile.DualChannel = arr.Count >= 2;
                            int maxSpeed = 0;
                            foreach (var m in arr)
                            {
                                string[] speedKeys = { "speedMHz", "SpeedMHz", "Speed", "speed", "clockSpeed", "ClockSpeed" };
                                foreach (var sk in speedKeys)
                                {
                                    if (m.TryGetProperty(sk, out var sp) && sp.TryGetInt32(out var speed) && speed > 0)
                                    {
                                        maxSpeed = Math.Max(maxSpeed, speed);
                                        break;
                                    }
                                }
                            }
                            if (maxSpeed > 0) profile.RamSpeedMhz = maxSpeed;
                            break;
                        }
                        if (profile.RamGb > 0) break;
                    }
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[HardwareProfileBuilder.ExtractRam] Modules extraction error: {ex.Message}");
                }
            }

            // === SOURCE 4: MachineIdentity section (PowerShell) ===
            if (profile.RamGb <= 0 && sections.HasValue)
            {
                try
                {
                    if (sections.Value.TryGetProperty("MachineIdentity", out var machineId))
                    {
                        JsonElement data = machineId;
                        if (machineId.ValueKind == JsonValueKind.Object && machineId.TryGetProperty("data", out var dataEl))
                            data = dataEl;
                        if (data.ValueKind == JsonValueKind.Object)
                        {
                            string[] ramKeys = { "TotalRAM_GB", "TotalRamGB", "TotalRAM", "totalRam", "TotalPhysicalMemoryGB", "RAM_GB", "RamGb", "totalGB" };
                            foreach (var key in ramKeys)
                            {
                                if (data.TryGetProperty(key, out var tr) && tr.TryGetDouble(out var rg) && rg > 0)
                                {
                                    profile.RamGb = rg;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[HardwareProfileBuilder.ExtractRam] MachineIdentity error: {ex.Message}");
                }
            }
        }

        private static void ExtractStorage(HardwareProfile profile, JsonElement? sections, JsonElement? diagnosticSnapshotEl)
        {
            App.LogMessage($"[HardwareProfileBuilder.ExtractStorage] Starting storage extraction. sections.HasValue={sections.HasValue}, diagnosticSnapshotEl.HasValue={diagnosticSnapshotEl.HasValue}");
            profile.StorageKind = PerformanceTierTable.StorageHdd; // Default
            bool hasNvme = false;
            bool hasSataSsd = false;

            // === SOURCE 0 (BEST): WMI MSFT_PhysicalDisk - most reliable for storage type ===
            // MediaType: 0=Unspecified, 3=HDD, 4=SSD, 5=SCM
            // BusType: 17=NVMe
            try
            {
                App.LogMessage($"[HardwareProfileBuilder.ExtractStorage] Trying WMI MSFT_PhysicalDisk...");
                using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT MediaType, BusType, Model, FriendlyName FROM MSFT_PhysicalDisk");
                foreach (ManagementObject disk in searcher.Get())
                {
                    var mediaType = disk["MediaType"]?.ToString() ?? "";
                    var busType = disk["BusType"]?.ToString() ?? "";
                    var model = disk["Model"]?.ToString() ?? disk["FriendlyName"]?.ToString() ?? "";
                    
                    App.LogMessage($"[HardwareProfileBuilder.ExtractStorage] WMI Disk: MediaType={mediaType}, BusType={busType}, Model={model}");

                    // BusType 17 = NVMe
                    if (busType == "17")
                    {
                        hasNvme = true;
                        App.LogMessage($"[HardwareProfileBuilder.ExtractStorage] => WMI: Detected NVMe (BusType=17)");
                    }
                    // MediaType 4 = SSD
                    else if (mediaType == "4")
                    {
                        hasSataSsd = true;
                        App.LogMessage($"[HardwareProfileBuilder.ExtractStorage] => WMI: Detected SSD (MediaType=4)");
                    }
                    // MediaType 3 = HDD - already the default, no action needed
                    else if (mediaType == "3")
                    {
                        App.LogMessage($"[HardwareProfileBuilder.ExtractStorage] => WMI: Detected HDD (MediaType=3)");
                    }
                    // MediaType 0 = Unspecified - check model name as fallback
                    else if (mediaType == "0" || string.IsNullOrEmpty(mediaType))
                    {
                        // Check model name for NVMe/SSD indicators
                        if (model.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
                        {
                            hasNvme = true;
                            App.LogMessage($"[HardwareProfileBuilder.ExtractStorage] => WMI: Detected NVMe from model name");
                        }
                        else if (model.Contains("SSD", StringComparison.OrdinalIgnoreCase))
                        {
                            hasSataSsd = true;
                            App.LogMessage($"[HardwareProfileBuilder.ExtractStorage] => WMI: Detected SSD from model name");
                        }
                    }
                    
                    if (hasNvme) break; // NVMe is the best, no need to continue
                }
            }
            catch (Exception ex)
            {
                App.LogMessage($"[HardwareProfileBuilder.ExtractStorage] WMI MSFT_PhysicalDisk error: {ex.Message}");
            }

            // If WMI found storage type, we're done with this source
            if (hasNvme || hasSataSsd)
            {
                App.LogMessage($"[HardwareProfileBuilder.ExtractStorage] WMI detection successful: hasNvme={hasNvme}, hasSataSsd={hasSataSsd}");
            }
            else
            {
                App.LogMessage($"[HardwareProfileBuilder.ExtractStorage] WMI detection inconclusive, trying JSON sources...");
            }

            // === SOURCE 1: diagnostic_snapshot metrics.storage ===
            if (!hasNvme && !hasSataSsd && diagnosticSnapshotEl.HasValue)
            {
                try
                {
                    var snap = diagnosticSnapshotEl.Value;
                    if (snap.ValueKind == JsonValueKind.Object && snap.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object)
                    {
                        if (metrics.TryGetProperty("storage", out var storMetrics) && storMetrics.ValueKind == JsonValueKind.Object)
                        {
                            // Check for storage type indicators
                            foreach (var prop in storMetrics.EnumerateObject())
                            {
                                var propName = prop.Name.ToLowerInvariant();
                                var propVal = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString()?.ToLowerInvariant() ?? "" : "";
                                
                                if (propName.Contains("nvme") || propVal.Contains("nvme"))
                                    hasNvme = true;
                                else if ((propName.Contains("ssd") || propVal.Contains("ssd")) && !propName.Contains("nvme"))
                                    hasSataSsd = true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[HardwareProfileBuilder.ExtractStorage] Source 1 error: {ex.Message}");
                }
            }

            // === SOURCE 2: sections.Storage (PowerShell scan) - only if WMI didn't find storage type ===
            if (!hasNvme && !hasSataSsd && sections.HasValue)
            {
                try
                {
                    string[] storageSections = { "Storage", "StorageInfo", "Disks", "STOCKAGE" };
                    foreach (var secName in storageSections)
                    {
                        if (!sections.Value.TryGetProperty(secName, out var storSec)) continue;

                        JsonElement data = storSec;
                        if (storSec.ValueKind == JsonValueKind.Object && storSec.TryGetProperty("data", out var dataEl))
                            data = dataEl;
                        if (data.ValueKind != JsonValueKind.Object) continue;

                        // Try multiple disk array property names
                        string[] diskArrayNames = { "physicalDisks", "PhysicalDisks", "disks", "Disks", "drives", "Drives", "smart" };
                        foreach (var arrName in diskArrayNames)
                        {
                            if (!data.TryGetProperty(arrName, out var disks) || disks.ValueKind != JsonValueKind.Array) continue;

                            foreach (var disk in disks.EnumerateArray())
                            {
                                var type = GetString(disk, "type") ?? GetString(disk, "mediaType") ?? GetString(disk, "MediaType") ?? "";
                                var iface = GetString(disk, "interface") ?? GetString(disk, "Interface") ?? GetString(disk, "busType") ?? GetString(disk, "BusType") ?? "";
                                var model = GetString(disk, "model") ?? GetString(disk, "Model") ?? GetString(disk, "name") ?? GetString(disk, "Name") ?? "";

                                App.LogMessage($"[HardwareProfileBuilder.ExtractStorage] Disk found: type='{type}', interface='{iface}', model='{model}'");

                                // NVMe detection
                                if (type.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ||
                                    iface.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ||
                                    model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ||
                                    iface.Equals("17", StringComparison.OrdinalIgnoreCase)) // BusType 17 = NVMe
                                {
                                    hasNvme = true;
                                    App.LogMessage($"[HardwareProfileBuilder.ExtractStorage] => Detected as NVMe");
                                }
                                // SSD detection (SATA or other)
                                else if (type.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
                                         model.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
                                         type.Equals("Solid State", StringComparison.OrdinalIgnoreCase))
                                {
                                    hasSataSsd = true;
                                    App.LogMessage($"[HardwareProfileBuilder.ExtractStorage] => Detected as SATA SSD");
                                }
                                else
                                {
                                    App.LogMessage($"[HardwareProfileBuilder.ExtractStorage] => No SSD/NVMe indicators found, treating as HDD");
                                }
                            }
                            if (hasNvme) break; // NVMe found, no need to continue
                        }
                        if (hasNvme) break;
                    }
                }
                catch (Exception ex)
                {
                    App.LogMessage($"[HardwareProfileBuilder.ExtractStorage] Source 2 error: {ex.Message}");
                }
            }

            // Set final storage kind
            if (hasNvme)
                profile.StorageKind = PerformanceTierTable.StorageNvme;
            else if (hasSataSsd)
                profile.StorageKind = PerformanceTierTable.StorageSataSsd;
            // else remains HDD (default)
            
            App.LogMessage($"[HardwareProfileBuilder.ExtractStorage] FINAL RESULT: hasNvme={hasNvme}, hasSataSsd={hasSataSsd}, StorageKind={profile.StorageKind}");
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
