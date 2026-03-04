using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Official provenance catalog for Rapport Integral UI fields.
    /// </summary>
    public static class IntegralFieldProvenanceCatalog
    {
        public sealed class FieldProvenance
        {
            public string SectionId { get; init; } = string.Empty;
            public string FieldId { get; init; } = string.Empty;
            public DataProvenanceType ProvenanceType { get; init; } = DataProvenanceType.Collected;
            public string JsonPath { get; init; } = string.Empty;
            public bool IsCritical { get; init; }
        }

        private static readonly Dictionary<string, FieldProvenance> Catalog = new(StringComparer.OrdinalIgnoreCase)
        {
            [Key("PlatformFirmware", "Version BIOS")] = new FieldProvenance
            {
                SectionId = "PlatformFirmware", FieldId = "Version BIOS", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "scan_powershell.sections.MachineIdentity.data.biosVersion", IsCritical = true
            },
            [Key("PlatformFirmware", "Date BIOS")] = new FieldProvenance
            {
                SectionId = "PlatformFirmware", FieldId = "Date BIOS", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "scan_powershell.sections.MachineIdentity.data.biosDate", IsCritical = true
            },
            [Key("PlatformFirmware", "TPM")] = new FieldProvenance
            {
                SectionId = "PlatformFirmware", FieldId = "TPM", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "scan_powershell.sections.MachineIdentity.data.tpmPresent", IsCritical = true
            },
            [Key("PlatformFirmware", "Version TPM")] = new FieldProvenance
            {
                SectionId = "PlatformFirmware", FieldId = "Version TPM", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "scan_powershell.sections.MachineIdentity.data.tpmVersion", IsCritical = true
            },
            [Key("PlatformFirmware", "Secure Boot")] = new FieldProvenance
            {
                SectionId = "PlatformFirmware", FieldId = "Secure Boot", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "scan_powershell.sections.MachineIdentity.data.secureBoot", IsCritical = true
            },
            [Key("GPU", "Température GPU")] = new FieldProvenance
            {
                SectionId = "GPU", FieldId = "Température GPU", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "sensors_csharp.gpu.gpuTempC", IsCritical = true
            },
            [Key("GPU", "Charge GPU (3D)")] = new FieldProvenance
            {
                SectionId = "GPU", FieldId = "Charge GPU (3D)", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "sensors_csharp.gpu.gpuLoadPercent", IsCritical = true
            },
            [Key("GPU", "VRAM Total")] = new FieldProvenance
            {
                SectionId = "GPU", FieldId = "VRAM Total", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "sensors_csharp.gpu.vramTotalMB", IsCritical = true
            },
            [Key("GPU", "VRAM Utilisée")] = new FieldProvenance
            {
                SectionId = "GPU", FieldId = "VRAM Utilisée", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "sensors_csharp.gpu.vramUsedMB", IsCritical = true
            },
            [Key("Performance", "Score performance")] = new FieldProvenance
            {
                SectionId = "Performance", FieldId = "Score performance", ProvenanceType = DataProvenanceType.Calculated,
                JsonPath = "technical_contract.score.value", IsCritical = true
            },
            [Key("Devices", "Programmes démarrage")] = new FieldProvenance
            {
                SectionId = "Devices", FieldId = "Programmes démarrage", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "scan_powershell.sections.StartupPrograms.data.startupCount", IsCritical = true
            },

            // ── CPU ──────────────────────────────────────────────────────────
            [Key("CPU", "Température CPU")] = new FieldProvenance
            {
                SectionId = "CPU", FieldId = "Température CPU", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "sensors_csharp.cpu.cpuTempC", IsCritical = true
            },
            [Key("CPU", "Charge CPU (%)")] = new FieldProvenance
            {
                SectionId = "CPU", FieldId = "Charge CPU (%)", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "diagnostic_snapshot.metrics.cpu.loadPercent", IsCritical = true
            },
            [Key("CPU", "Processeur")] = new FieldProvenance
            {
                SectionId = "CPU", FieldId = "Processeur", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "scan_powershell.sections.CPU.data.name", IsCritical = true
            },
            [Key("CPU", "Coeurs physiques")] = new FieldProvenance
            {
                SectionId = "CPU", FieldId = "Coeurs physiques", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "diagnostic_snapshot.metrics.cpu.cores", IsCritical = false
            },

            // ── RAM ──────────────────────────────────────────────────────────
            [Key("RAM", "RAM Total (GB)")] = new FieldProvenance
            {
                SectionId = "RAM", FieldId = "RAM Total (GB)", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "diagnostic_snapshot.metrics.memory.totalGB", IsCritical = true
            },
            [Key("RAM", "RAM Utilisee (%)")] = new FieldProvenance
            {
                SectionId = "RAM", FieldId = "RAM Utilisee (%)", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "diagnostic_snapshot.metrics.memory.usedPercent", IsCritical = true
            },
            [Key("RAM", "Modules RAM")] = new FieldProvenance
            {
                SectionId = "RAM", FieldId = "Modules RAM", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "scan_powershell.sections.Memory.data.modules", IsCritical = false
            },

            // ── Storage ──────────────────────────────────────────────────────
            [Key("Storage", "Disque Principal")] = new FieldProvenance
            {
                SectionId = "Storage", FieldId = "Disque Principal", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "scan_powershell.sections.Storage.data.disks[0].model", IsCritical = true
            },
            [Key("Storage", "Temperature Disque")] = new FieldProvenance
            {
                SectionId = "Storage", FieldId = "Temperature Disque", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "scan_powershell.sections.Temperatures.data.disks[0].tempC", IsCritical = true
            },
            [Key("Storage", "Statut SMART")] = new FieldProvenance
            {
                SectionId = "Storage", FieldId = "Statut SMART", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "scan_powershell.sections.Storage.data.disks[0].smartStatus", IsCritical = true
            },

            // ── Security ─────────────────────────────────────────────────────
            [Key("Security", "Antivirus")] = new FieldProvenance
            {
                SectionId = "Security", FieldId = "Antivirus", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "scan_powershell.sections.Security.data.antivirusName", IsCritical = true
            },
            [Key("Security", "Defenseur Windows")] = new FieldProvenance
            {
                SectionId = "Security", FieldId = "Defenseur Windows", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "security_info_csharp.defenderEnabled", IsCritical = true
            },
            [Key("Security", "Pare-feu")] = new FieldProvenance
            {
                SectionId = "Security", FieldId = "Pare-feu", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "security_info_csharp.firewallEnabled", IsCritical = true
            },

            // ── Updates ──────────────────────────────────────────────────────
            [Key("Updates", "En attente")] = new FieldProvenance
            {
                SectionId = "Updates", FieldId = "En attente", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "updates_csharp.pendingCount", IsCritical = true
            },
            [Key("Updates", "Redemarrage requis")] = new FieldProvenance
            {
                SectionId = "Updates", FieldId = "Redemarrage requis", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "scan_powershell.sections.WindowsUpdate.data.rebootRequired", IsCritical = true
            },

            // ── EventLogs ────────────────────────────────────────────────────
            [Key("EventLogs", "Erreurs critiques 48h")] = new FieldProvenance
            {
                SectionId = "EventLogs", FieldId = "Erreurs critiques 48h", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "event_logs_detailed[level=Critical]", IsCritical = true
            },
            [Key("EventLogs", "Evenements WHEA")] = new FieldProvenance
            {
                SectionId = "EventLogs", FieldId = "Evenements WHEA", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "diagnostic_signals.whea.value", IsCritical = true
            },

            // ── GPU (additional) ─────────────────────────────────────────────
            [Key("GPU", "Nom GPU")] = new FieldProvenance
            {
                SectionId = "GPU", FieldId = "Nom GPU", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "scan_powershell.sections.GPU.data.gpuList[0].name", IsCritical = true
            },
            [Key("GPU", "VRAM (PS fallback)")] = new FieldProvenance
            {
                SectionId = "GPU", FieldId = "VRAM (PS fallback)", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "scan_powershell.sections.GPU.data.gpuList[0].vramInfo", IsCritical = true
            },
            [Key("GPU", "Temperature GPU (PS)")] = new FieldProvenance
            {
                SectionId = "GPU", FieldId = "Temperature GPU (PS)", ProvenanceType = DataProvenanceType.Collected,
                JsonPath = "scan_powershell.sections.Temperatures.data.gpuTempInfo", IsCritical = true
            }
        };

        public static IReadOnlyCollection<FieldProvenance> GetCriticalCatalog() =>
            Catalog.Values.Where(v => v.IsCritical).ToList();

        public static FieldProvenance Resolve(string sectionId, string fieldId, string? source, bool isMissing)
        {
            var key = Key(sectionId, fieldId);
            if (Catalog.TryGetValue(key, out var found))
            {
                if (!isMissing)
                    return found;

                return new FieldProvenance
                {
                    SectionId = found.SectionId,
                    FieldId = found.FieldId,
                    JsonPath = found.JsonPath,
                    IsCritical = found.IsCritical,
                    ProvenanceType = DataProvenanceType.Unavailable
                };
            }

            var inferredType = InferProvenanceType(source, isMissing);
            var jsonPath = InferJsonPath(sectionId, fieldId, source);
            return new FieldProvenance
            {
                SectionId = sectionId ?? string.Empty,
                FieldId = fieldId ?? string.Empty,
                ProvenanceType = inferredType,
                JsonPath = jsonPath,
                IsCritical = IsCriticalField(sectionId ?? string.Empty, fieldId ?? string.Empty)
            };
        }

        public static string ToDisplayLabel(DataProvenanceType type) => type switch
        {
            DataProvenanceType.Collected => "Collecté",
            DataProvenanceType.Calculated => "Calculé",
            DataProvenanceType.Inferred => "Déduit",
            DataProvenanceType.Unavailable => "Indisponible",
            _ => "Collecté"
        };

        private static DataProvenanceType InferProvenanceType(string? source, bool isMissing)
        {
            if (isMissing)
                return DataProvenanceType.Unavailable;

            if (string.IsNullOrWhiteSpace(source))
                return DataProvenanceType.Calculated;

            if (source.Contains("PerformanceEvaluationEngine", StringComparison.OrdinalIgnoreCase) ||
                source.Contains("UnifiedDiagnosticScoreEngine", StringComparison.OrdinalIgnoreCase) ||
                source.Contains("QualityScoreCalculator", StringComparison.OrdinalIgnoreCase))
            {
                return DataProvenanceType.Calculated;
            }

            if (source.Contains("diagnostic_signals", StringComparison.OrdinalIgnoreCase) ||
                source.Contains("match", StringComparison.OrdinalIgnoreCase))
            {
                return DataProvenanceType.Inferred;
            }

            if (source.Contains("scan_powershell", StringComparison.OrdinalIgnoreCase) ||
                source.Contains("PS", StringComparison.OrdinalIgnoreCase) ||
                source.Contains("C#", StringComparison.OrdinalIgnoreCase) ||
                source.Contains("Collector", StringComparison.OrdinalIgnoreCase))
            {
                return DataProvenanceType.Collected;
            }

            return DataProvenanceType.Calculated;
        }

        private static string InferJsonPath(string sectionId, string fieldId, string? source)
        {
            if (!string.IsNullOrWhiteSpace(source) &&
                (source.Contains(".", StringComparison.Ordinal) || source.Contains("[", StringComparison.Ordinal)))
            {
                return source;
            }

            var normalizedSection = NormalizeToken(sectionId);
            var normalizedField = NormalizeToken(fieldId);
            return $"technical_contract.alias.{normalizedSection}.{normalizedField}";
        }

        private static bool IsCriticalField(string sectionId, string fieldId)
        {
            if (string.Equals(sectionId, "PlatformFirmware", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(sectionId, "GPU", StringComparison.OrdinalIgnoreCase) &&
                (fieldId.Contains("Température", StringComparison.OrdinalIgnoreCase) ||
                 fieldId.Contains("Temperature", StringComparison.OrdinalIgnoreCase) ||
                 fieldId.Contains("VRAM", StringComparison.OrdinalIgnoreCase) ||
                 fieldId.Contains("Charge", StringComparison.OrdinalIgnoreCase) ||
                 fieldId.Contains("Nom", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (string.Equals(sectionId, "CPU", StringComparison.OrdinalIgnoreCase) &&
                (fieldId.Contains("Température", StringComparison.OrdinalIgnoreCase) ||
                 fieldId.Contains("Temperature", StringComparison.OrdinalIgnoreCase) ||
                 fieldId.Contains("Charge", StringComparison.OrdinalIgnoreCase) ||
                 fieldId.Contains("Processeur", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (string.Equals(sectionId, "RAM", StringComparison.OrdinalIgnoreCase) &&
                (fieldId.Contains("Total", StringComparison.OrdinalIgnoreCase) ||
                 fieldId.Contains("Utilisee", StringComparison.OrdinalIgnoreCase) ||
                 fieldId.Contains("Utilisée", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (string.Equals(sectionId, "Storage", StringComparison.OrdinalIgnoreCase) &&
                (fieldId.Contains("Disque", StringComparison.OrdinalIgnoreCase) ||
                 fieldId.Contains("Temperature", StringComparison.OrdinalIgnoreCase) ||
                 fieldId.Contains("SMART", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (string.Equals(sectionId, "Security", StringComparison.OrdinalIgnoreCase) &&
                (fieldId.Contains("Antivirus", StringComparison.OrdinalIgnoreCase) ||
                 fieldId.Contains("Defenseur", StringComparison.OrdinalIgnoreCase) ||
                 fieldId.Contains("Pare-feu", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (string.Equals(sectionId, "Updates", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(sectionId, "EventLogs", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(sectionId, "Performance", StringComparison.OrdinalIgnoreCase) &&
                fieldId.Contains("Score", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(sectionId, "Devices", StringComparison.OrdinalIgnoreCase) &&
                fieldId.Contains("démarrage", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static string Key(string sectionId, string fieldId) =>
            $"{NormalizeToken(sectionId)}::{NormalizeToken(fieldId)}";

        private static string NormalizeToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                    continue;
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            }

            return sb.ToString();
        }
    }
}
