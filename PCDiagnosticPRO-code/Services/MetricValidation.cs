using System;
using System.Collections.Generic;
using System.Linq;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Services
{
    /// <summary>
    /// Statut de validité d'une métrique collectée
    /// </summary>
    public enum MetricValidity
    {
        /// <summary>Valeur valide et exploitable</summary>
        Valid,
        /// <summary>Valeur présente mais invalide (hors range, sentinelle)</summary>
        Invalid,
        /// <summary>Valeur absente ou non collectée</summary>
        Missing
    }

    /// <summary>
    /// Source de vérité pour une métrique (fusion PS vs C#)
    /// </summary>
    public enum MetricSource
    {
        PowerShell,
        CSharp,
        Derived, // Calculée depuis d'autres métriques
        Unknown
    }

    /// <summary>
    /// Résultat de validation d'une métrique
    /// </summary>
    public class ValidatedMetric<T>
    {
        public T? Value { get; set; }
        public MetricValidity Validity { get; set; } = MetricValidity.Missing;
        public MetricSource Source { get; set; } = MetricSource.Unknown;
        public string? Reason { get; set; }
        public string DisplayValue => GetDisplayValue();

        private string GetDisplayValue()
        {
            return Validity switch
            {
                MetricValidity.Valid => Value?.ToString() ?? "N/A",
                MetricValidity.Invalid => $"Non disponible ({Reason ?? "valeur invalide"})",
                MetricValidity.Missing => $"Non collecté ({Reason ?? "donnée absente"})",
                _ => "N/A"
            };
        }
    }

    /// <summary>
    /// Service de validation et normalisation des métriques collectées.
    /// Implémente les règles P0, P1, P2 pour la fusion PS/C#.
    /// </summary>
    public static class MetricValidation
    {
        #region Temperature Validation Ranges

        /// <summary>Plages valides pour les températures CPU (°C)</summary>
        public static readonly (double Min, double Max) CpuTempRange = (5.0, 120.0);
        
        /// <summary>Plages valides pour les températures GPU (°C)</summary>
        public static readonly (double Min, double Max) GpuTempRange = (10.0, 120.0);
        
        /// <summary>Plages valides pour les températures disque (°C)</summary>
        public static readonly (double Min, double Max) DiskTempRange = (0.0, 80.0);
        
        /// <summary>Valeurs sentinelles à rejeter</summary>
        public static readonly double[] SentinelValues = { -1, -999, 0, double.NaN, double.PositiveInfinity, double.NegativeInfinity };

        #endregion

        #region CPU Temperature Validation

        /// <summary>
        /// Valide une température CPU.
        /// RÈGLE P1: CPU temp == 0°C ou < 5°C = invalide
        /// </summary>
        public static ValidatedMetric<double> ValidateCpuTemp(MetricValue<double>? metric)
        {
            var result = new ValidatedMetric<double> { Source = MetricSource.CSharp };
            
            if (metric == null || !metric.Available)
            {
                result.Validity = MetricValidity.Missing;
                result.Reason = metric?.Reason ?? "Capteur non disponible";
                return result;
            }

            var temp = metric.Value;
            
            // Vérifier sentinelles
            if (IsSentinelValue(temp))
            {
                result.Validity = MetricValidity.Invalid;
                result.Reason = $"Valeur sentinelle détectée: {temp}";
                App.LogMessage($"[MetricValidation] CPU temp invalide: valeur sentinelle {temp}");
                return result;
            }
            
            // Vérifier plage
            if (temp < CpuTempRange.Min)
            {
                result.Validity = MetricValidity.Invalid;
                result.Reason = $"Température trop basse: {temp:F1}°C < {CpuTempRange.Min}°C";
                App.LogMessage($"[MetricValidation] CPU temp invalide: {temp:F1}°C < {CpuTempRange.Min}°C");
                return result;
            }
            
            if (temp > CpuTempRange.Max)
            {
                result.Validity = MetricValidity.Invalid;
                result.Reason = $"Température hors plage: {temp:F1}°C > {CpuTempRange.Max}°C";
                App.LogMessage($"[MetricValidation] CPU temp invalide: {temp:F1}°C > {CpuTempRange.Max}°C");
                return result;
            }
            
            result.Value = temp;
            result.Validity = MetricValidity.Valid;
            return result;
        }

        #endregion

        #region GPU Temperature Validation

        /// <summary>
        /// Valide une température GPU.
        /// RÈGLE P1: GPU temp > 120°C = invalide
        /// </summary>
        public static ValidatedMetric<double> ValidateGpuTemp(MetricValue<double>? metric)
        {
            var result = new ValidatedMetric<double> { Source = MetricSource.CSharp };
            
            if (metric == null || !metric.Available)
            {
                result.Validity = MetricValidity.Missing;
                result.Reason = metric?.Reason ?? "Capteur non disponible";
                return result;
            }

            var temp = metric.Value;
            
            if (IsSentinelValue(temp))
            {
                result.Validity = MetricValidity.Invalid;
                result.Reason = $"Valeur sentinelle détectée: {temp}";
                return result;
            }
            
            if (temp < GpuTempRange.Min || temp > GpuTempRange.Max)
            {
                result.Validity = MetricValidity.Invalid;
                result.Reason = $"Température hors plage: {temp:F1}°C (attendu {GpuTempRange.Min}-{GpuTempRange.Max}°C)";
                return result;
            }
            
            result.Value = temp;
            result.Validity = MetricValidity.Valid;
            return result;
        }

        #endregion

        #region VRAM Validation

        /// <summary>
        /// Valide la VRAM.
        /// RÈGLE P1: VRAM used > VRAM total = invalide
        /// </summary>
        public static ValidatedMetric<(double total, double used)> ValidateVram(
            MetricValue<double>? totalMB, MetricValue<double>? usedMB)
        {
            var result = new ValidatedMetric<(double, double)> { Source = MetricSource.CSharp };
            
            if (totalMB == null || !totalMB.Available || usedMB == null || !usedMB.Available)
            {
                result.Validity = MetricValidity.Missing;
                result.Reason = "VRAM total ou utilisée non disponible";
                return result;
            }

            var total = totalMB.Value;
            var used = usedMB.Value;
            
            if (IsSentinelValue(total) || IsSentinelValue(used))
            {
                result.Validity = MetricValidity.Invalid;
                result.Reason = "Valeur sentinelle détectée";
                return result;
            }
            
            if (total <= 0)
            {
                result.Validity = MetricValidity.Invalid;
                result.Reason = $"VRAM total invalide: {total} MB";
                return result;
            }
            
            if (used > total * 1.1) // 10% tolérance pour fluctuations
            {
                result.Validity = MetricValidity.Invalid;
                result.Reason = $"VRAM used ({used:F0} MB) > total ({total:F0} MB)";
                return result;
            }
            
            result.Value = (total, used);
            result.Validity = MetricValidity.Valid;
            return result;
        }

        #endregion

        #region Performance Counter Validation

        /// <summary>
        /// Normalise une valeur de performance counter.
        /// BLOC 4: Détecte sentinelles (-1, NaN, Infinity, null, valeurs absurdes)
        /// </summary>
        public static ValidatedMetric<double> ValidatePerfCounter(double? value, string counterName)
        {
            var result = new ValidatedMetric<double> { Source = MetricSource.PowerShell };
            
            if (!value.HasValue)
            {
                result.Validity = MetricValidity.Missing;
                result.Reason = $"Counter '{counterName}' non collecté";
                return result;
            }
            
            var v = value.Value;
            
            // Sentinelles courantes dans les perf counters
            if (IsSentinelValue(v))
            {
                result.Validity = MetricValidity.Invalid;
                result.Reason = $"Counter '{counterName}' valeur sentinelle: {v}";
                App.LogMessage($"[MetricValidation] PerfCounter '{counterName}' sentinelle: {v}");
                return result;
            }
            
            // Valeurs absurdes pour certains counters
            if (counterName.Contains("Queue", StringComparison.OrdinalIgnoreCase) && (v < 0 || v > 1000))
            {
                result.Validity = MetricValidity.Invalid;
                result.Reason = $"Queue length absurde: {v}";
                return result;
            }
            
            if (counterName.Contains("Percent", StringComparison.OrdinalIgnoreCase) && (v < 0 || v > 100))
            {
                result.Validity = MetricValidity.Invalid;
                result.Reason = $"Pourcentage hors plage: {v}%";
                return result;
            }
            
            result.Value = v;
            result.Validity = MetricValidity.Valid;
            return result;
        }

        #endregion

        #region Merge Policy (C# overrides PS for sensor metrics)

        /// <summary>
        /// RÈGLE P0: Fusionne une métrique PS et C# selon la politique:
        /// - Si C# a une valeur VALIDE → utiliser C#
        /// - Sinon si PS a une valeur → utiliser PS
        /// - Sinon → Missing
        /// </summary>
        public static ValidatedMetric<double> MergeSensorMetric(
            ValidatedMetric<double>? csharpMetric,
            double? psValue,
            string metricName)
        {
            // Priorité 1: C# valide
            if (csharpMetric != null && csharpMetric.Validity == MetricValidity.Valid)
            {
                App.LogMessage($"[MergePolicy] {metricName}: C# valide ({csharpMetric.Value}), ignore PS");
                return csharpMetric;
            }
            
            // Priorité 2: PS disponible (si C# invalide/missing)
            if (psValue.HasValue && !IsSentinelValue(psValue.Value))
            {
                var psMetric = new ValidatedMetric<double>
                {
                    Value = psValue.Value,
                    Validity = MetricValidity.Valid,
                    Source = MetricSource.PowerShell
                };
                App.LogMessage($"[MergePolicy] {metricName}: Fallback PS ({psValue.Value})");
                return psMetric;
            }
            
            // Priorité 3: Retourner le C# même si invalide (avec raison)
            if (csharpMetric != null)
            {
                App.LogMessage($"[MergePolicy] {metricName}: C# invalide ({csharpMetric.Reason}), pas de PS fallback");
                return csharpMetric;
            }
            
            // Rien disponible
            return new ValidatedMetric<double>
            {
                Validity = MetricValidity.Missing,
                Reason = $"Aucune source disponible pour {metricName}"
            };
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Vérifie si une valeur est une sentinelle à rejeter
        /// </summary>
        public static bool IsSentinelValue(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return true;
            
            foreach (var sentinel in SentinelValues)
            {
                if (!double.IsNaN(sentinel) && Math.Abs(value - sentinel) < 0.001)
                    return true;
            }
            
            return false;
        }

        /// <summary>
        /// Convertit un MetricValue en ValidatedMetric avec validation complète
        /// </summary>
        public static ValidatedMetric<double> FromMetricValue(MetricValue<double>? metric, Func<MetricValue<double>?, ValidatedMetric<double>> validator)
        {
            return validator(metric);
        }

        #endregion
    }

    /// <summary>
    /// Résultat de collecte avec diagnostic
    /// </summary>
    public class CollectionDiagnostics
    {
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public List<string> MissingData { get; set; } = new();
        public List<string> InvalidMetrics { get; set; } = new();
        
        /// <summary>Statut global de la collecte</summary>
        public string CollectionStatus => 
            Errors.Count > 0 ? "ÉCHOUÉE" :
            (Warnings.Count > 0 || InvalidMetrics.Count > 0) ? "PARTIELLE" : "COMPLÈTE";

        public void AddFromPsErrors(List<ScanErrorInfo>? psErrors)
        {
            if (psErrors == null) return;
            foreach (var err in psErrors)
            {
                Errors.Add($"[{err.Code}] {err.Section}: {err.Message}");
            }
        }

        public void AddFromPsMissingData(List<string>? missing)
        {
            if (missing == null) return;
            MissingData.AddRange(missing);
        }

        public void AddInvalidMetric(string metricName, string reason)
        {
            InvalidMetrics.Add($"{metricName}: {reason}");
        }
    }
}
