using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace PCDiagnosticPro.Models
{
    /// <summary>
    /// Taxonomie métier des sévérités - projection directe vers couleurs UI
    /// </summary>
    public enum HealthSeverity
    {
        /// <summary>État inconnu - données manquantes</summary>
        Unknown = 0,
        /// <summary>100% - Fonctionnement optimal</summary>
        Excellent = 1,
        /// <summary>70-99% - Bon état général</summary>
        Healthy = 2,
        /// <summary>60-69% - Dégradation légère, attention recommandée</summary>
        Warning = 3,
        /// <summary>40-59% - Dégradation significative, action requise</summary>
        Degraded = 4,
        /// <summary>&lt;40% - État critique, intervention urgente</summary>
        Critical = 5
    }

    /// <summary>
    /// Domaines de diagnostic machine - Extended with Applications and Performance
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
        /// <summary>Power: Battery, PowerSettings</summary>
        Power
    }

    /// <summary>
    /// Rapport de santé complet - modèle industriel production-grade
    /// Source de vérité : scoreV2 du script PowerShell
    /// </summary>
    public class HealthReport
    {
        /// <summary>Score global 0-100</summary>
        public int GlobalScore { get; set; }
        
        /// <summary>Sévérité globale calculée depuis le score</summary>
        public HealthSeverity GlobalSeverity { get; set; }
        
        /// <summary>Grade affiché (A, B, C, D, F)</summary>
        public string Grade { get; set; } = "N/A";
        
        /// <summary>Message principal pour l'utilisateur</summary>
        public string GlobalMessage { get; set; } = string.Empty;
        
        /// <summary>Sections de diagnostic par domaine</summary>
        public List<HealthSection> Sections { get; set; } = new();
        
        /// <summary>Recommandations prioritaires</summary>
        public List<HealthRecommendation> Recommendations { get; set; } = new();
        
        /// <summary>Métadonnées du scan</summary>
        public ScanMetadata Metadata { get; set; } = new();
        
        /// <summary>Données brutes du scoreV2 PowerShell</summary>
        public ScoreV2Data ScoreV2 { get; set; } = new();
        
        /// <summary>Erreurs rencontrées pendant le scan</summary>
        public List<ScanErrorInfo> Errors { get; set; } = new();
        
        /// <summary>Données manquantes (capteurs indisponibles, etc.)</summary>
        public List<string> MissingData { get; set; } = new();
        
        /// <summary>Nombre d'erreurs collecteur dérivé de errors[] (sans toucher PS). Si errors non vide ou partialFailure => ≥1.</summary>
        public int CollectorErrorsLogical { get; set; }
        
        /// <summary>Statut global de collecte : OK / PARTIAL / FAILED. Détermine badge UI et cap score.</summary>
        public string CollectionStatus { get; set; } = "OK";
        
        /// <summary>Date de génération du rapport</summary>
        public DateTime GeneratedAt { get; set; } = DateTime.Now;
        
        /// <summary>Modèle de confiance (coverage + cohérence)</summary>
        public ConfidenceModel ConfidenceModel { get; set; } = new();
        
        /// <summary>Divergence entre score PS et score UDIS</summary>
        public ScoreDivergence Divergence { get; set; } = new();

        /// <summary>UDIS — Machine Health Score 0-100 (70% du total)</summary>
        public int MachineHealthScore { get; set; }

        /// <summary>UDIS — Data Reliability Score 0-100 (20% du total)</summary>
        public int DataReliabilityScore { get; set; }

        /// <summary>UDIS — Diagnostic Clarity Score 0-100 (10% du total)</summary>
        public int DiagnosticClarityScore { get; set; }

        /// <summary>Findings normalisés pour LLM AutoFix</summary>
        public List<DiagnosticFinding> UdisFindings { get; set; } = new();

        /// <summary>AutoFix autorisé (Safety Gate)</summary>
        public bool AutoFixAllowed { get; set; }

        /// <summary>Rapport UDIS complet (optionnel)</summary>
        public UdisReport? UdisReport { get; set; }

        /// <summary>True when score is invalidated: critical data missing (Processes, Disks, etc.) — Fail-Close.</summary>
        public bool InsufficientDataForDiagnostic { get; set; }

        /// <summary>Calcule la sévérité depuis un score</summary>
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
        
        /// <summary>Retourne la couleur hexadécimale pour une sévérité</summary>
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
        
        /// <summary>Retourne l'icône pour une sévérité</summary>
        public static string SeverityToIcon(HealthSeverity severity)
        {
            return severity switch
            {
                HealthSeverity.Excellent => "✓",
                HealthSeverity.Healthy => "✓",
                HealthSeverity.Warning => "⚠",
                HealthSeverity.Degraded => "⚠",
                HealthSeverity.Critical => "✕",
                _ => "?"
            };
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
    /// One row for the "Score sur le marché" table on the main performance dashboard.
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
    /// Section de diagnostic pour un domaine spécifique
    /// </summary>
    public class HealthSection
    {
        /// <summary>Domaine de cette section</summary>
        public HealthDomain Domain { get; set; }
        
        /// <summary>Nom affiché (localisé)</summary>
        public string DisplayName { get; set; } = string.Empty;
        
        /// <summary>Icône du domaine</summary>
        public string Icon { get; set; } = "📊";
        
        /// <summary>Score de la section 0-100</summary>
        public int Score { get; set; }
        
        /// <summary>Sévérité calculée</summary>
        public HealthSeverity Severity { get; set; }
        
        /// <summary>Message court pour l'utilisateur</summary>
        public string StatusMessage { get; set; } = string.Empty;
        
        /// <summary>Explication détaillée (pour expansion)</summary>
        public string DetailedExplanation { get; set; } = string.Empty;
        
        /// <summary>Données utilisées pour calculer le score</summary>
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
        /// Données avec info-bulles pour affichage UI
        /// Combine EvidenceData avec EvidenceTooltips
        /// </summary>
        public IEnumerable<EvidenceItem> EvidenceDataWithTooltips => 
            EvidenceData.Select(kvp => new EvidenceItem
            {
                Key = kvp.Key,
                Value = kvp.Value,
                Tooltip = EvidenceTooltips.TryGetValue(kvp.Key, out var tip) ? tip : GetDefaultTooltip(kvp.Key)
            });
        
        /// <summary>
        /// Retourne une info-bulle par défaut pour les termes techniques courants
        /// </summary>
        private static string? GetDefaultTooltip(string key)
        {
            return key.ToLower() switch
            {
                // Stabilité système - PARTIE 7: Tooltips complets avec définitions
                "bsod" or "bsod 30j" => 
                    "BSOD (Blue Screen of Death)\n\n" +
                    "Définition : Écran bleu affiché par Windows lors d'une erreur critique qui empêche le système de fonctionner normalement.\n\n" +
                    "Importance : Un BSOD occasionnel peut être bénin, mais des BSOD fréquents indiquent généralement un problème matériel (RAM, disque), de pilote, ou de corruption système.\n\n" +
                    "Risques : Perte de données non enregistrées, instabilité récurrente, usure prématurée des composants si non résolu.\n\n" +
                    "Que faire : Notez le code d'erreur (ex: DRIVER_IRQL_NOT_LESS_OR_EQUAL), vérifiez les pilotes récemment installés, testez la RAM avec Windows Memory Diagnostic.",
                    
                "erreurs whea" or "whea" or "whea 30j" => 
                    "WHEA (Windows Hardware Error Architecture)\n\n" +
                    "Définition : Système intégré à Windows qui détecte et enregistre les erreurs matérielles. WHEA surveille le processeur, la mémoire, les bus (PCIe), et d'autres composants.\n\n" +
                    "Importance : Des erreurs WHEA récurrentes peuvent signaler une défaillance imminente du matériel, même si le système semble fonctionner.\n\n" +
                    "Risques : Corruption de données, plantages inattendus, panne matérielle totale si ignoré.\n\n" +
                    "Que faire : Vérifiez la température du CPU/GPU, testez la RAM, surveillez les journaux d'événements Windows pour identifier le composant concerné.",
                    
                "kernel-power" => 
                    "Kernel-Power (ID 41)\n\n" +
                    "Définition : Événement Windows signalant un arrêt système inattendu sans arrêt propre. Souvent appelé \"bug check\" ou crash kernel.\n\n" +
                    "Causes courantes : Coupure de courant soudaine, alimentation défaillante, surchauffe entraînant un arrêt de protection, pilote défectueux causant un crash.\n\n" +
                    "Importance : Des événements fréquents indiquent un problème d'alimentation ou de stabilité nécessitant une attention immédiate.\n\n" +
                    "Que faire : Vérifiez l'alimentation (onduleur recommandé), contrôlez les températures, mettez à jour les pilotes.",
                    
                "points de restauration" => 
                    "Points de restauration système\n\n" +
                    "Définition : Sauvegardes automatiques de l'état du système (registre, fichiers système, programmes installés) créées par Windows avant des modifications importantes.\n\n" +
                    "Importance : Permettent de revenir à un état antérieur si une mise à jour ou installation cause des problèmes.\n\n" +
                    "Recommandation : Avoir au moins 1 point de restauration récent (< 30 jours). Politique interne basée sur le risque (pas une exigence ISO).\n\n" +
                    "Que faire si aucun point récent : Créez un point manuellement via 'Créer un point de restauration' dans les paramètres système.",
                    
                "âge dernier point" => 
                    "Fraîcheur du dernier point de restauration\n\n" +
                    "Le seuil de 30 jours est une politique interne basée sur les bonnes pratiques, pas une exigence normative.\n\n" +
                    "Un point récent vous permet de revenir à un état stable en cas de problème après une mise à jour ou installation.",
                
                // FIX #8: Sécurité — définitions complètes avec emojis (définition, importance, risque)
                "bitlocker" => "📖 Définition : BitLocker est une fonctionnalité de chiffrement complet du disque intégrée à certaines éditions de Windows (Pro/Enterprise). Elle protège vos données en chiffrant l'intégralité du volume où est installé le système et/ou d'autres lecteurs.\n\n⚠️ Importance : Très important pour la confidentialité et la sécurité des données, surtout en cas de vol ou de perte de l'appareil.\n\n🚨 Risque si désactivé : Vos données sont vulnérables à l'accès non autorisé si quelqu'un obtient un accès physique à votre appareil. Non disponible sur Windows Home.",
                "secure boot" => "📖 Définition : Secure Boot est une fonctionnalité de sécurité du micrologiciel UEFI qui garantit que votre ordinateur démarre uniquement avec des logiciels de confiance (comme Windows). Il empêche le chargement de logiciels malveillants ou non autorisés avant même le démarrage du système d'exploitation.\n\n⚠️ Importance : Fondamental pour protéger le processus de démarrage contre les rootkits et autres menaces persistantes avancées.\n\n🚨 Risque si désactivé : L'ordinateur pourrait démarrer avec des logiciels malveillants ou des systèmes d'exploitation non fiables, compromettant la sécurité dès le démarrage.",
                "uac" => "📖 Définition : Le Contrôle de compte d'utilisateur (UAC) est une fonction de sécurité de Windows qui aide à empêcher les modifications non autorisées sur votre ordinateur. Lorsque l'UAC est actif, les applications et les tâches s'exécutent avec des autorisations limitées, et une invite de consentement est affichée avant que les actions nécessitant des privilèges d'administrateur ne soient exécutées.\n\n⚠️ Importance : Essentiel pour la protection contre les logiciels malveillants et pour prévenir les modifications accidentelles du système.\n\n🚨 Risque si désactivé : Les programmes malveillants peuvent s'exécuter avec des privilèges élevés sans votre consentement, rendant votre système plus vulnérable.",
                "rdp" => "📖 Définition : Le Protocole de Bureau à distance (RDP) est une technologie de Microsoft qui permet à un utilisateur de se connecter à un ordinateur distant via un réseau et d'afficher le bureau de cet ordinateur. Il est couramment utilisé pour l'administration à distance et le support technique.\n\n⚠️ Importance : Utile pour l'accès et la gestion à distance, mais doit être sécurisé.\n\n✅ Risque si désactivé : Aucun risque direct de sécurité, mais limite les capacités de gestion à distance.\n\n🚨 Risque si activé et mal sécurisé : Peut être une porte d'entrée pour des attaquants s'il est exposé à Internet sans mesures de sécurité robustes (mots de passe faibles, MFA manquant, pas de VPN).",
                "smbv1" => "📖 Définition : SMBv1 est une ancienne version du protocole Server Message Block, utilisé pour le partage de fichiers, d'imprimantes et de ports série sur un réseau. Il est considéré comme obsolète et a été remplacé par des versions plus sécurisées (SMBv2, SMBv3).\n\n⚠️ Importance : Ne devrait plus être utilisé. Les versions plus récentes offrent de meilleures performances et une sécurité renforcée.\n\n✅ Risque si désactivé : Aucun risque, au contraire, c'est une bonne pratique de sécurité.\n\n🚨 Risque si activé : SMBv1 contient des vulnérabilités de sécurité connues (ex. WannaCry, EternalBlue) et est susceptible d'attaques par rançongiciel et autres exploits. Il est fortement recommandé de le désactiver.",
                "antivirus" => "📖 Définition : Un antivirus est un logiciel de protection qui détecte, bloque et supprime les logiciels malveillants (virus, trojans, ransomwares, etc.). Windows Defender est l'antivirus intégré à Windows et est activé par défaut.\n\n⚠️ Importance : Indispensable pour protéger votre ordinateur contre les menaces en ligne et les fichiers infectés.\n\n🚨 Risque si désactivé : Votre système est exposé aux malwares, aux rançongiciels et au vol de données. Gardez toujours un antivirus actif.",
                "pare-feu" => "📖 Définition : Le pare-feu Windows filtre le trafic réseau entrant et sortant selon des règles de sécurité. Il bloque les connexions non autorisées tout en autorisant les communications légitimes.\n\n⚠️ Importance : Essentiel pour bloquer les accès non sollicités depuis Internet ou le réseau local et pour limiter les programmes qui peuvent communiquer.\n\n🚨 Risque si désactivé : Votre ordinateur devient visible et accessible depuis le réseau sans protection, ce qui favorise les intrusions et les attaques.",
                
                // Performance
                "bottlenecks" or "bottleneck" => "Bottleneck (goulot d'étranglement) : Composant limitant les performances globales car plus lent ou saturé que les autres. Ex: CPU saturé limitant le GPU.",
                "ram pressure" => "Pression RAM : Indique que la mémoire est insuffisante, forçant Windows à utiliser le fichier d'échange (plus lent).",
                "cpu bound" => "CPU Bound : Le processeur est le facteur limitant des performances. Les autres composants attendent le CPU.",
                "disk saturation" => "Saturation disque : Le disque est le goulot d'étranglement. Peut indiquer un HDD lent ou un SSD saturé.",
                
                // GPU
                "température gpu" or "temp gpu" => "Température GPU : Température de la carte graphique. <75°C = Normal, 75-85°C = Élevée, >85°C = Critique (throttling possible).",
                "vram" or "vram totale" => "VRAM : Mémoire vidéo dédiée de la carte graphique. Différente de la RAM système. Utilisée pour les textures, rendus 3D, buffers vidéo, etc.",
                "vram dédiée utilisée" => "VRAM Dédiée : Mémoire GPU réellement utilisée à cet instant. Cette valeur correspond à ce qu'affiche le Gestionnaire des tâches sous 'Mémoire GPU dédiée'. C'est la mémoire physique de votre carte graphique en cours d'utilisation.",
                "vram allouée (commit)" => "VRAM Allouée/Committed : Mémoire réservée par les applications pour le GPU. Cette valeur peut être significativement plus élevée que la VRAM dédiée car elle inclut les allocations prévues, les buffers, et la mémoire partagée. Pour la valeur exacte de mémoire GPU utilisée, référez-vous au Gestionnaire des tâches ou GPU-Z.",
                "tdr" or "tdr 30j" or "tdr video" or "tdr (crashes gpu)" => 
                    "🎮 TDR (Timeout Detection and Recovery)\n\n" +
                    "📖 Définition : Mécanisme Windows qui détecte quand le pilote graphique ne répond plus et tente de le réinitialiser sans redémarrer le système.\n\n" +
                    "⚠️ Importance : Des TDR fréquents indiquent un problème avec le pilote graphique, une surchauffe GPU, un overclocking instable, ou un matériel défaillant.\n\n" +
                    "🚨 Risques : Écran noir temporaire, perte de travail non sauvegardé, et dans les cas graves, BSOD.\n\n" +
                    "🔧 Que faire : Mettez à jour le pilote graphique, vérifiez la température GPU, désactivez l'overclocking si présent, ou testez avec une autre carte graphique.",
                
                // CPU
                "température cpu" or "temp cpu" => "Température CPU : <70°C = Normal, 70-85°C = Élevée (surveiller), >85°C = Critique (throttling activé).",
                "throttling" => "Throttling : Réduction automatique des performances pour éviter la surchauffe. Indique un problème de refroidissement.",
                
                // Stockage
                "smart" or "santé smart" => "SMART : Système d'auto-surveillance des disques. Détecte les signes avant-coureurs de panne.",
                
                // Réseau (C3: définitions centralisées)
                "latence" or "ping" => "Latence (Ping) : Temps de réponse du réseau en millisecondes.\n\n<30ms = Excellent (jeux, visio)\n30-100ms = Correct (navigation)\n>100ms = Lent (problème réseau ou distance serveur)",
                "download" or "téléchargement" => "Download : Débit descendant (téléchargement).\n\n>100 Mbps = Fibre/Excellent\n25-100 Mbps = Bon\n<25 Mbps = Lent (ADSL ou problème)",
                "upload" or "envoi" => "Upload : Débit montant (envoi de fichiers, visioconférence).\n\n>50 Mbps = Excellent\n10-50 Mbps = Correct\n<10 Mbps = Peut limiter visio/streaming",
                "jitter" => "Jitter : Variation de la latence. Un jitter élevé (>30ms) cause des saccades en visio ou jeux en ligne.",
                "packet loss" or "perte de paquets" => "Perte de paquets : Pourcentage de données perdues en transit.\n\n0% = Parfait\n<1% = Acceptable\n>1% = Problème réseau (câble, Wi-Fi, congestion)",
                
                // Système (A1-A4: définitions pour nouveaux champs)
                "utilisateur" => "Nom d'utilisateur Windows connecté. Peut être masqué pour la confidentialité.",
                "organisation" => "Domaine ou organisation Windows (si l'ordinateur est joint à un domaine Active Directory).",
                "carte mère" or "motherboard" => "Carte mère : Composant principal reliant tous les autres composants (CPU, RAM, GPU, stockage).",
                "version bios" or "bios" => "BIOS/UEFI : Micrologiciel de démarrage.\n\nUne version récente corrige des failles de sécurité et améliore la compatibilité matérielle.",
                "date bios" => "Date de sortie de la version du BIOS. Un BIOS ancien (>3 ans) peut nécessiter une mise à jour.",
                
                // Pilotes
                "pilote" or "driver" => "Pilote : Logiciel permettant au système d'exploitation de communiquer avec le matériel.\n\nDes pilotes obsolètes peuvent causer des problèmes de stabilité ou de performance.",
                "date pilote" => "Date de mise à jour du pilote. Un pilote ancien (>2 ans) peut être mis à jour.",
                "non signés" => 
                    "⚙️ Pilotes non signés\n\n" +
                    "📖 Définition : Un pilote « non signé » n'est pas signé numériquement par une autorité reconnue (Microsoft, éditeur matériel). Windows peut le bloquer selon les réglages de stratégie (Signature Enforcement).\n\n" +
                    "⚠️ Ce n'est pas forcément malveillant mais plus risqué : sources non officielles, vieux drivers non mis à jour, possible compatibilité ou stabilité réduite.\n\n" +
                    "🔧 Actions : Privilégier les pilotes officiels (site fabricant, Windows Update), vérifier l'éditeur, éviter de désactiver les contrôles de sécurité (désactivation du mode test, etc.).",
                
                // Alimentation
                "power throttling" => 
                    "⚡ Power throttling (limitation de puissance)\n\n" +
                    "📖 Définition : Limitation volontaire de la puissance du CPU/GPU pour réduire la chaleur et la consommation. Le système réduit les fréquences ou l'utilisation pour rester dans des limites thermiques ou d'alimentation.\n\n" +
                    "📉 Impact : Baisse des performances, latence accrue, FPS réduits en jeu, tâches lourdes plus lentes.\n\n" +
                    "⚠️ Causes typiques : Mode économie d'énergie, limites thermiques atteintes, politiques OEM, chargeur insuffisant sur portable, paramètres Windows ou BIOS.\n\n" +
                    "🔧 Actions : Vérifier le mode d'alimentation (Performances élevées), pilotes chipset à jour, températures (nettoyage, pâte thermique), paramètres Windows (Options d'alimentation), BIOS si pertinent.",
                
                // Applications
                "applications" or "apps" => "Applications installées détectées via le registre Windows et les packages AppX.",
                
                _ => null
            };
        }
        
        /// <summary>Recommandations spécifiques à cette section</summary>
        public List<string> SectionRecommendations { get; set; } = new();
        
        /// <summary>Findings/problèmes détectés</summary>
        public List<HealthFinding> Findings { get; set; } = new();
        
        /// <summary>La section a-t-elle des données disponibles</summary>
        public bool HasData { get; set; } = true;
        
        /// <summary>Statut de collecte (OK, PARTIAL, FAILED)</summary>
        public string CollectionStatus { get; set; } = "OK";
    }

    /// <summary>
    /// Item de données avec info-bulle pour affichage UI
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
                return Key.Equals("Périph. audio", StringComparison.OrdinalIgnoreCase) ||
                       Key.Equals("Imprimantes", StringComparison.OrdinalIgnoreCase) ||
                       Key.Equals("Obsolètes", StringComparison.OrdinalIgnoreCase) ||
                       Key.Equals("Pilotes obsolètes", StringComparison.OrdinalIgnoreCase);
            }
        }
        
        /// <summary>
        /// Détermine si l'icône info "i" doit être affichée.
        /// On affiche le "i" UNIQUEMENT pour les termes techniques nécessitant une explication détaillée.
        /// Exclusions explicites: Antivirus, Température GPU, VRAM totale (trop évidents, pas besoin d'explication)
        /// </summary>
        public bool ShouldShowInfoIcon
        {
            get
            {
                if (!HasTooltip) return false;
                
                // Exclusions explicites - champs évidents qui n'ont pas besoin d'icône info
                if (Key.Equals("Antivirus", StringComparison.OrdinalIgnoreCase) ||
                    Key.Equals("Température GPU", StringComparison.OrdinalIgnoreCase) ||
                    Key.Contains("VRAM totale", StringComparison.OrdinalIgnoreCase) ||
                    Key.Contains("VRAM", StringComparison.OrdinalIgnoreCase))
                    return false;
                
                // Liste des clés qui DOIVENT afficher l'icône "i" (termes techniques nécessitant explication)
                var keysWithInfoIcon = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    // Stabilité système - termes techniques
                    "BSOD", "BSOD 30j", "Erreurs WHEA", "WHEA", "Kernel-Power", 
                    "Points de restauration", "Âge dernier point",
                    
                    // Sécurité - termes techniques
                    "BitLocker", "Secure Boot", "UAC", "RDP", "SMBv1",
                    
                    // GPU - termes techniques
                    "TDR", "TDR 30j", "TDR (crashes GPU)", "TDR video",
                    
                    // CPU - termes techniques  
                    "Throttling",
                    
                    // Pilotes - termes techniques
                    "Non signés",
                    
                    // Alimentation - termes techniques
                    "Power throttling",
                    
                    // Performance - termes techniques
                    "Bottlenecks", "Bottleneck", "RAM pressure", "CPU bound", "Disk saturation"
                };
                
                return keysWithInfoIcon.Contains(Key);
            }
        }
        
        /// <summary>
        /// Icône de statut basée sur la valeur (✓, ✗, ou rien)
        /// Affiche une coche/croix selon que la valeur indique un état positif/négatif
        /// </summary>
        public string StatusIcon
        {
            get
            {
                // Ne pas afficher d'indicateur (☑) pour ces champs — pas de ✅ blanc côté UI
                // Inclut: Sécurité, Alimentation, Pilotes, GPU (TDR, Temp), Stockage (Temp, SMART, Partitions), Réseau (Latence, Perte, Qualité), Stabilité (BSOD, WHEA)
                if (Key.Equals("Antivirus", StringComparison.OrdinalIgnoreCase) ||
                    Key.Equals("Pare-feu", StringComparison.OrdinalIgnoreCase) ||
                    Key.Equals("Secure Boot", StringComparison.OrdinalIgnoreCase) ||
                    Key.Equals("UAC", StringComparison.OrdinalIgnoreCase) ||
                    Key.Equals("Kernel-Power", StringComparison.OrdinalIgnoreCase) ||
                    Key.Equals("Power throttling", StringComparison.OrdinalIgnoreCase) ||
                    Key.Equals("Non signés", StringComparison.OrdinalIgnoreCase) ||
                    Key.StartsWith("Pilote ", StringComparison.OrdinalIgnoreCase) ||
                    // GPU
                    Key.Equals("Température GPU", StringComparison.OrdinalIgnoreCase) ||
                    Key.Contains("TDR", StringComparison.OrdinalIgnoreCase) ||
                    // Stockage
                    Key.Equals("Températures disques", StringComparison.OrdinalIgnoreCase) ||
                    Key.Equals("TempMax Disques", StringComparison.OrdinalIgnoreCase) ||
                    Key.Equals("Santé SMART", StringComparison.OrdinalIgnoreCase) ||
                    Key.Equals("Partitions", StringComparison.OrdinalIgnoreCase) ||
                    // Réseau
                    Key.Equals("Latence (ping)", StringComparison.OrdinalIgnoreCase) ||
                    Key.Equals("Perte paquets", StringComparison.OrdinalIgnoreCase) ||
                    Key.Equals("Qualité connexion", StringComparison.OrdinalIgnoreCase) ||
                    // Stabilité
                    Key.Equals("BSOD", StringComparison.OrdinalIgnoreCase) ||
                    Key.Equals("Erreurs WHEA", StringComparison.OrdinalIgnoreCase) ||
                    // OS (Redémarrage requis: pas d'icône; seuls ⚠️ et 🚨 sont utilisés pour avertissement/danger)
                    Key.Equals("Updates Windows", StringComparison.OrdinalIgnoreCase) ||
                    Key.Equals("Erreurs critiques", StringComparison.OrdinalIgnoreCase) ||
                    Key.Equals("Redémarrage requis", StringComparison.OrdinalIgnoreCase))
                    return "";

                if (string.IsNullOrEmpty(Value)) return "";
                var vLower = Value.ToLower();
                var v = vLower;
                // Données indisponibles / inconnu : pas d'emoji
                if (vLower.Contains("non disponible") || vLower.Contains("unavailable") ||
                    vLower.Contains("données manquantes") || vLower.Contains("échec") ||
                    vLower.Contains("echoue") || vLower.Contains("failed") ||
                    v.Contains("inconnu") || v.Contains("unknown") || v.Contains("non détect"))
                    return "";

                // Avertissement (⚠️ uniquement)
                if (Key.IndexOf("Throttling", StringComparison.OrdinalIgnoreCase) >= 0 && (v.Contains("détecté") || v.Contains("détect")))
                    return ""; // positif = pas d'icône
                if ((Key.IndexOf("BSOD", StringComparison.OrdinalIgnoreCase) >= 0 || Key.IndexOf("WHEA", StringComparison.OrdinalIgnoreCase) >= 0 || Key.IndexOf("Kernel-Power", StringComparison.OrdinalIgnoreCase) >= 0) &&
                    (v.Contains("crash") || v.Contains("événement") || v.Contains("jours")))
                    return "⚠";
                if (v.Contains("⚠️"))
                    return "⚠";

                // Danger (🚨 uniquement) : états négatifs / critiques
                if (v.Contains("❌") || (v.StartsWith("non") && !v.Contains("non détect")) || v.Contains("désactivé"))
                    return "🚨";

                // Tous les autres états (positifs, neutres) : pas d'emoji
                return "";
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
    /// Données scoreV2 du PowerShell - source de vérité pour le score
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
    /// Détail des pénalités par catégorie
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
    /// Information sur une pénalité spécifique
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
    /// Erreur rencontrée pendant le scan
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
    /// Modèle de confiance du score - coverage + cohérence
    /// </summary>
    public class ConfidenceModel
    {
        /// <summary>Score de confiance global 0-100</summary>
        public int ConfidenceScore { get; set; } = 100;
        
        /// <summary>Niveau de confiance textuel — calculé automatiquement depuis le score.
        /// ≥90 = Fiable, ≥70 = Moyen, &lt;70 = Faible.</summary>
        public string ConfidenceLevel
        {
            get => ConfidenceScore >= 90 ? "Fiable" : ConfidenceScore >= 70 ? "Moyen" : "Faible";
            set { /* setter conservé pour compatibilité JSON mais la valeur est toujours recalculée */ }
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
        
        /// <summary>Indique si le score est fiable (seuil : ≥70)</summary>
        public bool IsReliable => ConfidenceScore >= 70;
    }

    /// <summary>
    /// Traçabilité de la divergence entre score PS et score UDIS
    /// </summary>
    public class ScoreDivergence
    {
        /// <summary>Score original du PowerShell (scoreV2)</summary>
        public int PowerShellScore { get; set; }
        
        /// <summary>Grade original du PowerShell</summary>
        public string PowerShellGrade { get; set; } = "N/A";
        
        /// <summary>Score calculé par UDIS (legacy field name conservé pour compat JSON)</summary>
        public int GradeEngineScore { get; set; }
        
        /// <summary>Grade calculé par UDIS</summary>
        public string GradeEngineGrade { get; set; } = "N/A";
        
        /// <summary>Différence absolue entre les deux scores</summary>
        public int Delta => Math.Abs(GradeEngineScore - PowerShellScore);
        
        /// <summary>Indique si les deux scores sont cohérents (delta &lt;= 10)</summary>
        public bool IsCoherent => Delta <= 10;
        
        /// <summary>Explication de la divergence</summary>
        public string Explanation { get; set; } = "";
        
        /// <summary>Source de vérité utilisée pour l'affichage UI</summary>
        public string SourceOfTruth { get; set; } = "UDIS";
    }
}
