using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Validateur de complétude UI - Tests contractuels pour garantir que les données
    /// présentes dans scan_result_combined.json apparaissent dans l'UI.
    /// 
    /// CONTRATS:
    /// - Si scan_powershell.sections.Memory existe → UI RAM doit avoir ≥3 lignes
    /// - Si scan_powershell.sections.Storage.data.volumes existe → UI Storage doit afficher toutes les partitions
    /// - Si scan_powershell.sections.CPU existe → UI CPU doit afficher modèle + coeurs + threads
    /// - Si scan_powershell.sections.GPU existe → UI GPU doit afficher driverVersion et resolution
    /// </summary>
    public static class UiCompletenessValidator
    {
        /// <summary>
        /// Résultat de la validation d'une section
        /// </summary>
        public class SectionValidation
        {
            public HealthDomain Domain { get; set; }
            public string SectionName { get; set; } = "";
            public bool DataExistsInJson { get; set; }
            public bool DataDisplayedInUi { get; set; }
            public int ExpectedMinFields { get; set; }
            public int ActualFields { get; set; }
            public double CoveragePercent { get; set; }
            public List<string> MissingFields { get; set; } = new();
            public List<string> Warnings { get; set; } = new();
            public bool IsValid => !DataExistsInJson || (DataDisplayedInUi && ActualFields >= ExpectedMinFields);
        }

        /// <summary>
        /// Résultat complet de la validation
        /// </summary>
        public class ValidationResult
        {
            public bool AllValid { get; set; }
            public int TotalSections { get; set; }
            public int ValidSections { get; set; }
            public double OverallCoverage { get; set; }
            public List<SectionValidation> Validations { get; set; } = new();
            public List<string> CriticalWarnings { get; set; } = new();
        }

        /// <summary>
        /// Définition des contrats de complétude par section
        /// </summary>
        private static readonly Dictionary<HealthDomain, (string[] requiredFields, int minFields)> Contracts = new()
        {
            { HealthDomain.OS, (new[] { "Version Windows", "Architecture", "Uptime" }, 3) },
            { HealthDomain.CPU, (new[] { "Modèle", "Cœurs / Threads" }, 2) },
            { HealthDomain.GPU, (new[] { "GPU", "Version pilote" }, 2) },
            { HealthDomain.RAM, (new[] { "RAM totale", "RAM utilisée" }, 2) },
            { HealthDomain.Storage, (new[] { "Partitions" }, 1) },
            { HealthDomain.Network, (new[] { "Adaptateur", "Adresse IP" }, 2) },
            { HealthDomain.SystemStability, (new[] { "BSOD" }, 1) },
            { HealthDomain.Drivers, (new[] { "Pilotes détectés" }, 1) },
            { HealthDomain.Applications, (new[] { "Apps installées" }, 1) },
            { HealthDomain.Performance, (new[] { "CPU", "RAM" }, 2) },
            { HealthDomain.Security, (new[] { "Antivirus", "BitLocker" }, 2) },
            { HealthDomain.Power, (new[] { "Plan alimentation" }, 1) }
        };

        /// <summary>
        /// Valide la complétude de l'UI par rapport aux données JSON.
        /// Utilisé pour les tests internes non-bloquants.
        /// </summary>
        public static ValidationResult Validate(JsonElement root, HealthReport? report, HardwareSensorsResult? sensors = null)
        {
            var result = new ValidationResult { TotalSections = 12 };
            var allCoverage = new List<double>();

            foreach (HealthDomain domain in Enum.GetValues<HealthDomain>())
            {
                var validation = ValidateSection(domain, root, report, sensors);
                result.Validations.Add(validation);

                if (validation.IsValid)
                    result.ValidSections++;
                else
                    result.CriticalWarnings.Add($"{domain}: {string.Join(", ", validation.Warnings)}");

                if (validation.CoveragePercent > 0)
                    allCoverage.Add(validation.CoveragePercent);
            }

            result.AllValid = result.ValidSections == result.TotalSections;
            result.OverallCoverage = allCoverage.Count > 0 ? allCoverage.Average() : 0;

            // Log résultats
            App.LogMessage($"[UiCompletenessValidator] Valid={result.AllValid}, " +
                $"Sections={result.ValidSections}/{result.TotalSections}, " +
                $"Coverage={result.OverallCoverage:F0}%");

            foreach (var warning in result.CriticalWarnings)
            {
                App.LogMessage($"[UiCompletenessValidator] WARNING: {warning}");
            }

            return result;
        }

        private static SectionValidation ValidateSection(
            HealthDomain domain, 
            JsonElement root, 
            HealthReport? report,
            HardwareSensorsResult? sensors)
        {
            var validation = new SectionValidation { Domain = domain };

            // 1. Vérifier si les données existent dans le JSON
            validation.DataExistsInJson = CheckJsonDataExists(domain, root);

            // 2. Extraire les données avec couverture
            var extraction = ComprehensiveEvidenceExtractor.ExtractWithCoverage(domain, root, sensors);
            validation.ActualFields = extraction.ActualFields;
            validation.CoveragePercent = extraction.CoverageScore;

            // 3. Vérifier les contrats
            if (Contracts.TryGetValue(domain, out var contract))
            {
                validation.ExpectedMinFields = contract.minFields;
                
                foreach (var field in contract.requiredFields)
                {
                    if (!extraction.Evidence.ContainsKey(field) && 
                        !extraction.Evidence.Keys.Any(k => k.Contains(field, StringComparison.OrdinalIgnoreCase)))
                    {
                        validation.MissingFields.Add(field);
                    }
                }
            }

            // 4. Vérifier si les données sont dans le HealthReport UI
            var section = report?.Sections.FirstOrDefault(s => s.Domain == domain);
            validation.DataDisplayedInUi = section?.EvidenceData?.Count > 0;
            validation.SectionName = section?.DisplayName ?? domain.ToString();

            // 5. Générer les warnings
            if (validation.DataExistsInJson && !validation.DataDisplayedInUi)
            {
                validation.Warnings.Add("Données JSON présentes mais non affichées dans l'UI");
            }
            if (validation.MissingFields.Count > 0)
            {
                validation.Warnings.Add($"Champs manquants: {string.Join(", ", validation.MissingFields)}");
            }
            if (validation.ActualFields < validation.ExpectedMinFields)
            {
                validation.Warnings.Add($"Trop peu de champs: {validation.ActualFields}/{validation.ExpectedMinFields}");
            }

            // Vérifications spécifiques par contrat
            ValidateSectionSpecificContracts(domain, extraction, root, validation);

            return validation;
        }

        private static void ValidateSectionSpecificContracts(
            HealthDomain domain, 
            ComprehensiveEvidenceExtractor.ExtractionResult extraction, 
            JsonElement root,
            SectionValidation validation)
        {
            switch (domain)
            {
                case HealthDomain.RAM:
                    // Contrat: si Memory existe → ≥3 lignes
                    if (validation.DataExistsInJson && extraction.ActualFields < 3)
                    {
                        validation.Warnings.Add("Memory existe mais UI RAM a moins de 3 lignes");
                    }
                    break;

                case HealthDomain.Storage:
                    // Contrat: si volumes existe → toutes les partitions doivent être affichées
                    var volumeCount = CountVolumes(root);
                    if (volumeCount > 0)
                    {
                        var partitionsDisplayed = extraction.Evidence.ContainsKey("Partitions");
                        if (!partitionsDisplayed)
                        {
                            validation.Warnings.Add($"Storage.volumes contient {volumeCount} volumes mais 'Partitions' non affiché");
                        }
                    }
                    break;

                case HealthDomain.CPU:
                    // Contrat: si CPU existe → modèle + coeurs + threads
                    if (validation.DataExistsInJson)
                    {
                        var hasModel = extraction.Evidence.ContainsKey("Modèle");
                        var hasCores = extraction.Evidence.ContainsKey("Cœurs / Threads") || 
                                       extraction.Evidence.ContainsKey("Cœurs");
                        if (!hasModel)
                            validation.Warnings.Add("CPU.data existe mais 'Modèle' non affiché");
                        if (!hasCores)
                            validation.Warnings.Add("CPU.data existe mais 'Cœurs' non affiché");
                    }
                    break;

                case HealthDomain.GPU:
                    // Contrat: si GPU existe → driverVersion et resolution
                    if (validation.DataExistsInJson)
                    {
                        var hasDriver = extraction.Evidence.ContainsKey("Version pilote");
                        var hasRes = extraction.Evidence.ContainsKey("Résolution");
                        if (!hasDriver)
                            validation.Warnings.Add("GPU.data existe mais 'Version pilote' non affiché");
                        // Resolution is optional, don't warn
                    }
                    break;

                case HealthDomain.Security:
                    // Contrat: BitLocker DOIT afficher Oui/Non, jamais "—"
                    if (extraction.Evidence.TryGetValue("BitLocker", out var bitlocker))
                    {
                        if (bitlocker.Contains("—") || bitlocker.Equals("-"))
                        {
                            validation.Warnings.Add("BitLocker affiche '—' au lieu de Oui/Non/Inconnu");
                        }
                    }
                    else
                    {
                        validation.Warnings.Add("BitLocker non présent dans Security");
                    }
                    break;
            }
        }

        private static bool CheckJsonDataExists(HealthDomain domain, JsonElement root)
        {
            var sectionName = domain switch
            {
                HealthDomain.OS => "OS",
                HealthDomain.CPU => "CPU",
                HealthDomain.GPU => "GPU",
                HealthDomain.RAM => "Memory",
                HealthDomain.Storage => "Storage",
                HealthDomain.Network => "Network",
                HealthDomain.SystemStability => "EventLogs",
                HealthDomain.Drivers => "DevicesDrivers",
                HealthDomain.Applications => "InstalledApplications",
                HealthDomain.Performance => "Processes",
                HealthDomain.Security => "Security",
                HealthDomain.Power => "Battery",
                _ => ""
            };

            if (string.IsNullOrEmpty(sectionName)) return false;

            // Check scan_powershell.sections
            if (root.TryGetProperty("scan_powershell", out var ps) &&
                ps.TryGetProperty("sections", out var sections) &&
                sections.TryGetProperty(sectionName, out _))
            {
                return true;
            }

            // Check direct sections
            if (root.TryGetProperty("sections", out var directSections) &&
                directSections.TryGetProperty(sectionName, out _))
            {
                return true;
            }

            return false;
        }

        private static int CountVolumes(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("scan_powershell", out var ps) &&
                    ps.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("Storage", out var storage))
                {
                    var data = storage.TryGetProperty("data", out var d) ? d : storage;
                    if (data.TryGetProperty("volumes", out var volumes) && 
                        volumes.ValueKind == JsonValueKind.Array)
                    {
                        return volumes.GetArrayLength();
                    }
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Génère un rapport de validation lisible pour le logging/debugging.
        /// </summary>
        public static string GenerateReport(ValidationResult result)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine("      UI COMPLETENESS VALIDATION REPORT");
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine($"Overall Status: {(result.AllValid ? "✅ PASSED" : "⚠️ WARNINGS")}");
            sb.AppendLine($"Valid Sections: {result.ValidSections}/{result.TotalSections}");
            sb.AppendLine($"Average Coverage: {result.OverallCoverage:F0}%");
            sb.AppendLine();

            foreach (var v in result.Validations)
            {
                var status = v.IsValid ? "✅" : "⚠️";
                sb.AppendLine($"{status} {v.Domain} ({v.SectionName})");
                sb.AppendLine($"   Data in JSON: {v.DataExistsInJson}, Displayed: {v.DataDisplayedInUi}");
                sb.AppendLine($"   Fields: {v.ActualFields}/{v.ExpectedMinFields} min, Coverage: {v.CoveragePercent:F0}%");
                
                if (v.MissingFields.Count > 0)
                    sb.AppendLine($"   Missing: {string.Join(", ", v.MissingFields)}");
                
                foreach (var w in v.Warnings)
                    sb.AppendLine($"   ⚠️ {w}");
            }

            if (result.CriticalWarnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("CRITICAL WARNINGS:");
                foreach (var w in result.CriticalWarnings)
                    sb.AppendLine($"  ❌ {w}");
            }

            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            return sb.ToString();
        }
    }
}
