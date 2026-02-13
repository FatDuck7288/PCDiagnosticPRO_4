using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PCDiagnosticPro.Models;
using PCDiagnosticPro.ViewModels;

namespace PCDiagnosticPro.Converters
{
    /// <summary>
    /// Convertit un statut en couleur
    /// </summary>
    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ScanSeverity severity)
            {
                return severity switch
                {
                    ScanSeverity.OK => new SolidColorBrush(Color.FromRgb(46, 213, 115)),      // Vert
                    ScanSeverity.Info => new SolidColorBrush(Color.FromRgb(55, 66, 250)),     // Bleu
                    ScanSeverity.Warning => new SolidColorBrush(Color.FromRgb(255, 165, 2)), // Orange
                    ScanSeverity.Error => new SolidColorBrush(Color.FromRgb(255, 71, 87)),   // Rouge
                    ScanSeverity.Critical => new SolidColorBrush(Color.FromRgb(255, 0, 0)),  // Rouge vif
                    _ => new SolidColorBrush(Color.FromRgb(139, 148, 158))                    // Gris
                };
            }

            if (value is string statusText)
            {
                return statusText.ToUpper() switch
                {
                    "OK" or "ACTIF" or "CONNECTÉ" or "À JOUR" => new SolidColorBrush(Color.FromRgb(46, 213, 115)),
                    "INFO" => new SolidColorBrush(Color.FromRgb(55, 66, 250)),
                    "WARN" or "ATTENTION" or "ÉLEVÉ" or "ÉLEVÉE" => new SolidColorBrush(Color.FromRgb(255, 165, 2)),
                    "FAIL" or "ERREUR" or "CRITIQUE" or "INACTIF" or "DÉCONNECTÉ" => new SolidColorBrush(Color.FromRgb(255, 71, 87)),
                    _ => new SolidColorBrush(Color.FromRgb(139, 148, 158))
                };
            }

            return new SolidColorBrush(Color.FromRgb(139, 148, 158));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convertit un booléen en visibilité
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value is bool boolValue)
                {
                    bool invert = parameter?.ToString()?.ToLower() == "invert";
                    return (boolValue ^ invert) ? Visibility.Visible : Visibility.Collapsed;
                }
                
                // Gérer null et autres types
                if (value == null)
                {
                    bool invert = parameter?.ToString()?.ToLower() == "invert";
                    return invert ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch
            {
                // En cas d'erreur, retourner une valeur sûre
            }
            
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convertit un pourcentage en angle pour l'arc de progression
    /// </summary>
    public class PercentToAngleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int percent)
            {
                return (percent / 100.0) * 360.0;
            }
            if (value is double percentDouble)
            {
                return (percentDouble / 100.0) * 360.0;
            }
            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convertit l'état du scan en visibilité
    /// </summary>
    public class ScanStateToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string state && parameter is string targetState)
            {
                return state == targetState ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convertit un grade en couleur
    /// </summary>
    public class GradeToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string grade)
            {
                return grade switch
                {
                    "A+" or "A" => new SolidColorBrush(Color.FromRgb(46, 213, 115)),
                    "B+" or "B" => new SolidColorBrush(Color.FromRgb(123, 237, 159)),
                    "C+" or "C" => new SolidColorBrush(Color.FromRgb(255, 165, 2)),
                    "D+" or "D" => new SolidColorBrush(Color.FromRgb(255, 99, 72)),
                    "F" => new SolidColorBrush(Color.FromRgb(255, 71, 87)),
                    _ => new SolidColorBrush(Color.FromRgb(139, 148, 158))
                };
            }
            return new SolidColorBrush(Color.FromRgb(139, 148, 158));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Inverse un booléen
    /// </summary>
    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return false;
        }
    }

    /// <summary>
    /// Convertit la progression en rayon de flou pour le glow
    /// </summary>
    public class ProgressToBlurConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value == null) return 0.0;
                
                if (value is int progress)
                {
                    // 0-100 -> 0-50 blur radius
                    return Math.Max(0.0, Math.Min(50.0, progress * 0.5));
                }
                
                if (value is double progressDouble)
                {
                    return Math.Max(0.0, Math.Min(50.0, progressDouble * 0.5));
                }
                
                // Tentative de conversion
                if (int.TryParse(value.ToString(), out int parsedProgress))
                {
                    return Math.Max(0.0, Math.Min(50.0, parsedProgress * 0.5));
                }
            }
            catch
            {
                // En cas d'erreur, retourner une valeur sûre
            }
            
            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convertit la progression en opacité pour le glow
    /// </summary>
    public class ProgressToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value == null) return 0.0;
                
                if (value is int progress)
                {
                    // 0-100 -> 0.0-0.8 opacity
                    return Math.Max(0.0, Math.Min(0.8, progress / 100.0 * 0.8));
                }
                
                if (value is double progressDouble)
                {
                    return Math.Max(0.0, Math.Min(0.8, progressDouble / 100.0 * 0.8));
                }
                
                // Tentative de conversion
                if (int.TryParse(value.ToString(), out int parsedProgress))
                {
                    return Math.Max(0.0, Math.Min(0.8, parsedProgress / 100.0 * 0.8));
                }
            }
            catch
            {
                // En cas d'erreur, retourner une valeur sûre
            }
            
            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convertit une progression en géométrie d'arc pour un indicateur circulaire
    /// Version améliorée : arc lisse sans ondulation, calcul précis
    /// </summary>
    public class ProgressToArcConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var percent = 0.0;
            if (value is int intValue)
            {
                percent = intValue;
            }
            else if (value is double doubleValue)
            {
                percent = doubleValue;
            }
            else if (value != null && double.TryParse(value.ToString(), out var parsed))
            {
                percent = parsed;
            }

            percent = Math.Max(0.0, Math.Min(100.0, percent));
            if (percent <= 0.0)
            {
                return Geometry.Empty;
            }

            var radius = 130.0;
            if (parameter != null && double.TryParse(parameter.ToString(), out var parsedRadius))
            {
                radius = parsedRadius;
            }

            // Padding interne : décale le centre pour que le stroke (centré sur le tracé)
            // ne dépasse jamais les bounds du Path. Sans ce padding, un stroke de 10px
            // déborde de 5px au-delà de y=0 et WPF clippe le Shape à ses propres bounds.
            const double padding = 10.0;
            var center = new Point(radius + padding, radius + padding);
            var startPoint = new Point(center.X, center.Y - radius);
            
            if (percent >= 100.0)
            {
                // Cercle complet : utiliser deux arcs pour garantir la continuité
                var midPoint = new Point(center.X, center.Y + radius);
                var fullFigure = new PathFigure
                {
                    StartPoint = startPoint,
                    IsClosed = false,
                    IsFilled = false
                };
                fullFigure.Segments.Add(new ArcSegment
                {
                    Point = midPoint,
                    Size = new Size(radius, radius),
                    SweepDirection = SweepDirection.Clockwise,
                    IsLargeArc = false
                });
                fullFigure.Segments.Add(new ArcSegment
                {
                    Point = startPoint,
                    Size = new Size(radius, radius),
                    SweepDirection = SweepDirection.Clockwise,
                    IsLargeArc = false
                });

                var fullGeometry = new PathGeometry();
                fullGeometry.Figures.Add(fullFigure);
                return fullGeometry;
            }

            // Calcul précis de l'angle en radians pour éviter les ondulations
            var angleDegrees = percent / 100.0 * 360.0;
            var angleRadians = angleDegrees * Math.PI / 180.0;

            // Point final calculé avec précision
            var endPoint = new Point(
                center.X + radius * Math.Sin(angleRadians),
                center.Y - radius * Math.Cos(angleRadians));

            var isLargeArc = angleDegrees > 180.0;

            // Créer l'arc avec un seul segment pour garantir la continuité
            var figure = new PathFigure
            {
                StartPoint = startPoint,
                IsClosed = false,
                IsFilled = false
            };
            
            // ArcSegment unique pour un rendu lisse
            figure.Segments.Add(new ArcSegment
            {
                Point = endPoint,
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = isLargeArc
            });

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            
            // Freeze la géométrie pour de meilleures performances
            geometry.Freeze();
            
            return geometry;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convertit HealthSeverity en couleur SolidColorBrush
    /// </summary>
    public class HealthSeverityToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is HealthSeverity severity)
            {
                var hexColor = HealthReport.SeverityToColor(severity);
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(hexColor);
                    return new SolidColorBrush(color);
                }
                catch
                {
                    return new SolidColorBrush(Color.FromRgb(158, 158, 158));
                }
            }
            return new SolidColorBrush(Color.FromRgb(158, 158, 158));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convertit HealthSeverity en icône texte
    /// </summary>
    public class HealthSeverityToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is HealthSeverity severity)
            {
                return HealthReport.SeverityToIcon(severity);
            }
            return "?";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convertit un score 0-100 en couleur selon la taxonomie métier
    /// </summary>
    public class ScoreToHealthColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int score)
            {
                var severity = HealthReport.ScoreToSeverity(score);
                var hexColor = HealthReport.SeverityToColor(severity);
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(hexColor);
                    return new SolidColorBrush(color);
                }
                catch
                {
                    return new SolidColorBrush(Color.FromRgb(158, 158, 158));
                }
            }
            return new SolidColorBrush(Color.FromRgb(158, 158, 158));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convertit HealthSeverity en texte d'alerte (! pour critical/degraded)
    /// </summary>
    public class HealthSeverityToAlertConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is HealthSeverity severity)
            {
                return severity switch
                {
                    HealthSeverity.Critical => "!",
                    HealthSeverity.Degraded => "!",
                    _ => ""
                };
            }
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convertit HealthSeverity en visibilité pour les alertes
    /// </summary>
    public class HealthSeverityToAlertVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is HealthSeverity severity)
            {
                return severity == HealthSeverity.Critical || severity == HealthSeverity.Degraded
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convertit une chaîne hexadécimale (#RRGGBB) en SolidColorBrush
    /// </summary>
    public class HexToSolidColorBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string hexColor && !string.IsNullOrEmpty(hexColor))
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(hexColor);
                    return new SolidColorBrush(color);
                }
                catch
                {
                    // Fallback grey
                }
            }
            return new SolidColorBrush(Color.FromRgb(158, 158, 158));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convertit IssueLevel (Info, Warning, Critical) en couleur pour les badges et bordures.
    /// </summary>
    public class IssueLevelToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is IssueLevel level)
            {
                return level switch
                {
                    IssueLevel.Critical => new SolidColorBrush(Color.FromRgb(255, 71, 87)),
                    IssueLevel.Warning => new SolidColorBrush(Color.FromRgb(255, 165, 2)),
                    IssueLevel.Info => new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                    _ => new SolidColorBrush(Color.FromRgb(139, 148, 158))
                };
            }
            return new SolidColorBrush(Color.FromRgb(139, 148, 158));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convertit IssueLevel en icône "!" pour Critical/Warning.
    /// </summary>
    public class IssueLevelToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is IssueLevel level)
                return level == IssueLevel.Critical || level == IssueLevel.Warning ? "!" : "";
            if (value is bool hasCritical && hasCritical)
                return "!";
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Convertit un score 0–100 en couleur : 100 = or, 70+ = vert, 60–70 = jaune, &lt;60 = rouge.
    /// </summary>
    public class ScoreToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var score = 0.0;
            if (value is int i) score = i;
            else if (value is double d) score = d;
            else if (value != null && double.TryParse(value.ToString(), out var p)) score = p;
            score = Math.Max(0, Math.Min(100, score));
            if (score >= 100) return new SolidColorBrush(Color.FromRgb(255, 215, 0));   // gold
            if (score >= 70) return new SolidColorBrush(Color.FromRgb(76, 175, 80));   // green
            if (score >= 60) return new SolidColorBrush(Color.FromRgb(255, 193, 7));   // yellow
            return new SolidColorBrush(Color.FromRgb(244, 67, 54));                    // red
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Performance section color coding: Red &lt;40, Yellow 40–70, Green &gt;70 (per spec). Score &lt; 0 = grey (unavailable).
    /// </summary>
    public class ScoreToPerformanceBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush GreyBrush = new SolidColorBrush(Color.FromRgb(0x4D, 0x55, 0x66));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var score = 0.0;
            if (value is int i) score = i;
            else if (value is double d) score = d;
            else if (value != null && double.TryParse(value.ToString(), out var p)) score = p;
            if (score < 0) return GreyBrush;
            score = Math.Max(0, Math.Min(100, score));
            if (score > 70) return new SolidColorBrush(Color.FromRgb(76, 175, 80));   // green
            if (score >= 40) return new SolidColorBrush(Color.FromRgb(255, 193, 7)); // yellow
            return new SolidColorBrush(Color.FromRgb(244, 67, 54));                   // red
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts score 0-100 to bar width in pixels (e.g. 200 * score/100) for Performance scenario bar chart. Score &lt; 0 = 0 width (unavailable).
    /// </summary>
    public class ScoreToBarWidthConverter : IValueConverter
    {
        private const double DefaultMaxWidth = 200.0;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var score = 0.0;
            if (value is int i) score = i;
            else if (value is double d) score = d;
            else if (value != null && double.TryParse(value.ToString(), out var p)) score = p;
            if (score < 0) return 0.0;
            score = Math.Max(0, Math.Min(100, score));
            double max = DefaultMaxWidth;
            if (parameter != null && double.TryParse(parameter.ToString(), NumberStyles.Number, culture, out var pm))
                max = pm;
            var computedWidth = score * max / 100.0;
            
            return computedWidth;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// For Performance section: returns "N/A" when score &lt; 0 (evaluation unavailable), else the score as string.
    /// ConverterParameter "WithSuffix" or "100": when score &gt;= 0, append "/100" (e.g. "92.4/100").
    /// ConverterParameter "Decimal": show decimal precision always (e.g. "92.4").
    /// Default: show decimal if not a round number.
    /// </summary>
    public class PerformanceScoreToDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var score = 0.0;
            if (value is int i) score = i;
            else if (value is double d) score = d;
            else if (value != null && double.TryParse(value.ToString(), out var p)) score = p;
            
            var paramStr = parameter?.ToString() ?? "";
            bool withSuffix = "WithSuffix".Equals(paramStr, StringComparison.OrdinalIgnoreCase)
                              || "100".Equals(paramStr, StringComparison.OrdinalIgnoreCase);
            bool forceDecimal = "Decimal".Equals(paramStr, StringComparison.OrdinalIgnoreCase);
            
            if (score < 0) return "N/A";
            
            score = Math.Max(0, Math.Min(100, score));
            
            // Use decimal format if score has meaningful decimal part
            bool hasDecimal = Math.Abs(score - Math.Round(score)) > 0.05;
            string scoreStr;
            
            if (score >= 99.95)
                scoreStr = "100";
            else if (forceDecimal || hasDecimal)
                scoreStr = $"{score:F1}";
            else
                scoreStr = $"{(int)Math.Round(score)}";
            
            return withSuffix ? $"{scoreStr}/100" : scoreStr;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Returns Visible when value is a non-empty collection, else Collapsed. For Performance dashboard empty state.
    /// </summary>
    public class CollectionNotEmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool hasItems = value is System.Collections.ICollection c && c.Count > 0;
            bool invert = "Invert".Equals(parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
            return (hasItems != invert) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Returns Visible when string value is non-null and non-empty; else Collapsed.
    /// Supports "Invert" parameter to invert logic.
    /// Used for Performance source traceability labels and fallback warnings.
    /// </summary>
    public class StringNotEmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool hasContent = value is string s && !string.IsNullOrEmpty(s);
            bool invert = "Invert".Equals(parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
            return (hasContent != invert) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>Returns Visible when value equals parameter (string comparison, case-insensitive); else Collapsed. Used e.g. to show "Refresh requirements" only on Performance section.</summary>
    public class EqualsToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var param = parameter?.ToString() ?? "";
            bool match = value is string s && string.Equals(s, param, StringComparison.OrdinalIgnoreCase);
            return match ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Returns Visible when integer value is greater than 0 (e.g. ConfidenceScore); else Collapsed. For optional Confidence row.
    /// </summary>
    public class IntGreaterThanZeroToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var n = 0;
            if (value is int i) n = i;
            else if (value != null && int.TryParse(value.ToString(), out var p)) n = p;
            return n > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// For Performance header: returns Collapsed when section is Performance and evaluation is unavailable (hide score/100 and check icon).
    /// Parameter "Invert": Collapsed when evaluation IS available (for showing N/A block only when unavailable).
    /// Value should be the HealthSection (binding to self or DataContext).
    /// </summary>
    public class PerformanceHeaderAvailabilityToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool invert = "Invert".Equals(parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
            if (value is not HealthSection section)
                return invert ? Visibility.Collapsed : Visibility.Visible;
            bool hideWhenUnavailable = section.Domain == HealthDomain.Performance && !section.IsPerformanceEvaluationAvailable;
            bool visible = invert ? hideWhenUnavailable : !hideWhenUnavailable;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Returns Visible when value is HealthDomain.Performance, else Collapsed. Parameter "Invert" = visible when NOT Performance.
    /// Used in MainWindow to switch between Performance dashboard and default section content.
    /// </summary>
    public class HealthDomainToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isPerformance = value is HealthDomain d && d == HealthDomain.Performance;
            bool invert = "Invert".Equals(parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
            return (isPerformance != invert) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Retourne 1.0 si le grade actuel correspond au paramètre, sinon 0.45.
    /// Utilisé pour auto-surligner la ligne de légende du grade actif.
    /// Usage XAML : Opacity="{Binding Grade, ConverterParameter=A+, Converter={StaticResource GradeMatchOpacity}}"
    /// </summary>
    public class GradeMatchOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string grade && parameter is string target)
            {
                // Match exact ou match grade family (ex: "B+" matches "B" target)
                if (string.Equals(grade, target, StringComparison.OrdinalIgnoreCase))
                    return 1.0;
                // B+ et B- matchent la ligne B, etc.
                if (grade.Length == 2 && (grade[1] == '+' || grade[1] == '-')
                    && string.Equals(grade.Substring(0, 1), target, StringComparison.OrdinalIgnoreCase))
                    return 1.0;
                return 0.45;
            }
            return 0.45;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// MultiValueConverter: shows visibility when row Key is "Kernel-Power" and section HasKernelPowerId1 is true.
    /// Used for the (i) button that opens KernelPowerInfoWindow.
    /// </summary>
    public class KernelPowerInfoButtonVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return Visibility.Collapsed;
            var key = values[0]?.ToString() ?? "";
            var hasKp1 = values[1] is bool b && b;
            return (string.Equals(key, "Kernel-Power", StringComparison.OrdinalIgnoreCase) && hasKp1)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Translates performance scenario names and classifications from English to French for UI display.
    /// Does not modify backend data - translation happens at presentation layer only.
    /// </summary>
    public class ScenarioNameTranslatorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return value;
            
            var text = value.ToString();
            if (string.IsNullOrEmpty(text)) return value;

            // Translate scenario names
            var translated = text switch
            {
                "Office / Browsing" => "Bureau / Navigation",
                "4K Video Editing" => "Montage vidéo 4K",
                "Streaming + Gaming" => "Streaming + Jeu",
                "Virtual Machines" => "Machines virtuelles",
                "AI (basic inference)" => "IA (inférence de base)",
                
                // Translate classifications
                "Not Recommended" => "Non recommandé",
                "Good" => "Bon",
                
                // Keep as-is
                "Excellent" => "Excellent",
                "Acceptable" => "Acceptable",
                "Multitasking" => "Multitâche",
                "Gaming (1080p)" => "Jeu (1080p)",
                "Gaming (1440p)" => "Jeu (1440p)",
                
                _ => text
            };

            return translated;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Filters out items from a collection based on a property value.
    /// Used to hide "AI (basic inference)" scenario from UI without modifying backend data.
    /// Parameter format: "PropertyName:ValueToFilter"
    /// </summary>
    public class CollectionFilterConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is System.Collections.IEnumerable collection && parameter is string filterSpec)
            {
                var parts = filterSpec.Split(':');
                if (parts.Length != 2) return value;

                var propertyName = parts[0].Trim();
                var filterValue = parts[1].Trim();

                var filtered = new System.Collections.ObjectModel.ObservableCollection<object>();
                foreach (var item in collection)
                {
                    if (item == null) continue;

                    var prop = item.GetType().GetProperty(propertyName);
                    if (prop == null)
                    {
                        filtered.Add(item);
                        continue;
                    }

                    var propValue = prop.GetValue(item)?.ToString() ?? "";
                    if (!propValue.Equals(filterValue, StringComparison.OrdinalIgnoreCase))
                    {
                        filtered.Add(item);
                    }
                }

                return filtered;
            }

            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// MultiValueConverter: computes proportional width as (score / 100) * parentWidth.
    /// Values[0] = Score (int or double), Values[1] = parent ActualWidth (double).
    /// Used for progress bars that must scale to their container width.
    /// </summary>
    public class ScoreToProportionalWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return 0.0;
            
            var score = 0.0;
            if (values[0] is int i) score = i;
            else if (values[0] is double d) score = d;
            else if (values[0] != null && double.TryParse(values[0].ToString(), out var p)) score = p;
            
            var parentWidth = 0.0;
            if (values[1] is double pw) parentWidth = pw;
            else if (values[1] != null && double.TryParse(values[1].ToString(), out var pw2)) parentWidth = pw2;
            
            if (score < 0) return 0.0;
            score = Math.Max(0, Math.Min(100, score));
            var result = (score / 100.0) * parentWidth;
            
            return result;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts score 0-100 to scale factor 0.0-1.0 for ScaleTransform.
    /// </summary>
    public class ScoreToScaleFactorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var score = 0.0;
            if (value is int i) score = i;
            else if (value is double d) score = d;
            else if (value != null && double.TryParse(value.ToString(), out var p)) score = p;
            if (score < 0) return 0.0;
            score = Math.Max(0, Math.Min(100, score));
            var scaleFactor = score / 100.0;
            
            return scaleFactor;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Extracts temperature value from a string containing °C and maps it to a thermal status badge.
    /// Returns badge text like "🟢 Normal", "🟡 Élevé", "🔴 Critique", or "⚪ Inconnu".
    /// UI-only logic for disk temperature display.
    /// </summary>
    public class TemperatureToBadgeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return "⚪ Inconnu";

            var text = value.ToString();
            if (string.IsNullOrEmpty(text)) return "⚪ Inconnu";

            // Extract temperature value
            var match = System.Text.RegularExpressions.Regex.Match(text, @"(\d+(?:\.\d+)?)\s*°C");
            if (!match.Success || !double.TryParse(match.Groups[1].Value, out var temp))
                return "⚪ Inconnu";

            // Map to thermal status
            if (temp < 50) return "🟢 Normal";
            if (temp <= 60) return "🟡 Élevé";
            return "🔴 Critique";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Extracts temperature value and returns color brush for thermal status badge.
    /// UI-only logic for disk temperature display.
    /// </summary>
    public class TemperatureToBadgeColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return new SolidColorBrush(Color.FromRgb(139, 148, 158)); // Grey

            var text = value.ToString();
            if (string.IsNullOrEmpty(text)) return new SolidColorBrush(Color.FromRgb(139, 148, 158));

            // Extract temperature value
            var match = System.Text.RegularExpressions.Regex.Match(text, @"(\d+(?:\.\d+)?)\s*°C");
            if (!match.Success || !double.TryParse(match.Groups[1].Value, out var temp))
                return new SolidColorBrush(Color.FromRgb(139, 148, 158)); // Grey

            // Map to color
            if (temp < 50) return new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
            if (temp <= 60) return new SolidColorBrush(Color.FromRgb(255, 193, 7)); // Yellow
            return new SolidColorBrush(Color.FromRgb(244, 67, 54)); // Red
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Returns Visible only if value contains "°C" (for disk temperature filtering).
    /// </summary>
    public class ContainsCelsiusToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return Visibility.Collapsed;
            var text = value.ToString();
            return !string.IsNullOrEmpty(text) && text.Contains("°C") ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Returns Visible if Key contains "SMART" (case-insensitive).
    /// Used to show info icon for SMART health entries.
    /// </summary>
    public class IsSmartKeyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return Visibility.Collapsed;
            var text = value.ToString();
            return !string.IsNullOrEmpty(text) && text.Contains("SMART", StringComparison.OrdinalIgnoreCase) 
                ? Visibility.Visible 
                : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Maps SMART health value to badge text with emoji.
    /// UI-only logic based on existing model values.
    /// </summary>
    public class SmartHealthToBadgeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return "⚪ Inconnu";

            var text = value.ToString()?.ToLower() ?? "";
            
            // Check for failure/critical states
            if (text.Contains("fail") || text.Contains("critique") || text.Contains("critical") || text.Contains("échec"))
                return "🔴 Critique";
            
            // Check for warning states
            if (text.Contains("warn") || text.Contains("avertissement") || text.Contains("attention"))
                return "🟡 Avertissement";
            
            // Check for OK states
            if (text.Contains("ok") || text.Contains("bon") || text.Contains("good") || text.Contains("healthy"))
                return "🟢 OK";
            
            // Default to unknown
            return "⚪ Inconnu";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Maps SMART health value to badge color.
    /// UI-only logic based on existing model values.
    /// </summary>
    public class SmartHealthToBadgeColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return new SolidColorBrush(Color.FromRgb(139, 148, 158)); // Grey

            var text = value.ToString()?.ToLower() ?? "";
            
            // Check for failure/critical states
            if (text.Contains("fail") || text.Contains("critique") || text.Contains("critical") || text.Contains("échec"))
                return new SolidColorBrush(Color.FromRgb(244, 67, 54)); // Red
            
            // Check for warning states
            if (text.Contains("warn") || text.Contains("avertissement") || text.Contains("attention"))
                return new SolidColorBrush(Color.FromRgb(255, 193, 7)); // Yellow
            
            // Check for OK states
            if (text.Contains("ok") || text.Contains("bon") || text.Contains("good") || text.Contains("healthy"))
                return new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
            
            // Default to unknown (grey)
            return new SolidColorBrush(Color.FromRgb(139, 148, 158));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Deduplicates disk temperature entries for display.
    /// Filters collection to show only unique disk entries (removes duplicates like "Model: 45°C" and "Model (HDD 1TB) 45°C").
    /// UI-only logic - presentation layer deduplication.
    /// </summary>
    public class DiskTemperatureDeduplicator : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not System.Collections.IEnumerable collection)
                return value;

            var items = new System.Collections.Generic.List<object>();
            var seenDiskNames = new System.Collections.Generic.HashSet<string>();

            foreach (var item in collection)
            {
                if (item == null) continue;

                // Get the Value property (contains temperature with °C)
                var valueProperty = item.GetType().GetProperty("Value");
                var keyProperty = item.GetType().GetProperty("Key");
                
                if (valueProperty == null || keyProperty == null) continue;

                var valueStr = valueProperty.GetValue(item)?.ToString() ?? "";
                var keyStr = keyProperty.GetValue(item)?.ToString() ?? "";

                // Only process temperature entries
                if (!valueStr.Contains("°C")) continue;

                // Extract disk model name (before the colon or parenthesis)
                var diskModel = valueStr.Split(':')[0].Trim();
                if (diskModel.Contains('('))
                    diskModel = diskModel.Split('(')[0].Trim();

                // Also skip aggregate entries (contain | separators)
                if (valueStr.Contains("|")) continue;

                // Skip if we've already seen this disk
                if (seenDiskNames.Contains(diskModel))
                    continue;

                seenDiskNames.Add(diskModel);
                items.Add(item);
            }

            return new System.Collections.ObjectModel.ObservableCollection<object>(items);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Extracts just the disk model name from a value like "Model: 45°C" or "Model (HDD 1TB) 45°C".
    /// Returns just "Model" for display in the Disque column.
    /// </summary>
    public class ExtractDiskModelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return "";
            
            var text = value.ToString() ?? "";
            
            // Extract model name before colon or parenthesis
            var model = text.Split(':')[0].Trim();
            if (model.Contains('('))
                model = model.Split('(')[0].Trim();
            
            return model;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Extracts just the temperature value from a string like "Model: 45°C".
    /// Returns just "45°C" for display in the Température column.
    /// </summary>
    public class ExtractTemperatureConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return "N/A";
            
            var text = value.ToString() ?? "";
            
            // Extract temperature (everything after the last colon or the whole string if it's just temp)
            var match = System.Text.RegularExpressions.Regex.Match(text, @"(\d+(?:\.\d+)?)\s*°C");
            if (match.Success)
                return match.Value;
            
            return "N/A";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Hides duplicate disk entries in EvidenceDataWithTooltips.
    /// Shows only the shorter format (e.g., "Model: 45°C") and hides the longer one (e.g., "Model (HDD 1TB) 45°C").
    /// Parameter: "HideLonger" - hides entries with parentheses when duplicate exists.
    /// </summary>
    public class DiskDuplicateFilterConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return Visibility.Visible;
            
            var text = value.ToString() ?? "";
            
            // Hide entries that contain both parenthesis AND temperature (these are the duplicates with extra info)
            if (text.Contains("(") && text.Contains("°C"))
                return Visibility.Collapsed;
            
            // Hide aggregate entries (contain | separator)
            if (text.Contains("|") && text.Contains("°C"))
                return Visibility.Collapsed;
            
            // Hide standalone temperature values (just "45°C" without model name)
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d+(?:\.\d+)?\s*°C$"))
                return Visibility.Collapsed;
            
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
