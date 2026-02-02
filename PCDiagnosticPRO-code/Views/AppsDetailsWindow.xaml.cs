using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace PCDiagnosticPro.Views
{
    /// <summary>
    /// Fenêtre de détails des applications - affiche un DataGrid avec toutes les apps installées
    /// </summary>
    public partial class AppsDetailsWindow : Window
    {
        public AppsDetailsWindow(JsonElement? appsData, JsonElement? startupData)
        {
            InitializeComponent();
            DataContext = new AppsDetailsViewModel(appsData, startupData);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    /// <summary>
    /// ViewModel pour la fenêtre de détails des applications
    /// PARTIE 9: Ajout recherche et filtrage
    /// </summary>
    public class AppsDetailsViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private readonly List<AppDisplayItem> _allApps = new();
        public ObservableCollection<AppDisplayItem> Applications { get; } = new();
        public ObservableCollection<AppDisplayItem> FilteredApplications { get; } = new();

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set { _searchText = value; OnPropertyChanged(nameof(SearchText)); ApplyFilter(); }
        }

        public int TotalCount { get; }
        public int RecentCount { get; }
        public int StartupCount { get; }
        
        public bool HasRecent => RecentCount > 0;
        public bool HasStartupApps => StartupCount > 0;

        public string SummaryText { get; }

        public AppsDetailsViewModel(JsonElement? appsData, JsonElement? startupData)
        {
            var apps = new List<AppDisplayItem>();
            var startupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Extract startup app names for cross-reference
            if (startupData.HasValue)
            {
                JsonElement? programs = null;
                if (startupData.Value.TryGetProperty("programs", out var progs))
                    programs = progs;
                else if (startupData.Value.TryGetProperty("startupItems", out var items))
                    programs = items;

                if (programs.HasValue && programs.Value.ValueKind == JsonValueKind.Array)
                {
                    StartupCount = programs.Value.GetArrayLength();
                    foreach (var prog in programs.Value.EnumerateArray())
                    {
                        var name = GetString(prog, "name") ?? GetString(prog, "displayName");
                        if (!string.IsNullOrEmpty(name))
                            startupNames.Add(name);
                    }
                }
            }

            // Extract applications
            if (appsData.HasValue)
            {
                JsonElement? appsList = null;
                
                // Try different property names
                if (appsData.Value.TryGetProperty("applications", out var appsProp))
                    appsList = appsProp;
                else if (appsData.Value.TryGetProperty("apps", out var appsProp2))
                    appsList = appsProp2;
                else if (appsData.Value.TryGetProperty("list", out var listProp))
                    appsList = listProp;
                else if (appsData.Value.ValueKind == JsonValueKind.Array)
                    appsList = appsData.Value;

                if (appsList.HasValue && appsList.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var app in appsList.Value.EnumerateArray())
                    {
                        var item = new AppDisplayItem(app, startupNames);
                        if (!string.IsNullOrEmpty(item.Name))
                            apps.Add(item);
                    }
                }

                // Get total count from property if available
                var countProp = GetInt(appsData, "applicationCount") ?? 
                                GetInt(appsData, "count") ?? 
                                GetInt(appsData, "totalCount");
                TotalCount = countProp ?? apps.Count;
            }

            // Count recent installs (last 30 days)
            RecentCount = apps.Count(a => a.IsRecent);

            // Sort by install date descending, then by name
            foreach (var app in apps.OrderByDescending(a => a.InstallDate ?? DateTime.MinValue).ThenBy(a => a.Name))
            {
                _allApps.Add(app);
                Applications.Add(app);
            }
            
            ApplyFilter();

            SummaryText = $"Applications détectées depuis le registre Windows et les sources de packages";
        }

        private void ApplyFilter()
        {
            FilteredApplications.Clear();
            var search = SearchText?.ToLowerInvariant() ?? "";
            
            foreach (var app in _allApps)
            {
                // Filter by search text
                if (!string.IsNullOrEmpty(search))
                {
                    var matchName = app.Name?.ToLowerInvariant().Contains(search) == true;
                    var matchPublisher = app.Publisher?.ToLowerInvariant().Contains(search) == true;
                    if (!matchName && !matchPublisher)
                        continue;
                }
                
                FilteredApplications.Add(app);
            }
        }

        private static string? GetString(JsonElement? element, string propName)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
            if (element.Value.TryGetProperty(propName, out var prop))
                return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
            return null;
        }

        private static int? GetInt(JsonElement? element, string propName)
        {
            if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object) return null;
            if (element.Value.TryGetProperty(propName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number) return prop.GetInt32();
                if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var i)) return i;
            }
            return null;
        }
    }

    /// <summary>
    /// Item d'application pour affichage dans le DataGrid
    /// </summary>
    public class AppDisplayItem
    {
        private readonly HashSet<string> _startupNames;

        public string Name { get; }
        public string? Publisher { get; }
        public string? Version { get; }
        public DateTime? InstallDate { get; }
        public string? Source { get; }
        public bool IsStartupApp { get; }

        public AppDisplayItem(JsonElement app, HashSet<string> startupNames)
        {
            _startupNames = startupNames;

            Name = GetString(app, "name") ?? GetString(app, "displayName") ?? "";
            Publisher = GetString(app, "publisher") ?? GetString(app, "vendor");
            Version = GetString(app, "version") ?? GetString(app, "displayVersion");
            Source = DetermineSource(app);
            
            // Parse install date
            var dateStr = GetString(app, "installDate") ?? GetString(app, "InstallDate");
            if (!string.IsNullOrEmpty(dateStr))
            {
                if (DateTime.TryParse(dateStr, out var dt))
                    InstallDate = dt;
                else if (dateStr.Length >= 8)
                {
                    // Try yyyyMMdd format
                    try
                    {
                        var y = int.Parse(dateStr.Substring(0, 4));
                        var m = int.Parse(dateStr.Substring(4, 2));
                        var d = int.Parse(dateStr.Substring(6, 2));
                        InstallDate = new DateTime(y, m, d);
                    }
                    catch { }
                }
            }

            // Check if this app is in startup
            IsStartupApp = !string.IsNullOrEmpty(Name) && startupNames.Contains(Name);
        }

        public string InstallDateFormatted => InstallDate?.ToString("yyyy-MM-dd") ?? "—";

        public bool IsRecent => InstallDate.HasValue && (DateTime.Now - InstallDate.Value).TotalDays <= 30;

        public string RecentIcon => IsRecent ? "🆕" : "";

        public string SourceIcon => Source?.ToLower() switch
        {
            "msi" or "windows installer" => "📦",
            "store" or "microsoft store" => "🏪",
            "winget" => "📥",
            "msix" or "appx" => "🗃️",
            _ => "❓"
        };

        private static string? GetString(JsonElement element, string propName)
        {
            if (element.TryGetProperty(propName, out var prop))
                return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
            return null;
        }

        private static string? DetermineSource(JsonElement app)
        {
            var source = GetString(app, "source") ?? GetString(app, "installSource");
            if (!string.IsNullOrEmpty(source))
                return source;

            // Try to determine from other properties
            var uninstallString = GetString(app, "uninstallString") ?? "";
            var installLocation = GetString(app, "installLocation") ?? "";

            if (uninstallString.Contains("MsiExec", StringComparison.OrdinalIgnoreCase))
                return "MSI";
            if (installLocation.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                return "Store";
            if (uninstallString.Contains("winget", StringComparison.OrdinalIgnoreCase))
                return "Winget";

            return "Unknown";
        }
    }
}
