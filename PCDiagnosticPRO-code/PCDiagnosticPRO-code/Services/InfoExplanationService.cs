using System;
using System.Collections.Generic;
using System.Globalization;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    public sealed class InfoExplanationService
    {
        public IReadOnlyList<InfoLine> BuildInfoLines(InfoContext ctx)
        {
            if (ctx == null)
                throw new ArgumentNullException(nameof(ctx));

            var rawLines = ctx.ContextId switch
            {
                InfoContextId.DiskTemp => BuildDiskTemperature(ctx),
                InfoContextId.TDR => BuildTdr(ctx),
                InfoContextId.VRAM => BuildVram(ctx),
                InfoContextId.CPUThrottle => BuildCpuThrottle(ctx),
                InfoContextId.CPUTemperature => BuildCpuTemperature(ctx),
                InfoContextId.SMARTHealth => BuildSmartHealth(ctx),
                InfoContextId.WHEA => BuildWhea(ctx),
                InfoContextId.KernelPower => BuildKernelPower(ctx),
                InfoContextId.RestorePoints => BuildRestorePoints(ctx),
                InfoContextId.RebootRequired => BuildRebootRequired(ctx),
                InfoContextId.UpdatesPending => BuildUpdatesPending(ctx),
                InfoContextId.BSOD => BuildBsod(ctx),
                InfoContextId.NetworkLoss => BuildNetworkLoss(ctx),
                InfoContextId.GPULoad => BuildGpuLoad(ctx),
                InfoContextId.SecurityAntivirus => BuildSecurityAntivirus(ctx),
                InfoContextId.SecurityFirewall => BuildSecurityFirewall(ctx),
                InfoContextId.SecuritySecureBoot => BuildSecuritySecureBoot(ctx),
                InfoContextId.SecurityBitLocker => BuildSecurityBitLocker(ctx),
                InfoContextId.SecurityUac => BuildSecurityUac(ctx),
                InfoContextId.SecuritySmbV1 => BuildSecuritySmbV1(ctx),
                InfoContextId.SecurityTamperProtection => BuildSecurityTamperProtection(ctx),
                InfoContextId.SecurityRealTimeProtection => BuildSecurityRealTimeProtection(ctx),
                InfoContextId.SecurityVbs => BuildSecurityVbs(ctx),
                InfoContextId.SecurityCredentialGuard => BuildSecurityCredentialGuard(ctx),
                InfoContextId.SecurityMemoryIntegrity => BuildSecurityMemoryIntegrity(ctx),
                InfoContextId.SecurityAsr => BuildSecurityAsr(ctx),
                _ => BuildGeneric(ctx)
            };

            return NormalizeLines(rawLines);
        }

        private static IReadOnlyList<InfoLine> BuildDiskTemperature(InfoContext ctx)
        {
            var temp = ReadDouble(ctx);
            var title = temp.HasValue
                ? ctx.Severity switch
                {
                    InfoSeverity.Danger => $"Température disque : niveau critique ({temp.Value:F0} °C)",
                    InfoSeverity.Warning => $"Température disque : vigilance requise ({temp.Value:F0} °C)",
                    _ => $"Température disque : état normal ({temp.Value:F0} °C)"
                }
                : "Température disque : information partielle";

            return ctx.Severity switch
            {
                InfoSeverity.Danger => BuildRows(
                    title,
                    "Le disque fonctionne au-dessus de la plage thermique recommandée.",
                    "Une chaleur prolongée accélère la dégradation des composants du disque.",
                    "Risque de corruption de données, d'instabilité et de panne brutale.",
                    "Sauvegarde immédiate, réduire la charge, vérifier ventilation et santé du disque.",
                    InfoSeverity.Danger),

                InfoSeverity.Warning => BuildRows(
                    title,
                    "La température mesurée est au-dessus de la plage optimale pour un usage continu.",
                    "Le disque reste utilisable, mais l'usure peut augmenter si cette situation persiste.",
                    "Risque d'usure accélérée et de performances dégradées à moyen terme.",
                    "Améliorer le flux d'air, nettoyer le boîtier et vérifier le fonctionnement des ventilateurs.",
                    InfoSeverity.Warning),

                _ => BuildRows(
                    title,
                    "Mesure thermique actuelle d'un SSD/HDD pendant l'activité.",
                    "La plage observée est saine pour la fiabilité du stockage.",
                    "Risque faible à ce niveau de température.",
                    "Continuer la surveillance périodique.",
                    InfoSeverity.Info)
            };
        }

        private static IReadOnlyList<InfoLine> BuildTdr(InfoContext ctx)
        {
            var count = ReadEventCount(ctx);
            var title = count switch
            {
                >= 3 => $"Stabilité GPU : TDR fréquents détectés ({count} événements)",
                >= 1 => $"Stabilité GPU : TDR récents détectés ({count} événement{(count == 1 ? string.Empty : "s")})",
                _ => "Stabilité GPU : aucun TDR récent détecté"
            };

            if (count <= 0)
            {
                return BuildRows(
                    title,
                    "TDR est le mécanisme Windows qui réinitialise le pilote graphique s'il ne répond plus.",
                    "Ce mécanisme évite un redémarrage immédiat de la machine en cas de blocage ponctuel.",
                    "Risque actuel faible car aucun événement récent n'est observé.",
                    "Conserver des pilotes GPU à jour et maintenir un refroidissement correct.",
                    InfoSeverity.Info);
            }

            if (count <= 2)
            {
                return BuildRows(
                    title,
                    "Le pilote graphique a cessé de répondre puis a été réinitialisé par Windows.",
                    "Quelques événements peuvent signaler une instabilité logicielle ou thermique ponctuelle.",
                    "Risque de saccades, écran noir temporaire et perte de travail non sauvegardé.",
                    "Vérifier pilote GPU, températures et paramètres d'overclocking.",
                    InfoSeverity.Warning);
            }

            return BuildRows(
                title,
                "Des blocages répétés du pilote graphique sont détectés sur la période récente.",
                "La fréquence observée traduit une instabilité élevée qui dépasse un incident isolé.",
                "Risque élevé de crash applicatif, BSOD et interruption de session.",
                "Contrôler PSU et températures, puis effectuer une réinstallation propre du pilote GPU.",
                InfoSeverity.Danger);
        }

        private static IReadOnlyList<InfoLine> BuildVram(InfoContext ctx)
        {
            var pct = ReadDouble(ctx);
            var title = pct.HasValue
                ? $"Utilisation VRAM : {pct.Value:F0}%"
                : "Utilisation VRAM : mesure partielle";

            if (!pct.HasValue)
            {
                return BuildRows(
                    title,
                    "La VRAM est la mémoire vidéo dédiée utilisée par le GPU.",
                    "Elle conditionne la fluidité sur textures lourdes et hautes résolutions.",
                    "Risque non quantifiable sans pourcentage fiable.",
                    "Refaire une mesure en charge graphique stable.",
                    InfoSeverity.Warning);
            }

            if (pct.Value < 70)
            {
                return BuildRows(
                    title,
                    "La VRAM est la mémoire vidéo dédiée active pendant le rendu.",
                    "Le niveau actuel laisse une marge confortable.",
                    "Risque faible de saturation mémoire GPU.",
                    "Aucune action immédiate, surveillance normale.",
                    InfoSeverity.Info);
            }

            if (pct.Value <= 90)
            {
                return BuildRows(
                    title,
                    "La mémoire vidéo approche une zone de pression.",
                    "La marge restante se réduit sur scènes lourdes.",
                    "Risque de micro-saccades et variabilité de FPS.",
                    "Fermer les applications graphiques secondaires et surveiller la charge.",
                    InfoSeverity.Warning);
            }

            return BuildRows(
                title,
                "La VRAM dédiée est proche de la saturation.",
                "Le GPU dispose de très peu de marge mémoire.",
                "Risque de stutter marqué, crash applicatif ou erreur de rendu.",
                "Réduire résolution/textures et fermer les applications concurrentes.",
                InfoSeverity.Danger);
        }

        private static IReadOnlyList<InfoLine> BuildCpuTemperature(InfoContext ctx)
        {
            var temp = ReadDouble(ctx);
            var raw = TextEncodingNormalizer.Normalize(ctx.Value?.ToString() ?? string.Empty);
            var title = temp.HasValue
                ? $"Temperature CPU : {temp.Value:F0}C"
                : "Temperature CPU : indisponible";

            if (temp.HasValue)
            {
                if (temp.Value >= 90)
                {
                    return BuildRows(
                        title,
                        "La temperature CPU correspond a la chaleur mesuree sur le package ou les coeurs.",
                        "Un niveau tres eleve degrade la stabilite et force le throttling.",
                        "Risque de baisse de performances, saccades CPU-bound et arret de protection thermique.",
                        "Verifier refroidissement, ventilateurs, poussiere et pate thermique.",
                        InfoSeverity.Danger);
                }

                if (temp.Value >= 80)
                {
                    return BuildRows(
                        title,
                        "La temperature CPU est mesuree en charge reelle.",
                        "Le niveau actuel est eleve mais reste gerable a court terme.",
                        "Risque de throttling ponctuel si la charge augmente.",
                        "Ameliorer le flux d'air et surveiller l'evolution sous charge continue.",
                        InfoSeverity.Warning);
                }

                return BuildRows(
                    title,
                    "La temperature CPU est mesuree en direct par le pipeline capteurs.",
                    "Le niveau observe est coherent avec un fonctionnement sain.",
                    "Risque faible de limitation thermique immediate.",
                    "Aucune action urgente, poursuivre la surveillance normale.",
                    InfoSeverity.Info);
            }

            var snapshot = CpuTemperatureMetadataService.GetLastUiSnapshot();
            var reasonCode = CpuTemperatureMetadataService.NormalizeReasonCode(snapshot.ReasonCode);
            var sourceHint = reasonCode switch
            {
                CpuTemperatureMetadataService.ReasonBlockedBySecurity => "Les capteurs semblent bloques par les protections systeme.",
                CpuTemperatureMetadataService.ReasonNotSupported => "Ce materiel/firmware n'expose pas de capteur CPU exploitable.",
                CpuTemperatureMetadataService.ReasonNoSensors => "Aucun capteur CPU n'a pu etre lu sur cette machine.",
                CpuTemperatureMetadataService.ReasonAccessDenied => "L'acces aux capteurs CPU est refuse par Windows.",
                CpuTemperatureMetadataService.ReasonError => "La lecture capteur a rencontre une erreur technique.",
                _ when raw.Contains("bloque par la securite", StringComparison.OrdinalIgnoreCase) => "Les capteurs semblent bloques par les protections systeme.",
                _ when raw.Contains("capteur non pris en charge", StringComparison.OrdinalIgnoreCase) => "Ce materiel/firmware n'expose pas de capteur CPU exploitable.",
                _ when raw.Contains("acces refuse", StringComparison.OrdinalIgnoreCase) => "L'acces aux capteurs CPU est refuse par Windows.",
                _ when raw.Contains("aucun capteur", StringComparison.OrdinalIgnoreCase) => "Aucun capteur CPU n'a pu etre lu sur cette machine.",
                _ => "Aucun capteur CPU n'a pu etre lu sur cette machine."
            };

            var detailLine =
                $"Details capteurs avances: source={snapshot.Source}, confiance={snapshot.Confidence}, raison={reasonCode}" +
                (string.IsNullOrWhiteSpace(snapshot.ReasonDetail) ? string.Empty : $", detail technique={snapshot.ReasonDetail}");

            return BuildRows(
                title,
                "La temperature CPU indique la marge thermique disponible pour maintenir les performances.",
                "Elle aide a diagnostiquer la cause des ralentissements et du throttling.",
                "Sans mesure fiable, le diagnostic thermique reste partiel.",
                $"Source de l'indisponibilite : {sourceHint} {detailLine}. Le pipeline tente d'abord LHM puis ACPI ThermalZone.",
                InfoSeverity.Warning);
        }
        private static IReadOnlyList<InfoLine> BuildCpuThrottle(InfoContext ctx)
        {
            var detected = ReadBoolean(ctx);
            var title = detected == true
                ? "CPU : throttling détecté"
                : detected == false
                    ? "CPU : throttling non détecté"
                    : "CPU : état de throttling indéterminé";

            if (detected == true)
            {
                if (ctx.Severity == InfoSeverity.Danger)
                {
                    return BuildRows(
                        title,
                        "Le throttling réduit automatiquement la fréquence CPU pour respecter des limites thermiques ou de puissance.",
                        "La répétition du phénomène indique une contrainte forte qui impacte durablement les performances.",
                        "Risque élevé de chute de performances, saccades CPU-bound et instabilité sous charge.",
                        "Réduire immédiatement la charge, vérifier refroidissement/alim et appliquer un profil d'alimentation adapté.",
                        InfoSeverity.Danger);
                }

                return BuildRows(
                    title,
                    "Le throttling réduit automatiquement la fréquence CPU pour respecter des limites thermiques ou de puissance.",
                    "Ce mécanisme protège le matériel mais diminue les performances sous charge.",
                    "Risque de baisse durable de performances, latence accrue et instabilité perçue.",
                    "Vérifier refroidissement, poussière, pâte thermique et plan d'alimentation.",
                    InfoSeverity.Warning);
            }

            if (detected == false)
            {
                return BuildRows(
                    title,
                    "Aucune limitation CPU significative n'est observée pendant la collecte.",
                    "Le comportement actuel est cohérent avec un fonctionnement thermique normal.",
                    "Risque faible de perte de performance liée à la température ou à la puissance.",
                    "Conserver une ventilation correcte et un profil d'alimentation adapté.",
                    InfoSeverity.Info);
            }

            return BuildRows(
                title,
                "Le statut de throttling n'a pas pu être confirmé avec certitude.",
                "Une mesure incomplète peut masquer une limitation intermittente.",
                "Risque modéré de diagnostic incomplet.",
                "Refaire une mesure pendant une charge CPU soutenue.",
                InfoSeverity.Warning);
        }

        private static IReadOnlyList<InfoLine> BuildSmartHealth(InfoContext ctx)
        {
            var title = ctx.Severity switch
            {
                InfoSeverity.Danger => "Stockage : alerte SMART critique",
                InfoSeverity.Warning => "Stockage : avertissement SMART",
                _ => "Stockage : état SMART sain"
            };

            return ctx.Severity switch
            {
                InfoSeverity.Danger => BuildRows(
                    title,
                    "SMART est le système d'auto-surveillance intégré aux disques.",
                    "Une alerte critique indique un risque élevé de défaillance matérielle.",
                    "Risque de perte de données et de panne soudaine.",
                    "Sauvegarder immédiatement et planifier le remplacement du disque.",
                    InfoSeverity.Danger),

                InfoSeverity.Warning => BuildRows(
                    title,
                    "SMART signale des indicateurs de santé en dégradation.",
                    "La fiabilité peut rester acceptable à court terme mais doit être surveillée.",
                    "Risque d'erreurs disque croissantes si la tendance continue.",
                    "Vérifier les attributs SMART et préparer une sauvegarde proactive.",
                    InfoSeverity.Warning),

                _ => BuildRows(
                    title,
                    "SMART vérifie des indicateurs internes de fiabilité du disque.",
                    "Aucun indicateur critique n'est remonté actuellement.",
                    "Risque faible à court terme.",
                    "Maintenir la sauvegarde régulière et la surveillance standard.",
                    InfoSeverity.Info)
            };
        }

        private static IReadOnlyList<InfoLine> BuildWhea(InfoContext ctx)
        {
            var count = ReadEventCount(ctx);
            var title = count > 0
                ? $"Stabilité matériel : erreurs WHEA détectées ({count})"
                : "Stabilité matériel : aucune erreur WHEA récente";

            return count > 0
                ? BuildRows(
                    title,
                    "WHEA regroupe les erreurs matérielles remontées par Windows.",
                    "Ces événements peuvent révéler un problème CPU, RAM, bus PCIe ou alimentation.",
                    "Risque de plantages, corruption de données et dégradation progressive.",
                    "Tester RAM, vérifier températures et stabilité d'alimentation.",
                    count >= 3 ? InfoSeverity.Danger : InfoSeverity.Warning)
                : BuildRows(
                    title,
                    "Aucun signal d'erreur matérielle WHEA n'est remonté.",
                    "Le système ne présente pas d'anomalie matérielle explicite sur cette métrique.",
                    "Risque faible sur ce point précis.",
                    "Conserver la surveillance lors des prochaines charges lourdes.",
                    InfoSeverity.Info);
        }

        private static IReadOnlyList<InfoLine> BuildKernelPower(InfoContext ctx)
        {
            var count = ReadEventCount(ctx);
            var title = count > 0
                ? $"Stabilité système : événements Kernel-Power ({count})"
                : "Stabilité système : aucun événement Kernel-Power récent";

            return count > 0
                ? BuildRows(
                    title,
                    "Kernel-Power signale un arrêt ou redémarrage inattendu.",
                    "La répétition de ces événements indique un défaut de stabilité à investiguer.",
                    "Risque de coupure brutale, perte de travail et corruption de session.",
                    "Vérifier alimentation, températures, pilotes critiques et journal système.",
                    count >= 3 ? InfoSeverity.Danger : InfoSeverity.Warning)
                : BuildRows(
                    title,
                    "Aucun redémarrage brutal récent n'est observé via cette métrique.",
                    "Le comportement d'alimentation paraît stable sur la période.",
                    "Risque faible de coupure non planifiée à court terme.",
                    "Maintenir les contrôles de température et de mise à jour pilote.",
                    InfoSeverity.Info);
        }

        private static IReadOnlyList<InfoLine> BuildRestorePoints(InfoContext ctx)
        {
            var count = ReadEventCount(ctx);
            var title = ctx.Confidence == InfoConfidence.None
                ? "Points de restauration : indisponible"
                : $"Points de restauration : {count}";

            if (ctx.Confidence == InfoConfidence.None)
            {
                return BuildRows(
                    title,
                    "Un point de restauration est un instantané système pour revenir en arrière après un incident.",
                    "Il permet de réduire le risque lors des mises à jour pilotes, changements registre et installations sensibles.",
                    "Sans donnée fiable, l'état de protection du système est incertain.",
                    "Relancer en administrateur pour vérifier ou créer un point de restauration.",
                    InfoSeverity.Warning);
            }

            if (count == 0)
            {
                return BuildRows(
                    title,
                    "Aucun point actif n'a été trouvé sur la machine.",
                    "En cas d'instabilité, il n'existe pas de retour système immédiat prêt à l'emploi.",
                    "Risque de restauration impossible après une mise à jour problématique.",
                    "Créer un point maintenant et vérifier que la protection système est activée.",
                    InfoSeverity.Warning);
            }

            return BuildRows(
                title,
                "Les points de restauration enregistrent l'état système pour rollback.",
                "Ils améliorent la résilience lors des changements logiciels et pilotes.",
                "Risque modéré si les points sont anciens ou trop peu nombreux.",
                "Maintenir des points réguliers avant toute modification importante.",
                InfoSeverity.Info);
        }

        private static IReadOnlyList<InfoLine> BuildSecurityAntivirus(InfoContext ctx) =>
            BuildSecurityRows(
                "Antivirus",
                "L'antivirus détecte et bloque les malwares connus et comportements suspects.",
                "C'est la première barrière contre les menaces courantes.",
                "S'il est désactivé, la machine est exposée aux exécutions malveillantes.",
                "Activer la protection, mettre les signatures à jour et lancer un scan.",
                ctx);

        private static IReadOnlyList<InfoLine> BuildSecurityFirewall(InfoContext ctx) =>
            BuildSecurityRows(
                "Pare-feu",
                "Le pare-feu contrôle les connexions entrantes/sortantes selon des règles.",
                "Il limite l'exposition réseau et les accès non autorisés.",
                "S'il est inactif, les services locaux peuvent être atteints depuis le réseau.",
                "Activer le pare-feu sur tous les profils et auditer les règles ouvertes.",
                ctx);

        private static IReadOnlyList<InfoLine> BuildSecuritySecureBoot(InfoContext ctx) =>
            BuildSecurityRows(
                "Secure Boot",
                "Secure Boot bloque le démarrage de chargeurs non signés.",
                "Il réduit les risques de bootkits et compromissions pré-OS.",
                "Désactivé, l'amorçage est plus vulnérable aux attaques persistantes.",
                "Activer Secure Boot dans l'UEFI si le système est compatible.",
                ctx);

        private static IReadOnlyList<InfoLine> BuildSecurityBitLocker(InfoContext ctx) =>
            BuildSecurityRows(
                "BitLocker",
                "BitLocker chiffre les volumes pour protéger les données au repos.",
                "Le chiffrement protège les données en cas de vol ou perte du poste.",
                "Sans chiffrement, les données sont lisibles hors session Windows.",
                "Activer BitLocker ou Device Encryption et sauvegarder la clé de récupération.",
                ctx);

        private static IReadOnlyList<InfoLine> BuildSecurityUac(InfoContext ctx) =>
            BuildSecurityRows(
                "UAC",
                "L'UAC limite les élévations silencieuses de privilèges administrateur.",
                "Il empêche l'exécution non consentie d'actions système sensibles.",
                "Désactivé, les modifications critiques peuvent être appliquées plus facilement par malware.",
                "Réactiver l'UAC avec un niveau de notification adapté.",
                ctx);

        private static IReadOnlyList<InfoLine> BuildSecuritySmbV1(InfoContext ctx) =>
            BuildSecurityRows(
                "SMBv1",
                "SMBv1 est un protocole de partage de fichiers ancien et obsolète.",
                "Ce contrôle vérifie s'il est encore activé, car il doit rester désactivé.",
                "Activé, il augmente fortement la surface d'attaque réseau.",
                "Désactiver SMBv1 sur client/serveur et conserver SMBv2/SMBv3 uniquement.",
                ctx);

        private static IReadOnlyList<InfoLine> BuildSecurityTamperProtection(InfoContext ctx) =>
            BuildSecurityRows(
                "Tamper Protection",
                "La protection contre altération empêche la désactivation non autorisée de Defender.",
                "Elle bloque les modifications malveillantes des réglages de sécurité.",
                "Désactivée, les protections peuvent être neutralisées plus facilement.",
                "Activer Tamper Protection dans la sécurité Windows.",
                ctx);

        private static IReadOnlyList<InfoLine> BuildSecurityRealTimeProtection(InfoContext ctx) =>
            BuildSecurityRows(
                "Protection en temps réel",
                "La protection temps réel inspecte les fichiers/processus à l'exécution.",
                "Elle bloque les menaces au moment de l'activité, avant propagation.",
                "Désactivée, les malwares peuvent s'exécuter sans interception immédiate.",
                "Réactiver Defender RTP et vérifier l'état des services de sécurité.",
                ctx);

        private static IReadOnlyList<InfoLine> BuildSecurityVbs(InfoContext ctx) =>
            BuildSecurityRows(
                "VBS",
                "VBS isole des composants de sécurité via virtualisation matérielle.",
                "Il renforce l'isolation des secrets et protections du noyau.",
                "Désactivé, certaines défenses avancées ne sont pas disponibles.",
                "Activer VBS/virtualisation firmware et vérifier compatibilité pilotes.",
                ctx);

        private static IReadOnlyList<InfoLine> BuildSecurityCredentialGuard(InfoContext ctx) =>
            BuildSecurityRows(
                "Credential Guard",
                "Credential Guard isole les secrets d'authentification dans un environnement protégé.",
                "Il réduit le risque de vol d'identifiants en mémoire.",
                "Désactivé, les attaques de type credential dumping sont plus probables.",
                "Activer Credential Guard via stratégie sécurité/VBS.",
                ctx);

        private static IReadOnlyList<InfoLine> BuildSecurityMemoryIntegrity(InfoContext ctx) =>
            BuildSecurityRows(
                "Intégrité mémoire",
                "L'intégrité mémoire (HVCI) valide les pilotes/code noyau avant chargement.",
                "Elle empêche l'injection de code noyau non fiable.",
                "Désactivée, le risque d'attaque noyau via pilote augmente.",
                "Activer Core Isolation > Intégrité mémoire et corriger les pilotes incompatibles.",
                ctx);

        private static IReadOnlyList<InfoLine> BuildSecurityAsr(InfoContext ctx) =>
            BuildSecurityRows(
                "Règles ASR",
                "Les règles ASR bloquent des comportements d'attaque connus (Office, scripts, LOLBins).",
                "Elles complètent l'antivirus sur des vecteurs modernes d'intrusion.",
                "Absentes/inactives, certaines techniques de post-exploitation passent plus facilement.",
                "Activer les règles ASR critiques en mode blocage ou audit progressif.",
                ctx);

        private static IReadOnlyList<InfoLine> BuildRebootRequired(InfoContext ctx)
        {
            var reboot = ReadBoolean(ctx);
            var title = reboot == true
                ? "Maintenance système : redémarrage requis"
                : "Maintenance système : aucun redémarrage requis";

            return reboot == true
                ? BuildRows(
                    title,
                    "Windows attend un redémarrage pour finaliser des changements système.",
                    "Les correctifs ou pilotes peuvent rester partiellement appliqués tant que le redémarrage n'est pas fait.",
                    "Risque d'incohérence logicielle et de comportement instable.",
                    "Planifier un redémarrage complet dès que possible.",
                    InfoSeverity.Warning)
                : BuildRows(
                    title,
                    "Aucune opération système en attente de redémarrage n'est détectée.",
                    "L'état de maintenance est cohérent pour cette métrique.",
                    "Risque faible sur ce point.",
                    "Poursuivre les mises à jour régulières.",
                    InfoSeverity.Info);
        }

        private static IReadOnlyList<InfoLine> BuildUpdatesPending(InfoContext ctx)
        {
            var count = ReadEventCount(ctx);
            var title = count > 0
                ? $"Mises à jour : {count} en attente"
                : "Mises à jour : système à jour";

            if (count <= 0)
            {
                return BuildRows(
                    title,
                    "Aucune mise à jour en attente n'est détectée.",
                    "Le niveau de correctifs est aligné avec l'état actuel du système.",
                    "Risque réduit d'exposition à des failles connues.",
                    "Conserver la vérification périodique des mises à jour.",
                    InfoSeverity.Info);
            }

            if (count < 10)
            {
                return BuildRows(
                    title,
                    "Des mises à jour sont disponibles mais non encore appliquées.",
                    "Reporter trop longtemps les correctifs peut dégrader sécurité et stabilité.",
                    "Risque modéré de vulnérabilité et d'incompatibilité logicielle.",
                    "Installer les mises à jour dans une fenêtre de maintenance proche.",
                    InfoSeverity.Warning);
            }

            return BuildRows(
                title,
                "Un volume important de correctifs reste en attente.",
                "Le retard cumulé augmente le risque fonctionnel et sécurité.",
                "Risque élevé de faille non corrigée et d'instabilité applicative.",
                "Prioriser l'installation des mises à jour puis redémarrer.",
                InfoSeverity.Danger);
        }

        private static IReadOnlyList<InfoLine> BuildBsod(InfoContext ctx)
        {
            var count = ReadEventCount(ctx);
            var title = count > 0
                ? $"Stabilité système : BSOD détectés ({count})"
                : "Stabilité système : aucun BSOD récent";

            return count > 0
                ? BuildRows(
                    title,
                    "Un BSOD est un arrêt critique Windows lié à une erreur système ou pilote.",
                    "Des événements récurrents signalent une instabilité structurelle à traiter.",
                    "Risque de perte de données non sauvegardées et redémarrages forcés.",
                    "Analyser les minidumps, vérifier pilotes récents et tester RAM.",
                    count >= 3 ? InfoSeverity.Danger : InfoSeverity.Warning)
                : BuildRows(
                    title,
                    "Aucun crash critique de type écran bleu n'est observé.",
                    "Le niveau de stabilité perçu est correct sur cet indicateur.",
                    "Risque faible de crash noyau immédiat.",
                    "Maintenir les pilotes à jour et conserver des points de restauration.",
                    InfoSeverity.Info);
        }

        private static IReadOnlyList<InfoLine> BuildNetworkLoss(InfoContext ctx)
        {
            var loss = ReadDouble(ctx);
            var title = loss.HasValue
                ? $"Réseau : perte de paquets {loss.Value:F1}%"
                : "Réseau : perte de paquets non mesurée";

            if (!loss.HasValue || loss.Value <= 1)
            {
                return BuildRows(
                    title,
                    "La perte de paquets mesure les données non reçues à destination.",
                    "Un niveau très bas garantit une communication stable.",
                    "Risque faible de coupure audio/vidéo ou retransmission excessive.",
                    "Aucune action immédiate, conserver la surveillance.",
                    InfoSeverity.Info);
            }

            if (loss.Value <= 5)
            {
                return BuildRows(
                    title,
                    "Le lien réseau présente des pertes intermittentes.",
                    "Cela peut dégrader jeux, visioconférence et transferts en temps réel.",
                    "Risque de saccades, latence variable et baisse de qualité.",
                    "Contrôler Wi-Fi/câblage et réduire la congestion réseau locale.",
                    InfoSeverity.Warning);
            }

            return BuildRows(
                title,
                "Le taux de perte est élevé et impacte fortement la qualité de transmission.",
                "La connexion devient instable pour les usages sensibles à la latence.",
                "Risque de déconnexions, coupures audio/vidéo et timeouts applicatifs.",
                "Tester via Ethernet, vérifier routeur/modem et analyser interférences.",
                InfoSeverity.Danger);
        }

        private static IReadOnlyList<InfoLine> BuildGpuLoad(InfoContext ctx)
        {
            var load = ReadDouble(ctx);
            var title = load.HasValue
                ? $"Charge GPU : {load.Value:F0}%"
                : "Charge GPU : mesure partielle";

            if (!load.HasValue || load.Value < 80)
            {
                return BuildRows(
                    title,
                    "La charge GPU représente l'utilisation des unités de calcul graphique.",
                    "Le niveau observé reste compatible avec un fonctionnement normal.",
                    "Risque faible de saturation GPU immédiate.",
                    "Aucune action urgente, continuer la surveillance en charge réelle.",
                    InfoSeverity.Info);
            }

            if (load.Value < 95)
            {
                return BuildRows(
                    title,
                    "Le GPU travaille à un niveau élevé sur la charge courante.",
                    "Une charge soutenue est normale en jeu ou rendu intensif.",
                    "Risque modéré de chauffe et de baisse de marge de performance.",
                    "Vérifier température GPU et courbe de ventilation.",
                    InfoSeverity.Warning);
            }

            return BuildRows(
                title,
                "Le GPU est proche de la saturation continue.",
                "La marge de performance résiduelle devient très faible.",
                "Risque de stutter, baisse de FPS et instabilité sous charge prolongée.",
                "Réduire paramètres graphiques lourds et surveiller températures.",
                InfoSeverity.Danger);
        }

        private static IReadOnlyList<InfoLine> BuildGeneric(InfoContext ctx)
        {
            var label = string.IsNullOrWhiteSpace(ctx.MetricLabel) ? "Métrique système" : ctx.MetricLabel;
            return BuildRows(
                $"Information contextuelle : {label}",
                "Cette métrique décrit un aspect de l'état actuel de la machine.",
                "Son interprétation dépend de la charge et du contexte d'utilisation.",
                "Un écart durable peut annoncer une dégradation de stabilité ou de performance.",
                "Comparer la tendance dans le temps et agir si la valeur se dégrade.",
                ctx.Severity);
        }

        private static IReadOnlyList<InfoLine> BuildSecurityRows(
            string title,
            string definition,
            string importance,
            string risks,
            string actions,
            InfoContext ctx)
        {
            var effectiveSeverity = ctx.Confidence == InfoConfidence.None ? InfoSeverity.Warning : ctx.Severity;
            var finalTitle = effectiveSeverity switch
            {
                InfoSeverity.Danger => $"{title} : risque élevé",
                InfoSeverity.Warning => $"{title} : vigilance",
                _ => $"{title} : état conforme"
            };

            return BuildRows(finalTitle, definition, importance, risks, actions, effectiveSeverity);
        }

        private static IReadOnlyList<InfoLine> BuildRows(
            string title,
            string definition,
            string importance,
            string risks,
            string actions,
            InfoSeverity severity)
        {
            return new List<InfoLine>
            {
                new() { Emoji = "🔧", Text = Sanitize(title), Tone = ToPrimaryTone(severity) },
                new() { Emoji = "📄", Label = "Définition", Text = Sanitize(definition), Tone = InfoTone.Info },
                new() { Emoji = "💡", Label = "Importance", Text = Sanitize(importance), Tone = InfoTone.Info },
                new() { Emoji = "⚠️", Label = "Risques", Text = Sanitize(risks), Tone = severity == InfoSeverity.Danger ? InfoTone.Danger : InfoTone.Warning },
                new() { Emoji = "🛠", Label = "Actions", Text = Sanitize(actions), Tone = InfoTone.Action }
            };
        }

        private static IReadOnlyList<InfoLine> NormalizeLines(IReadOnlyList<InfoLine> lines)
        {
            if (lines == null || lines.Count == 0)
                return Array.Empty<InfoLine>();

            var normalized = new List<InfoLine>(lines.Count);
            foreach (var line in lines)
            {
                if (line == null)
                    continue;

                var emoji = TextEncodingNormalizer.Normalize(line.Emoji);
                if (string.IsNullOrWhiteSpace(emoji))
                    emoji = "•";

                normalized.Add(new InfoLine
                {
                    Emoji = emoji,
                    Label = TextEncodingNormalizer.Normalize(line.Label),
                    Text = TextEncodingNormalizer.Normalize(line.Text),
                    Tone = line.Tone
                });
            }

            return normalized;
        }

        private static InfoTone ToPrimaryTone(InfoSeverity severity)
        {
            return severity switch
            {
                InfoSeverity.Danger => InfoTone.Danger,
                InfoSeverity.Warning => InfoTone.Warning,
                _ => InfoTone.Info
            };
        }

        private static string Sanitize(string value) =>
            TextEncodingNormalizer.Normalize(value.Replace("\r", " ").Replace("\n", " ").Trim());

        private static double? ReadDouble(InfoContext ctx)
        {
            if (ctx.Value is double d)
                return d;
            if (ctx.Value is float f)
                return f;
            if (ctx.Value is int i)
                return i;

            var text = ctx.Value?.ToString();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var normalized = text.Replace(',', '.');
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static int ReadEventCount(InfoContext ctx)
        {
            if (ctx.Evidence.EventCount.HasValue)
                return ctx.Evidence.EventCount.Value;

            var numeric = ReadDouble(ctx);
            return numeric.HasValue ? (int)Math.Round(numeric.Value) : 0;
        }

        private static bool? ReadBoolean(InfoContext ctx)
        {
            if (ctx.Value is bool b)
                return b;

            var s = ctx.Value?.ToString();
            if (string.IsNullOrWhiteSpace(s))
                return null;

            var normalized = TextEncodingNormalizer.Normalize(s).Trim().ToLowerInvariant();
            if (normalized.Contains("oui", StringComparison.Ordinal) ||
                normalized.Contains("yes", StringComparison.Ordinal) ||
                normalized.Contains("détecté", StringComparison.Ordinal) ||
                normalized.Contains("detecte", StringComparison.Ordinal) ||
                normalized.Contains("activé", StringComparison.Ordinal) ||
                normalized.Contains("active", StringComparison.Ordinal))
            {
                return true;
            }

            if (normalized.Contains("non", StringComparison.Ordinal) ||
                normalized.Contains("no", StringComparison.Ordinal) ||
                normalized.Contains("désactivé", StringComparison.Ordinal) ||
                normalized.Contains("desactive", StringComparison.Ordinal) ||
                normalized.Contains("non détecté", StringComparison.Ordinal) ||
                normalized.Contains("non detecte", StringComparison.Ordinal))
            {
                return false;
            }

            return null;
        }
    }
}

