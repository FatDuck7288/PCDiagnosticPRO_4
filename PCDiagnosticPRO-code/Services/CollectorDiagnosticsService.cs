using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// PHASE 3 & 4: Service de diagnostic de la collecte.
    /// Calcule CollectorErrorsLogical, gère le Confidence gating,
    /// et parse missingData/topPenalties de façon flexible.
    /// </summary>
    public static class CollectorDiagnosticsService
    {
        #region Collector Diagnostics Model

        public class CollectorDiagnosticsResult
        {
            /// <summary>Nombre d'erreurs logiques calculées (sans modifier le JSON source)</summary>
            public int CollectorErrorsLogical { get; set; }
            
            /// <summary>Erreurs PS extraites</summary>
            public List<ScanErrorInfo> Errors { get; set; } = new();
            
            /// <summary>Données manquantes normalisées (array de strings)</summary>
            public List<string> MissingDataNormalized { get; set; } = new();
            
            /// <summary>Top penalties normalisées</summary>
            public List<PenaltyInfo> TopPenaltiesNormalized { get; set; } = new();
            
            /// <summary>Métriques invalidées par DataSanitizer</summary>
            public List<string> InvalidatedMetrics { get; set; } = new();
            
            /// <summary>Statut global de la collecte</summary>
            public string CollectionStatus { get; set; } = "OK";
            
            /// <summary>Message détaillé pour l'UI</summary>
            public string StatusMessage { get; set; } = "";
            
            /// <summary>Est-ce que la collecte est considérée comme échouée?</summary>
            public bool IsCollectionFailed => CollectorErrorsLogical > 0 || CollectionStatus == "FAILED";
            
            /// <summary>Est-ce que la collecte est partielle?</summary>
            public bool IsPartial => MissingDataNormalized.Count > 0 || InvalidatedMetrics.Count > 0;
        }

        #endregion

        #region Main Analysis Method

        /// <summary>
        /// Analyse complète des diagnostics de collecte depuis le JSON PS brut.
        /// </summary>
        public static CollectorDiagnosticsResult Analyze(JsonElement root, HardwareSensorsResult? sensors)
        {
            var result = new CollectorDiagnosticsResult();
            
            // 1. Extraire les erreurs PS
            result.Errors = ExtractErrors(root);
            
            // 2. Parser missingData de façon flexible (PHASE 4.1)
            result.MissingDataNormalized = ExtractMissingDataFlexible(root);
            
            // 3. Parser topPenalties de façon flexible (PHASE 4.2)
            result.TopPenaltiesNormalized = ExtractTopPenaltiesFlexible(root);
            
            // 4. Appliquer DataSanitizer aux capteurs
            if (sensors != null)
            {
                result.InvalidatedMetrics = DataSanitizer.SanitizeSensors(sensors);
            }
            
            // 5. Calculer CollectorErrorsLogical (PHASE 3.1)
            result.CollectorErrorsLogical = CalculateCollectorErrorsLogical(result);
            
            // 6. Déterminer le statut
            result.CollectionStatus = DetermineCollectionStatus(result);
            result.StatusMessage = GenerateStatusMessage(result);
            
            App.LogMessage($"[CollectorDiagnostics] Errors={result.Errors.Count}, MissingData={result.MissingDataNormalized.Count}, " +
                          $"InvalidMetrics={result.InvalidatedMetrics.Count}, CollectorErrorsLogical={result.CollectorErrorsLogical}, " +
                          $"Status={result.CollectionStatus}");
            
            return result;
        }

        #endregion

        #region PHASE 3.1: CollectorErrorsLogical Calculation

        /// <summary>
        /// PHASE 3.1: Calcule CollectorErrorsLogical sans modifier le JSON source.
        /// Si errors[] non vide => collectorErrorsLogical >= count(errors)
        /// Si TXT indique "statut collecte échouée" => collectorErrorsLogical >= 1
        /// </summary>
        private static int CalculateCollectorErrorsLogical(CollectorDiagnosticsResult result)
        {
            // P0.1: collectorErrorsLogical = (errors?.Count ?? 0) + métriques invalidées (sans double comptage)
            int count = result.Errors.Count + result.InvalidatedMetrics.Count;
            return count;
        }

        #endregion

        #region PHASE 4.1: Flexible missingData Parsing

        /// <summary>
        /// PHASE 4.1: Parse missingData de façon flexible.
        /// Supporte:
        /// a) array de strings: ["ProcessList", "ExternalNetTests"]
        /// b) objet { key: reason }: { "ProcessList": "disabled", "ExternalNetTests": false }
        /// c) objet { key: true/false }: { "ProcessList": true }
        /// Convertit en liste de strings normalisée.
        /// </summary>
        private static List<string> ExtractMissingDataFlexible(JsonElement root)
        {
            var missing = new List<string>();
            
            // FIX: Guard against non-Object root before TryGetProperty
            if (root.ValueKind != JsonValueKind.Object)
            {
                App.LogMessage($"[CollectorDiagnostics] missingData: root is not Object (is {root.ValueKind})");
                return missing;
            }
            
            if (!root.TryGetProperty("missingData", out var mdElement))
            {
                App.LogMessage("[CollectorDiagnostics] missingData: propriété absente");
                return missing;
            }
            
            switch (mdElement.ValueKind)
            {
                case JsonValueKind.Array:
                    // Format a) array de strings
                    foreach (var item in mdElement.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            var val = item.GetString();
                            if (!string.IsNullOrEmpty(val))
                                missing.Add(val);
                        }
                        else if (item.ValueKind == JsonValueKind.Object)
                        {
                            // Array d'objets - extraire les clés
                            foreach (var prop in item.EnumerateObject())
                            {
                                missing.Add($"{prop.Name}: {GetPropertyValueAsString(prop.Value)}");
                            }
                        }
                    }
                    App.LogMessage($"[CollectorDiagnostics] missingData: array avec {missing.Count} éléments");
                    break;
                    
                case JsonValueKind.Object:
                    // Format b) ou c) objet { key: value }
                    foreach (var prop in mdElement.EnumerateObject())
                    {
                        var key = prop.Name;
                        var value = prop.Value;
                        
                        string normalized;
                        switch (value.ValueKind)
                        {
                            case JsonValueKind.String:
                                normalized = $"{key}: {value.GetString()}";
                                break;
                            case JsonValueKind.True:
                                normalized = $"{key}: missing";
                                break;
                            case JsonValueKind.False:
                                normalized = $"{key}: disabled";
                                break;
                            case JsonValueKind.Number:
                                normalized = $"{key}: {value.GetDouble()}";
                                break;
                            default:
                                normalized = $"{key}: {value.GetRawText()}";
                                break;
                        }
                        
                        missing.Add(normalized);
                    }
                    App.LogMessage($"[CollectorDiagnostics] missingData: OBJECT converti en {missing.Count} strings");
                    break;
                    
                default:
                    App.LogMessage($"[CollectorDiagnostics] missingData: type inattendu {mdElement.ValueKind}");
                    break;
            }
            
            return missing;
        }

        #endregion

        #region PHASE 4.2: Flexible topPenalties Parsing

        /// <summary>
        /// PHASE 4.2: Parse topPenalties de façon flexible.
        /// Supporte:
        /// - {} comme liste vide
        /// - [] comme liste vide
        /// - [{ source, penalty, msg, type }] comme liste normale
        /// - { key: value } => convertir en liste si possible
        /// </summary>
        private static List<PenaltyInfo> ExtractTopPenaltiesFlexible(JsonElement root)
        {
            var penalties = new List<PenaltyInfo>();
            
            // FIX: Guard against non-Object root before TryGetProperty
            if (root.ValueKind != JsonValueKind.Object)
                return penalties;
            
            // Chercher dans scoreV2.topPenalties
            if (!root.TryGetProperty("scoreV2", out var scoreV2))
            {
                return penalties;
            }
            
            // FIX: Guard against non-Object scoreV2 before TryGetProperty
            if (scoreV2.ValueKind != JsonValueKind.Object)
                return penalties;
            
            if (!scoreV2.TryGetProperty("topPenalties", out var tpElement))
            {
                return penalties;
            }
            
            switch (tpElement.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in tpElement.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            var penalty = new PenaltyInfo();
                            if (item.TryGetProperty("source", out var s)) penalty.Source = s.GetString() ?? "";
                            if (item.TryGetProperty("penalty", out var p)) penalty.Penalty = p.GetInt32();
                            if (item.TryGetProperty("msg", out var m)) penalty.Message = m.GetString() ?? "";
                            if (item.TryGetProperty("message", out var m2)) penalty.Message = m2.GetString() ?? "";
                            if (item.TryGetProperty("type", out var t)) penalty.Type = t.GetString() ?? "";
                            penalties.Add(penalty);
                        }
                    }
                    break;
                    
                case JsonValueKind.Object:
                    // {} vide ou objet à convertir
                    int propCount = 0;
                    foreach (var prop in tpElement.EnumerateObject())
                    {
                        propCount++;
                        // Essayer de convertir chaque propriété en penalty
                        var penalty = new PenaltyInfo { Source = prop.Name };
                        
                        if (prop.Value.ValueKind == JsonValueKind.Number)
                        {
                            penalty.Penalty = prop.Value.GetInt32();
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            if (prop.Value.TryGetProperty("penalty", out var p)) penalty.Penalty = p.GetInt32();
                            if (prop.Value.TryGetProperty("msg", out var m)) penalty.Message = m.GetString() ?? "";
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            penalty.Message = prop.Value.GetString() ?? "";
                        }
                        
                        penalties.Add(penalty);
                    }
                    
                    if (propCount == 0)
                    {
                        App.LogMessage("[CollectorDiagnostics] topPenalties: {} vide (traité comme liste vide)");
                    }
                    else
                    {
                        App.LogMessage($"[CollectorDiagnostics] topPenalties: OBJECT converti en {penalties.Count} penalties");
                    }
                    break;
                    
                default:
                    App.LogMessage($"[CollectorDiagnostics] topPenalties: type inattendu {tpElement.ValueKind}");
                    break;
            }
            
            return penalties;
        }

        #endregion

        #region PHASE 3.2: Confidence Gating

        /// <summary>
        /// PHASE 3.2: Applique le confidence gating.
        /// FIX #9: Seuils plus tolérants pour éviter le score figé à 70.
        /// - Si collectorErrorsLogical > 5 => ConfidenceScore plafonné à 70
        /// - Si collectorErrorsLogical > 0 => ConfidenceScore plafonné à 85 (plus permissif)
        /// - Si missingData critique => ConfidenceScore plafonné à 75.
        /// </summary>
        public static int ApplyConfidenceGating(int baseConfidence, CollectorDiagnosticsResult diagnostics)
        {
            int confidence = baseConfidence;
            
            // FIX #9: Gate 1 - Seuil plus tolérant pour erreurs collecteur
            if (diagnostics.CollectorErrorsLogical > 5)
            {
                confidence = Math.Min(confidence, 70);
                App.LogMessage($"[ConfidenceGating] Capped to 70 due to {diagnostics.CollectorErrorsLogical} collector errors (>5)");
            }
            else if (diagnostics.CollectorErrorsLogical > 0)
            {
                confidence = Math.Min(confidence, 85); // FIX #9: Plafond moins restrictif pour 1-5 erreurs
                App.LogMessage($"[ConfidenceGating] Capped to 85 due to {diagnostics.CollectorErrorsLogical} collector errors (1-5)");
            }
            
            // Gate 2: Données manquantes critiques
            var criticalMissing = diagnostics.MissingDataNormalized.Count(m => 
                m.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("Memory", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("Disk", StringComparison.OrdinalIgnoreCase));
            
            if (criticalMissing > 0)
            {
                confidence = Math.Min(confidence, 75);
                App.LogMessage($"[ConfidenceGating] Capped to 75 due to {criticalMissing} critical missing data");
            }
            
            // Gate 3: Métriques invalidées
            if (diagnostics.InvalidatedMetrics.Count > 2)
            {
                confidence = Math.Min(confidence, 65);
                App.LogMessage($"[ConfidenceGating] Capped to 65 due to {diagnostics.InvalidatedMetrics.Count} invalidated metrics");
            }
            
            return confidence;
        }

        #endregion

        #region Helpers

        private static List<ScanErrorInfo> ExtractErrors(JsonElement root)
        {
            var errors = new List<ScanErrorInfo>();
            
            // FIX: Guard against non-Object root before TryGetProperty
            if (root.ValueKind != JsonValueKind.Object)
                return errors;
            
            if (root.TryGetProperty("errors", out var errArray) && errArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var err in errArray.EnumerateArray())
                {
                    // FIX: Only process if err is an Object
                    if (err.ValueKind != JsonValueKind.Object)
                        continue;
                    
                    var error = new ScanErrorInfo();
                    if (err.TryGetProperty("code", out var c)) error.Code = c.GetString() ?? "";
                    if (err.TryGetProperty("message", out var m)) error.Message = m.GetString() ?? "";
                    if (err.TryGetProperty("section", out var s)) error.Section = s.GetString() ?? "";
                    if (err.TryGetProperty("exceptionType", out var e)) error.ExceptionType = e.GetString() ?? "";
                    errors.Add(error);
                }
            }
            
            return errors;
        }

        private static string DetermineCollectionStatus(CollectorDiagnosticsResult result)
        {
            if (result.CollectorErrorsLogical > 3)
                return "FAILED";
            if (result.CollectorErrorsLogical > 0 || result.MissingDataNormalized.Count > 0 || result.InvalidatedMetrics.Count > 0)
                return "PARTIAL";
            return "OK";
        }

        private static string GenerateStatusMessage(CollectorDiagnosticsResult result)
        {
            return result.CollectionStatus switch
            {
                "FAILED" => $"Collecte échouée ({result.CollectorErrorsLogical} erreurs)",
                "PARTIAL" => $"Collecte partielle ({result.MissingDataNormalized.Count} données manquantes, {result.InvalidatedMetrics.Count} métriques invalides)",
                _ => "Collecte complète"
            };
        }

        private static string GetPropertyValueAsString(JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.GetDouble().ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "null",
                _ => value.GetRawText()
            };
        }

        #endregion
    }
}
