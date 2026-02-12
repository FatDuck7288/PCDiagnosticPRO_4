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
            return score * max / 100.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// For Performance section: returns "N/A" when score &lt; 0 (evaluation unavailable), else the score as string.
    /// ConverterParameter "WithSuffix" or "100": when score &gt;= 0, append "/100" (e.g. "100/100").
    /// </summary>
    public class PerformanceScoreToDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var score = 0.0;
            if (value is int i) score = i;
            else if (value is double d) score = d;
            else if (value != null && double.TryParse(value.ToString(), out var p)) score = p;
            bool withSuffix = "WithSuffix".Equals(parameter?.ToString(), StringComparison.OrdinalIgnoreCase)
                              || "100".Equals(parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
            if (score < 0) return "N/A";
            var num = (int)Math.Round(Math.Max(0, Math.Min(100, score)));
            return withSuffix ? $"{num}/100" : num.ToString();
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
}
