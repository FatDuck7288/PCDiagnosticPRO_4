using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// BLOC 6: Helper pour la transparence de la section Sécurité.
    /// Distingue clairement ce qui est couvert vs ce qui ne l'est pas.
    /// </summary>
    public static class SecurityTransparencyHelper
    {
        /// <summary>
        /// Résumé de la couverture sécurité
        /// </summary>
        public class SecurityCoverage
        {
            // Ce qui EST couvert
            public bool DefenderStatusCollected { get; set; }
            public bool DefenderRealTimeProtection { get; set; }
            public bool FirewallStatusCollected { get; set; }
            public bool FirewallEnabled { get; set; }
            public bool UacStatusCollected { get; set; }
            public int UacLevel { get; set; }
            public bool SecureBootCollected { get; set; }
            public bool SecureBootEnabled { get; set; }
            public bool BitLockerCollected { get; set; }
            public bool BitLockerEnabled { get; set; }
            public bool AsrRulesCollected { get; set; }
            public int AsrRulesCount { get; set; }
            
            // Menaces détectées (si disponible)
            public int ThreatHistoryCount { get; set; }
            public List<string> PuaDetections { get; set; } = new();
            public List<string> VulnerableDrivers { get; set; } = new();
            
            // Ce qui N'EST PAS couvert
            public List<string> NotCovered { get; set; } = new()
            {
                "Scan antivirus actif (pas de scan AV lancé)",
                "Détection EDR/XDR tiers",
                "Analyse comportementale malware",
                "Audit des permissions fichiers",
                "Vérification certificats système"
            };
            
            // Avertissements
            public List<string> Warnings { get; set; } = new();
        }

        /// <summary>
        /// Parse la section Security du JSON PS
        /// </summary>
        public static SecurityCoverage ParseSecurityData(JsonElement? psData)
        {
            var coverage = new SecurityCoverage();
            
            if (!psData.HasValue) return coverage;
            
            try
            {
                // Chercher Security dans sections ou racine
                JsonElement secElement = default;
                bool found = false;
                
                if (psData.Value.TryGetProperty("sections", out var sections) &&
                    sections.TryGetProperty("Security", out var sec))
                {
                    secElement = sec;
                    found = true;
                }
                else if (psData.Value.TryGetProperty("Security", out var secDirect))
                {
                    secElement = secDirect;
                    found = true;
                }
                
                if (!found) return coverage;
                
                JsonElement data = secElement;
                if (secElement.TryGetProperty("data", out var dataElem))
                    data = dataElem;
                
                // Defender
                if (data.TryGetProperty("defender", out var defender))
                {
                    coverage.DefenderStatusCollected = true;
                    if (defender.TryGetProperty("realTimeProtection", out var rtp))
                        coverage.DefenderRealTimeProtection = rtp.GetBoolean();
                    if (defender.TryGetProperty("enabled", out var en))
                        coverage.DefenderRealTimeProtection = en.GetBoolean();
                }
                
                // Firewall
                if (data.TryGetProperty("firewall", out var fw))
                {
                    coverage.FirewallStatusCollected = true;
                    if (fw.TryGetProperty("enabled", out var fwEn))
                        coverage.FirewallEnabled = fwEn.GetBoolean();
                    else if (fw.TryGetProperty("domainProfile", out var dp) && dp.TryGetProperty("enabled", out var dpEn))
                        coverage.FirewallEnabled = dpEn.GetBoolean();
                }
                
                // UAC
                if (data.TryGetProperty("uac", out var uac))
                {
                    coverage.UacStatusCollected = true;
                    if (uac.TryGetProperty("level", out var level))
                        coverage.UacLevel = level.GetInt32();
                    else if (uac.TryGetProperty("enabled", out var uacEn))
                        coverage.UacLevel = uacEn.GetBoolean() ? 1 : 0;
                }
                
                // SecureBoot
                if (data.TryGetProperty("secureBoot", out var sb))
                {
                    coverage.SecureBootCollected = true;
                    if (sb.TryGetProperty("enabled", out var sbEn))
                        coverage.SecureBootEnabled = sbEn.GetBoolean();
                }
                
                // BitLocker
                if (data.TryGetProperty("bitLocker", out var bl))
                {
                    coverage.BitLockerCollected = true;
                    if (bl.TryGetProperty("enabled", out var blEn))
                        coverage.BitLockerEnabled = blEn.GetBoolean();
                    else if (bl.TryGetProperty("protectionStatus", out var ps))
                        coverage.BitLockerEnabled = ps.GetString()?.Contains("On", StringComparison.OrdinalIgnoreCase) == true;
                }
                
                // ASR Rules
                if (data.TryGetProperty("asrRules", out var asr) && asr.ValueKind == JsonValueKind.Array)
                {
                    coverage.AsrRulesCollected = true;
                    coverage.AsrRulesCount = asr.GetArrayLength();
                }
                
                // Threat History
                if (data.TryGetProperty("threatHistory", out var th) && th.ValueKind == JsonValueKind.Array)
                {
                    coverage.ThreatHistoryCount = th.GetArrayLength();
                    
                    foreach (var threat in th.EnumerateArray())
                    {
                        var name = "";
                        if (threat.TryGetProperty("threatName", out var tn))
                            name = tn.GetString() ?? "";
                        else if (threat.TryGetProperty("name", out var n))
                            name = n.GetString() ?? "";
                        
                        if (string.IsNullOrEmpty(name)) continue;
                        
                        // Catégoriser
                        if (name.StartsWith("PUA", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Bundler", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("PUADlManager", StringComparison.OrdinalIgnoreCase))
                        {
                            coverage.PuaDetections.Add(name);
                        }
                        else if (name.Contains("VulnerableDriver", StringComparison.OrdinalIgnoreCase))
                        {
                            coverage.VulnerableDrivers.Add(name);
                        }
                    }
                }
                
                // Générer avertissements
                if (!coverage.DefenderRealTimeProtection)
                    coverage.Warnings.Add("Protection temps réel Defender désactivée");
                if (!coverage.FirewallEnabled)
                    coverage.Warnings.Add("Pare-feu désactivé");
                if (coverage.UacLevel == 0)
                    coverage.Warnings.Add("UAC désactivé ou niveau minimal");
                if (!coverage.SecureBootEnabled && coverage.SecureBootCollected)
                    coverage.Warnings.Add("Secure Boot désactivé");
                if (coverage.VulnerableDrivers.Count > 0)
                    coverage.Warnings.Add($"{coverage.VulnerableDrivers.Count} driver(s) vulnérable(s) détecté(s)");
            }
            catch (Exception ex)
            {
                App.LogMessage($"[SecurityTransparency] Erreur parsing: {ex.Message}");
            }
            
            return coverage;
        }

        /// <summary>
        /// Génère la section TXT de transparence sécurité
        /// </summary>
        public static void WriteSecurityTransparencySection(System.Text.StringBuilder sb, SecurityCoverage coverage)
        {
            sb.AppendLine("  ┌─ SÉCURITÉ : NIVEAU DE VISIBILITÉ ─────────────────────────────────────────┐");
            sb.AppendLine();
            
            // Ce qui est couvert
            sb.AppendLine("  │  ✓ CE QUI EST VÉRIFIÉ:");
            sb.AppendLine($"  │    • Windows Defender   : {(coverage.DefenderStatusCollected ? (coverage.DefenderRealTimeProtection ? "✅ Actif" : "⚠️ Inactif") : "❓ Non collecté")}");
            sb.AppendLine($"  │    • Pare-feu Windows   : {(coverage.FirewallStatusCollected ? (coverage.FirewallEnabled ? "✅ Actif" : "⚠️ Inactif") : "❓ Non collecté")}");
            sb.AppendLine($"  │    • UAC                : {(coverage.UacStatusCollected ? $"Niveau {coverage.UacLevel}" : "❓ Non collecté")}");
            sb.AppendLine($"  │    • Secure Boot        : {(coverage.SecureBootCollected ? (coverage.SecureBootEnabled ? "✅ Activé" : "⚠️ Désactivé") : "❓ Non collecté")}");
            sb.AppendLine($"  │    • BitLocker          : {(coverage.BitLockerCollected ? (coverage.BitLockerEnabled ? "✅ Activé" : "○ Non activé") : "❓ Non collecté")}");
            
            if (coverage.AsrRulesCollected)
                sb.AppendLine($"  │    • Règles ASR         : {coverage.AsrRulesCount} règle(s)");
            
            sb.AppendLine();
            
            // Menaces détectées
            if (coverage.ThreatHistoryCount > 0)
            {
                sb.AppendLine($"  │  ⚠️ HISTORIQUE MENACES: {coverage.ThreatHistoryCount} élément(s)");
                
                if (coverage.PuaDetections.Count > 0)
                {
                    sb.AppendLine($"  │    • PUA/Bundlers: {coverage.PuaDetections.Count}");
                    foreach (var pua in coverage.PuaDetections.Take(3))
                        sb.AppendLine($"  │      - {pua}");
                    if (coverage.PuaDetections.Count > 3)
                        sb.AppendLine($"  │      ... et {coverage.PuaDetections.Count - 3} autres");
                }
                
                if (coverage.VulnerableDrivers.Count > 0)
                {
                    sb.AppendLine($"  │    • Drivers vulnérables: {coverage.VulnerableDrivers.Count}");
                    foreach (var vd in coverage.VulnerableDrivers.Take(3))
                        sb.AppendLine($"  │      - {vd}");
                }
                
                sb.AppendLine();
            }
            
            // Ce qui N'EST PAS couvert
            sb.AppendLine("  │  ✗ CE QUI N'EST PAS COUVERT:");
            foreach (var notCovered in coverage.NotCovered)
            {
                sb.AppendLine($"  │    • {notCovered}");
            }
            sb.AppendLine();
            
            // Avertissements
            if (coverage.Warnings.Count > 0)
            {
                sb.AppendLine("  │  ⚠️ AVERTISSEMENTS:");
                foreach (var warn in coverage.Warnings)
                {
                    sb.AppendLine($"  │    • {warn}");
                }
            }
            
            sb.AppendLine("  └─────────────────────────────────────────────────────────────────────────────┘");
        }
    }
}
