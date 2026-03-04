using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using PCDiagnosticPro.Services;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Taxonomie m?tier des s?v?rit?s - projection directe vers couleurs UI
    /// </summary>
    public enum HealthSeverity
    {
        /// <summary>État inconnu - données manquantes</summary>
        Unknown = 0,
        /// <summary>100% - Fonctionnement optimal</summary>
        Excellent = 1,
        /// <summary>70-99% - Bon ?tat g?n?ral</summary>
        Healthy = 2,
        /// <summary>60-69% - D?gradation l?g?re, attention recommand?e</summary>
        Warning = 3,
        /// <summary>40-59% - D?gradation significative, action requise</summary>
        Degraded = 4,
        /// <summary>&lt;40% - ??tat critique, intervention urgente</summary>
        Critical = 5
    }

    /// <summary>
    /// Domaines de diagnostic machine.
    /// </summary>
    public enum HealthDomain
    {
        OS,
        CPU,
        GPU,
        RAM,
        Storage,
        Network,
        SystemStability,
        Drivers,
        /// <summary>Applications: StartupPrograms, InstalledApplications, ScheduledTasks</summary>
        Applications,
        /// <summary>Performance: ProcessTelemetry, PerformanceCounters, real-time metrics</summary>
        Performance,
        /// <summary>Security: Antivirus, Firewall, UAC, SecureBoot, Bitlocker</summary>
        Security,
        /// <summary>Platform/Firmware: BIOS, TPM, Secure Boot with traceability.</summary>
        PlatformFirmware,
        /// <summary>Power: Battery, PowerSettings</summary>
        Power
    }

    /// <summary>
    /// Rapport de sant? complet - mod?le industriel production-grade
    /// Source de v?rit? : scoreV2 du script PowerShell
    /// </summary>
    public class HealthReport
    {
        /// <summary>Score global 0-100</summary>
        public int GlobalScore { get; set; }
        
        /// <summary>S?v?rit? globale calcul?e depuis le score</summary>
        public HealthSeverity GlobalSeverity { get; set; }
        
        /// <summary>Grade affich? (A, B, C, D, F)</summary>
        public string Grade { get; set; } = "N/A";
        
        /// <summary>Message principal pour l'utilisateur</summary>
        public string GlobalMessage { get; set; } = string.Empty;
        
        /// <summary>Sections de diagnostic par domaine</summary>
        public List<HealthSection> Sections { get; set; } = new();
        
        /// <summary>Recommandations prioritaires</summary>
        public List<HealthRecommendation> Recommendations { get; set; } = new();
        
        /// <summary>Métadonnées du scan</summary>
        public ScanMetadata Metadata { get; set; } = new();
        
        /// <summary>Donn?es brutes du scoreV2 PowerShell</summary>
        public ScoreV2Data ScoreV2 { get; set; } = new();
        
        /// <summary>Erreurs rencontr?es pendant le scan</summary>
        public List<ScanErrorInfo> Errors { get; set; } = new();
        
        /// <summary>Donn?es manquantes (capteurs indisponibles, etc.)</summary>
        public List<string> MissingData { get; set; } = new();
        
        /// <summary>Nombre d'erreurs collecteur d?riv? de errors[] (sans toucher PS). Si errors non vide ou partialFailure => ???1.</summary>
        public int CollectorErrorsLogical { get; set; }
        
        /// <summary>Statut global de collecte : OK / PARTIAL / FAILED. D?termine badge UI et cap score.</summary>
        public string CollectionStatus { get; set; } = "OK";
        
        /// <summary>Date de g?n?ration du rapport</summary>
        public DateTime GeneratedAt { get; set; } = DateTime.Now;
        
        /// <summary>Mod?le de confiance (coverage + coh?rence)</summary>
        public ConfidenceModel ConfidenceModel { get; set; } = new();
        
        /// <summary>Divergence entre score PS et score UDIS</summary>
        public ScoreDivergence Divergence { get; set; } = new();

        /// <summary>UDIS ??? Machine Health Score 0-100 (70% du total)</summary>
        public int MachineHealthScore { get; set; }

        /// <summary>UDIS ??? Data Reliability Score 0-100 (20% du total)</summary>
        public int DataReliabilityScore { get; set; }

        /// <summary>UDIS ??? Diagnostic Clarity Score 0-100 (10% du total)</summary>
        public int DiagnosticClarityScore { get; set; }

        /// <summary>Findings normalis?s pour LLM AutoFix</summary>
        public List<DiagnosticFinding> UdisFindings { get; set; } = new();

        /// <summary>AutoFix autoris? (Safety Gate)</summary>
        public bool AutoFixAllowed { get; set; }

        /// <summary>Rapport UDIS complet (optionnel)</summary>
        public UdisReport? UdisReport { get; set; }

        /// <summary>True when score is invalidated: critical data missing (Processes, Disks, etc.) ??? Fail-Close.</summary>
        public bool InsufficientDataForDiagnostic { get; set; }

        /// <summary>Calcule la s?v?rit? depuis un score</summary>
        public static HealthSeverity ScoreToSeverity(int score)
        {
            return score switch
            {
                100 => HealthSeverity.Excellent,
                >= 70 => HealthSeverity.Healthy,
                >= 60 => HealthSeverity.Warning,
                >= 40 => HealthSeverity.Degraded,
                _ => HealthSeverity.Critical
            };
        }
        
        /// <summary>Retourne la couleur hexad?cimale pour une s?v?rit?</summary>
        public static string SeverityToColor(HealthSeverity severity)
        {
            return severity switch
            {
                HealthSeverity.Excellent => "#FFD700",  // Gold
                HealthSeverity.Healthy => "#4CAF50",    // Green
                HealthSeverity.Warning => "#FFC107",    // Yellow/Amber
                HealthSeverity.Degraded => "#FF9800",   // Orange
                HealthSeverity.Critical => "#F44336",   // Red
                _ => "#9E9E9E"                          // Grey for Unknown
            };
        }
        
        /// <summary>Retourne l'ic?ne pour une s?v?rit?</summary>
        public static string SeverityToIcon(HealthSeverity severity)
        {
            var raw = severity switch
            {
                HealthSeverity.Excellent => "OK",
                HealthSeverity.Healthy => "OK",
                HealthSeverity.Warning => "!",
                HealthSeverity.Degraded => "!",
                HealthSeverity.Critical => "!",
                _ => "?"
            };
            return TextEncodingNormalizer.Normalize(raw);
        }
    }

    /// <summary>
    /// <summary>
    /// One row for the Performance Capability Matrix / bar chart (main window dashboard).
    /// </summary>
    public class PerformanceScenarioRow
    {
        public string Name { get; set; } = "";
        public int Score { get; set; }
        public string Classification { get; set; } = "";
    }

    /// <summary>
    /// One row for the "Score sur le march?" table on the main performance dashboard.
    /// </summary>
    public class PerformanceMarketRow
    {
        public string Component { get; set; } = "";
        public string DetectedModel { get; set; } = "";
        public string BenchmarkScoreDisplay { get; set; } = "";
        public string PercentileDisplay { get; set; } = "";
        public string RankDisplay { get; set; } = "";
        public string Source { get; set; } = "";
        public string ConfidenceDisplay { get; set; } = "";
    }

    /// <summary>
    /// Section de diagnostic pour un domaine sp?cifique
    /// </summary>
    public class HealthSection
    {
        /// <summary>Domaine de cette section</summary>
        public HealthDomain Domain { get; set; }
        
        /// <summary>Nom affich? (localis?)</summary>
        public string DisplayName { get; set; } = string.Empty;
        
        /// <summary>Ic?ne du domaine</summary>
        public string Icon { get; set; } = "????";
        
        /// <summary>Score de la section 0-100</summary>
        public int Score { get; set; }

        /// <summary>Reason when score is unavailable (Score &lt; 0).</summary>
        public string ScoreUnavailableReason { get; set; } = string.Empty;

        [JsonPropertyName("scoreDeductions")]
        public List<ScoreDeduction> ScoreDeductions { get; set; } = new();
        
        /// <summary>S?v?rit? calcul?e</summary>
        public HealthSeverity Severity { get; set; }
        
        /// <summary>Message court pour l'utilisateur</summary>
        public string StatusMessage { get; set; } = string.Empty;
        
        /// <summary>Explication d?taill?e (pour expansion)</summary>
        public string DetailedExplanation { get; set; } = string.Empty;
        
        /// <summary>Donn?es utilis?es pour calculer le score</summary>
        public Dictionary<string, string> EvidenceData { get; set; } = new();

        /// <summary>Performance dashboard: category label (Entry / Mid / High / Workstation). Filled by InjectPerformanceScore.</summary>
        public string PerformanceCategory { get; set; } = "";
        /// <summary>Performance dashboard: primary bottleneck text. Filled by InjectPerformanceScore.</summary>
        public string PrimaryBottleneck { get; set; } = "";
        /// <summary>Performance dashboard: short realistic summary. Filled by InjectPerformanceScore.</summary>
        public string RealisticSummary { get; set; } = "";
        /// <summary>Performance dashboard: scenario rows for matrix and bar chart. Filled by InjectPerformanceScore.</summary>
        public List<PerformanceScenarioRow> PerformanceScenarioRows { get; set; } = new();
        /// <summary>Performance dashboard: market rank rows (CPU/GPU/RAM/Storage/Global).</summary>
        public List<PerformanceMarketRow> PerformanceMarketRows { get; set; } = new();

        /// <summary>True when Performance evaluation succeeded; false when fallback (données ou erreur). Used to gate score/badge/bars in UI.</summary>
        public bool IsPerformanceEvaluationAvailable { get; set; } = true;

        /// <summary>Performance UI: CPU spec used (model or tier). "Unknown" when evaluation unavailable.</summary>
        public string PerformanceCpuDisplay { get; set; } = "";
        /// <summary>Performance UI: GPU spec used (model or tier). "Unknown" when evaluation unavailable.</summary>
        public string PerformanceGpuDisplay { get; set; } = "";
        /// <summary>Performance UI: VRAM dedicated (e.g. "8192 MB"). "Unknown" when evaluation unavailable.</summary>
        public string PerformanceVramDisplay { get; set; } = "";
        /// <summary>Performance UI: RAM (e.g. "16 GB"). "Unknown" when evaluation unavailable.</summary>
        public string PerformanceRamDisplay { get; set; } = "";
        /// <summary>Performance UI: Storage type (HDD/SATA_SSD/NVMe or "Unknown").</summary>
        public string PerformanceStorageDisplay { get; set; } = "";
        /// <summary>True when CPU tier was resolved from a known name pattern; false = Unmatched, reduces confidence.</summary>
        public bool PerformanceCpuNameMatched { get; set; } = true;
        /// <summary>True when GPU tier was resolved from a known name pattern; false = Unmatched, reduces confidence.</summary>
        public bool PerformanceGpuNameMatched { get; set; } = true;

        /// <summary>Info-bulles explicatives pour les termes techniques</summary>
        public Dictionary<string, string> EvidenceTooltips { get; set; } = new();

        /// <summary>True when at least one Kernel Power EventID 1 is present (power state change); enables the (i) button that opens KernelPowerInfoWindow.</summary>
        public bool HasKernelPowerId1 { get; set; }
        
        /// <summary>
        /// Donn?es avec info-bulles pour affichage UI
        /// Combine EvidenceData avec EvidenceTooltips
        /// </summary>
        public IEnumerable<EvidenceItem> EvidenceDataWithTooltips =>
            GetOrderedEvidenceData().Select(kvp => new EvidenceItem
            {
                Key = kvp.Key,
                Value = GetUiDisplayValue(kvp.Key, kvp.Value),
                Tooltip = EvidenceTooltips.TryGetValue(kvp.Key, out var tip) ? tip : GetDefaultTooltip(kvp.Key)
            });

        private string GetUiDisplayValue(string key, string value)
        {
            var normalizedValue = TextEncodingNormalizer.Normalize(value);
            var userFacingValue = TextEncodingNormalizer.ToUserFacingValue(normalizedValue);
            if (Domain != HealthDomain.PlatformFirmware)
                return userFacingValue;

            if (userFacingValue.StartsWith("Indisponible", StringComparison.OrdinalIgnoreCase))
                return "Indisponible";

            var sourceIndex = userFacingValue.IndexOf(" (source:", StringComparison.OrdinalIgnoreCase);
            if (sourceIndex >= 0)
                return userFacingValue.Substring(0, sourceIndex).Trim();

            var confidenceIndex = userFacingValue.IndexOf(" (confiance:", StringComparison.OrdinalIgnoreCase);
            if (confidenceIndex >= 0)
                return userFacingValue.Substring(0, confidenceIndex).Trim();

            var missingIndex = userFacingValue.IndexOf(" (reasonIfMissing:", StringComparison.OrdinalIgnoreCase);
            if (missingIndex >= 0)
                return "Indisponible";

            return userFacingValue;
        }

        private IEnumerable<KeyValuePair<string, string>> GetOrderedEvidenceData()
        {
            if (EvidenceData == null || EvidenceData.Count == 0)
                return Enumerable.Empty<KeyValuePair<string, string>>();

            var visibleEvidence = EvidenceData.Where(kvp => !IsLegacyScoreExplanationKey(kvp.Key));

            if (Domain != HealthDomain.Storage)
                return visibleEvidence;

            var storageOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Disques physiques"] = 10,
                ["Températures disques"] = 30,
                ["TempMax Disques"] = 31,
                ["Santé SMART"] = 40,
                ["Partitions"] = 50,
                ["Volume critique"] = 60,
                ["Capacité totale"] = 70,
                ["Top processus IO"] = 80
            };

            static int GetDiskIndex(string key)
            {
                if (!key.StartsWith("Disque ", StringComparison.OrdinalIgnoreCase))
                    return -1;

                var suffix = key.Substring("Disque ".Length).Trim();
                return int.TryParse(suffix, out var index) && index > 0 ? index : -1;
            }

            return visibleEvidence
                .OrderBy(kvp =>
                {
                    var normalizedKey = TextEncodingNormalizer.Normalize(kvp.Key);
                    var diskIndex = GetDiskIndex(normalizedKey);
                    if (diskIndex > 0)
                        return 20 + diskIndex;

                    return storageOrder.TryGetValue(normalizedKey, out var rank) ? rank : 1000;
                })
                .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsLegacyScoreExplanationKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            var normalized = TextEncodingNormalizer.Normalize(key);
            return normalized.Equals("Pourquoi ce score ?", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Equals("Pourquoi ce score?", StringComparison.OrdinalIgnoreCase);
        }
        
        /// <summary>
        /// Retourne une info-bulle courte. Les explications détaillées sont générées
        /// dynamiquement par InfoExplanationService lors du clic sur l'icône (i).
        /// </summary>
        private static string? GetDefaultTooltip(string key)
        {
            var normalizedKey = TextEncodingNormalizer.Normalize(key);
            if (!InfoContextResolver.SupportsMetricKey(normalizedKey))
                return null;

            return "Cliquez sur l'icône (i) pour afficher l'explication contextuelle.";
        }
        
        /// <summary>Recommandations sp?cifiques ? cette section</summary>
        public List<string> SectionRecommendations { get; set; } = new();
        
        /// <summary>Findings/problèmes détectés</summary>
        public List<HealthFinding> Findings { get; set; } = new();
        
        /// <summary>La section a-t-elle des données disponibles</summary>
        public bool HasData { get; set; } = true;
        
        /// <summary>Statut de collecte (OK, PARTIAL, FAILED)</summary>
        public string CollectionStatus { get; set; } = "OK";
    }

    public class ScoreDeduction
    {
        [JsonPropertyName("ruleId")]
        public string RuleId { get; set; } = string.Empty;

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;

        [JsonPropertyName("delta")]
        public int Delta { get; set; }

        [JsonPropertyName("sourceMetric")]
        public string SourceMetric { get; set; } = string.Empty;

        [JsonPropertyName("confidence")]
        public string Confidence { get; set; } = "medium";
    }

    /// <summary>
    /// Item de données avec info-bulle pour affichage UI.
    /// </summary>
    public class EvidenceItem
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string? Tooltip { get; set; }
        public bool HasTooltip => !string.IsNullOrEmpty(Tooltip);

        /// <summary>
        /// Indique si cette ligne ouvre une fenêtre de liste au clic (Périph. audio, Imprimantes, Obsolètes).
        /// </summary>
        public bool IsListDetailKey
        {
            get
            {
                var key = TextEncodingNormalizer.Normalize(Key);
                return key.Equals("Périph. audio", StringComparison.OrdinalIgnoreCase) ||
                       key.Equals("Periph. audio", StringComparison.OrdinalIgnoreCase) ||
                       key.Equals("Imprimantes", StringComparison.OrdinalIgnoreCase) ||
                       key.Equals("Obsolètes", StringComparison.OrdinalIgnoreCase) ||
                       key.Equals("Obsoletes", StringComparison.OrdinalIgnoreCase) ||
                       key.Equals("Pilotes obsolètes", StringComparison.OrdinalIgnoreCase) ||
                       key.Equals("Pilotes obsoletes", StringComparison.OrdinalIgnoreCase) ||
                       key.Equals("Barrettes", StringComparison.OrdinalIgnoreCase) ||
                       key.Equals("Points de restauration", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Détermine si l'icône info "i" doit être affichée pour cette métrique.
        /// </summary>
        public bool ShouldShowInfoIcon
        {
            get
            {
                var key = TextEncodingNormalizer.Normalize(Key);
                return InfoContextResolver.SupportsMetricKey(key);
            }
        }

        /// <summary>
        /// Icône de statut basée sur la valeur (⚠, 🚨, ou rien).
        /// </summary>
        public string StatusIcon
        {
            get
            {
                var key = TextEncodingNormalizer.Normalize(Key);
                var value = TextEncodingNormalizer.Normalize(Value);

                // Pas d'indicateur visuel pour ces champs : ils ont déjà un contexte dédié.
                if (key.Equals("Antivirus", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Pare-feu", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Secure Boot", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("BitLocker", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("UAC", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("SMBv1", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Protection en temps réel", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Protection contre altération", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("VBS", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Credential Guard", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Intégrité mémoire", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Règles ASR", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Kernel-Power", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Points de restauration", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Power throttling", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Non signés", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("Pilote ", StringComparison.OrdinalIgnoreCase) ||
                    // GPU
                    key.Equals("Température GPU", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("TDR", StringComparison.OrdinalIgnoreCase) ||
                    // Stockage
                    key.Equals("Températures disques", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("TempMax Disques", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Santé SMART", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Partitions", StringComparison.OrdinalIgnoreCase) ||
                    // Réseau
                    key.Equals("Latence (ping)", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Perte paquets", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Qualité connexion", StringComparison.OrdinalIgnoreCase) ||
                    // Stabilité
                    key.Equals("BSOD", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Erreurs WHEA", StringComparison.OrdinalIgnoreCase) ||
                    // OS
                    key.Equals("Updates Windows", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Erreurs critiques", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Redémarrage requis", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Bureau a distance (RDP)", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Bureau à distance (RDP)", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("RDP", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                if (string.IsNullOrWhiteSpace(value))
                    return string.Empty;

                var vLower = value.ToLowerInvariant();
                // Battery exception: desktop machines without battery are neutral information, not an alert.
                if (key.Equals("Batterie", StringComparison.OrdinalIgnoreCase) &&
                    vLower.Contains("desktop", StringComparison.Ordinal))
                {
                    return string.Empty;
                }

                // Battery warning only when a portable device is expected but battery is missing/unavailable.
                if (key.Equals("Batterie", StringComparison.OrdinalIgnoreCase) &&
                    (vLower.Contains("portable attendu", StringComparison.Ordinal) ||
                     vLower.Contains("batterie non detect", StringComparison.Ordinal) ||
                     vLower.Contains("batterie attendue", StringComparison.Ordinal)))
                {
                    return "âš ";
                }

                // Données indisponibles / inconnues : pas d'icône.
                if (vLower.Contains("non disponible", StringComparison.Ordinal) ||
                    vLower.Contains("unavailable", StringComparison.Ordinal) ||
                    vLower.Contains("données manquantes", StringComparison.Ordinal) ||
                    vLower.Contains("échec", StringComparison.Ordinal) ||
                    vLower.Contains("echoue", StringComparison.Ordinal) ||
                    vLower.Contains("failed", StringComparison.Ordinal) ||
                    vLower.Contains("inconnu", StringComparison.Ordinal) ||
                    vLower.Contains("unknown", StringComparison.Ordinal) ||
                    vLower.Contains("non détect", StringComparison.Ordinal))
                {
                    return string.Empty;
                }

                // Throttling détecté est positif (pas d'icône d'alerte ici).
                if (key.Contains("Throttling", StringComparison.OrdinalIgnoreCase) &&
                    (vLower.Contains("détecté", StringComparison.Ordinal) || vLower.Contains("detect", StringComparison.Ordinal)))
                {
                    return string.Empty;
                }

                if ((key.Contains("BSOD", StringComparison.OrdinalIgnoreCase) ||
                     key.Contains("WHEA", StringComparison.OrdinalIgnoreCase) ||
                     key.Contains("Kernel-Power", StringComparison.OrdinalIgnoreCase)) &&
                    (vLower.Contains("crash", StringComparison.Ordinal) ||
                     vLower.Contains("événement", StringComparison.Ordinal) ||
                     vLower.Contains("evenement", StringComparison.Ordinal) ||
                     vLower.Contains("jours", StringComparison.Ordinal)))
                {
                    return "âš ";
                }

                if (vLower.Contains("âš ", StringComparison.Ordinal))
                    return "âš ";

                if (vLower.Contains("🚨", StringComparison.Ordinal) ||
                    vLower.Contains("âŒ", StringComparison.Ordinal) ||
                    (vLower.StartsWith("non", StringComparison.Ordinal) && !vLower.Contains("non détect", StringComparison.Ordinal)) ||
                    vLower.Contains("désactiv", StringComparison.Ordinal))
                {
                    return "🚨";
                }

                return string.Empty;
            }
        }
    }
    
    /// <summary>
    /// Problème/finding détecté
    /// </summary>
    public class HealthFinding
    {
        public HealthSeverity Severity { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public int PenaltyApplied { get; set; }
    }

    /// <summary>
    /// Recommandation pour l'utilisateur
    /// </summary>
    public class HealthRecommendation
    {
        public HealthSeverity Priority { get; set; }
        public HealthDomain? RelatedDomain { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ActionText { get; set; } = string.Empty;
    }

    /// <summary>
    /// Métadonnées du scan PowerShell
    /// </summary>
    public class ScanMetadata
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "unknown";
        
        [JsonPropertyName("runId")]
        public string RunId { get; set; } = string.Empty;
        
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }
        
        [JsonPropertyName("isAdmin")]
        public bool IsAdmin { get; set; }
        
        [JsonPropertyName("redactLevel")]
        public string RedactLevel { get; set; } = "standard";
        
        [JsonPropertyName("quickScan")]
        public bool QuickScan { get; set; }
        
        [JsonPropertyName("monitorSeconds")]
        public int MonitorSeconds { get; set; }
        
        [JsonPropertyName("durationSeconds")]
        public double DurationSeconds { get; set; }
        
        [JsonPropertyName("partialFailure")]
        public bool PartialFailure { get; set; }
    }

    /// <summary>
    /// Donn?es scoreV2 du PowerShell - source de v?rit? pour le score
    /// </summary>
    public class ScoreV2Data
    {
        [JsonPropertyName("score")]
        public int Score { get; set; } = 100;
        
        [JsonPropertyName("baseScore")]
        public int BaseScore { get; set; } = 100;
        
        [JsonPropertyName("totalPenalty")]
        public int TotalPenalty { get; set; }
        
        [JsonPropertyName("breakdown")]
        public ScoreBreakdown Breakdown { get; set; } = new();
        
        [JsonPropertyName("grade")]
        public string Grade { get; set; } = "N/A";
        
        [JsonPropertyName("topPenalties")]
        public List<PenaltyInfo> TopPenalties { get; set; } = new();
        
        /// <summary>
        /// FIX RISK #5: Reason why score is unavailable (if Score == -1)
        /// </summary>
        [JsonIgnore]
        public string? UnavailableReason { get; set; }
        
        /// <summary>
        /// True if score could not be calculated (Score == -1)
        /// </summary>
        [JsonIgnore]
        public bool IsUnavailable => Score < 0;
    }

    /// <summary>
    /// D?tail des p?nalit?s par cat?gorie
    /// </summary>
    public class ScoreBreakdown
    {
        [JsonPropertyName("critical")]
        public int Critical { get; set; }
        
        [JsonPropertyName("collectorErrors")]
        public int CollectorErrors { get; set; }
        
        [JsonPropertyName("warnings")]
        public int Warnings { get; set; }
        
        [JsonPropertyName("timeouts")]
        public int Timeouts { get; set; }
        
        [JsonPropertyName("infoIssues")]
        public int InfoIssues { get; set; }
        
        [JsonPropertyName("excludedLimitations")]
        public int ExcludedLimitations { get; set; }
    }

    /// <summary>
    /// Information sur une p?nalit? sp?cifique
    /// </summary>
    public class PenaltyInfo
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
        
        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;
        
        [JsonPropertyName("penalty")]
        public int Penalty { get; set; }
        
        [JsonPropertyName("msg")]
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Erreur rencontr?e pendant le scan
    /// </summary>
    public class ScanErrorInfo
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;
        
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
        
        [JsonPropertyName("section")]
        public string Section { get; set; } = string.Empty;
        
        [JsonPropertyName("exceptionType")]
        public string ExceptionType { get; set; } = string.Empty;
    }

    /// <summary>
    /// Mod?le de confiance du score - coverage + coh?rence
    /// </summary>
    public class ConfidenceModel
    {
        /// <summary>Score de confiance global 0-100</summary>
        public int ConfidenceScore { get; set; } = 100;
        
        /// <summary>Niveau de confiance textuel ??? calcul? automatiquement depuis le score.
        /// ???90 = Fiable, ???70 = Moyen, &lt;70 = Faible.</summary>
        public string ConfidenceLevel
        {
            get => ConfidenceScore >= 90 ? "Fiable" : ConfidenceScore >= 70 ? "Moyen" : "Faible";
            set { /* setter conserv? pour compatibilit? JSON mais la valeur est toujours recalcul?e */ }
        }
        
        /// <summary>Ratio de couverture des sections PS (0-1)</summary>
        public double SectionsCoverage { get; set; } = 1.0;
        
        /// <summary>Ratio de couverture des capteurs hardware (0-1)</summary>
        public double SensorsCoverage { get; set; } = 0.0;
        
        /// <summary>Nombre de capteurs disponibles</summary>
        public int SensorsAvailable { get; set; }
        
        /// <summary>Nombre total de capteurs attendus</summary>
        public int SensorsTotal { get; set; }
        
        /// <summary>Avertissements sur la qualité des données</summary>
        public List<string> Warnings { get; set; } = new();
        
        /// <summary>Indique si le score est fiable (seuil : ???70)</summary>
        public bool IsReliable => ConfidenceScore >= 70;
    }

    /// <summary>
    /// Tra?abilit? de la divergence entre score PS et score UDIS
    /// </summary>
    public class ScoreDivergence
    {
        /// <summary>Score original du PowerShell (scoreV2)</summary>
        public int PowerShellScore { get; set; }
        
        /// <summary>Grade original du PowerShell</summary>
        public string PowerShellGrade { get; set; } = "N/A";
        
        /// <summary>Score calcul? par UDIS (legacy field name conserv? pour compat JSON)</summary>
        public int GradeEngineScore { get; set; }
        
        /// <summary>Grade calcul? par UDIS</summary>
        public string GradeEngineGrade { get; set; } = "N/A";
        
        /// <summary>Diff?rence absolue entre les deux scores</summary>
        public int Delta => Math.Abs(GradeEngineScore - PowerShellScore);
        
        /// <summary>Indique si les deux scores sont coh?rents (delta &lt;= 10)</summary>
        public bool IsCoherent => Delta <= 10;
        
        /// <summary>Explication de la divergence</summary>
        public string Explanation { get; set; } = "";
        
        /// <summary>Source de v?rit? utilis?e pour l'affichage UI</summary>
        public string SourceOfTruth { get; set; } = "UDIS";
    }
}





