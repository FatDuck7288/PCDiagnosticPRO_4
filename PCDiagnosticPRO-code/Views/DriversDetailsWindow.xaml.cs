using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using PCDiagnosticPro.Models;

namespace PCDiagnosticPro.Views
{
    /// <summary>
    /// Fenêtre de détails des pilotes - affiche un DataGrid avec tous les pilotes installés
    /// </summary>
    public partial class DriversDetailsWindow : Window
    {
        public DriversDetailsWindow(DriverInventoryResult? driverInventory)
        {
            InitializeComponent();
            DataContext = new DriversDetailsViewModel(driverInventory);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    /// <summary>
    /// ViewModel pour la fenêtre de détails des pilotes
    /// PARTIE 8: Ajout recherche et filtrage par classe
    /// </summary>
    public class DriversDetailsViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private readonly List<DriverDisplayItem> _allDrivers = new();
        public ObservableCollection<DriverDisplayItem> Drivers { get; } = new();
        public ObservableCollection<DriverDisplayItem> FilteredDrivers { get; } = new();
        public ObservableCollection<string> DeviceClasses { get; } = new();

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set { _searchText = value; OnPropertyChanged(nameof(SearchText)); ApplyFilter(); }
        }

        private string _selectedClass = "Toutes";
        public string SelectedClass
        {
            get => _selectedClass;
            set { _selectedClass = value; OnPropertyChanged(nameof(SelectedClass)); ApplyFilter(); }
        }

        public int TotalCount { get; }
        public int SignedCount { get; }
        public int UnsignedCount { get; }
        public int ProblemCount { get; }
        public int OutdatedCount { get; }
        
        public bool HasUnsigned => UnsignedCount > 0;
        public bool HasProblems => ProblemCount > 0;
        public bool HasOutdated => OutdatedCount > 0;

        public string SummaryText { get; }

        public DriversDetailsViewModel(DriverInventoryResult? inventory)
        {
            DeviceClasses.Add("Toutes");
            
            if (inventory?.Drivers == null || !inventory.Available)
            {
                SummaryText = "Aucune donnée de pilotes disponible";
                return;
            }

            TotalCount = inventory.TotalCount;
            SignedCount = inventory.SignedCount;
            UnsignedCount = inventory.UnsignedCount;
            ProblemCount = inventory.ProblemCount;

            // Calculate outdated count (>24 months old)
            OutdatedCount = inventory.Drivers.Count(d => IsDriverOutdated(d));

            SummaryText = $"Source: {inventory.Source} | Collecté le: {inventory.Timestamp}";

            // Populate drivers list and extract unique classes
            var classes = new HashSet<string>();
            foreach (var driver in inventory.Drivers.OrderBy(d => d.DeviceClass).ThenBy(d => d.DeviceName))
            {
                var item = new DriverDisplayItem(driver);
                _allDrivers.Add(item);
                Drivers.Add(item);
                if (!string.IsNullOrEmpty(driver.DeviceClass))
                    classes.Add(driver.DeviceClass);
            }
            
            // Populate class filter dropdown
            foreach (var cls in classes.OrderBy(c => c))
                DeviceClasses.Add(cls);
            
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            FilteredDrivers.Clear();
            var search = SearchText?.ToLowerInvariant() ?? "";
            
            foreach (var driver in _allDrivers)
            {
                // Filter by class
                if (SelectedClass != "Toutes" && driver.DeviceClass != SelectedClass)
                    continue;
                
                // Filter by search text
                if (!string.IsNullOrEmpty(search))
                {
                    var matchName = driver.DeviceName?.ToLowerInvariant().Contains(search) == true;
                    var matchClass = driver.DeviceClass?.ToLowerInvariant().Contains(search) == true;
                    var matchProvider = driver.Provider?.ToLowerInvariant().Contains(search) == true;
                    if (!matchName && !matchClass && !matchProvider)
                        continue;
                }
                
                FilteredDrivers.Add(driver);
            }
        }

        private static bool IsDriverOutdated(DriverInventoryItem driver)
        {
            if (driver.UpdateStatus == "Outdated")
                return true;

            if (!string.IsNullOrEmpty(driver.DriverDate) && 
                DateTime.TryParse(driver.DriverDate, out var date))
            {
                return (DateTime.Now - date).TotalDays > 730; // >24 months
            }

            return false;
        }
    }

    /// <summary>
    /// Item de pilote pour affichage dans le DataGrid
    /// </summary>
    public class DriverDisplayItem
    {
        private readonly DriverInventoryItem _driver;

        public DriverDisplayItem(DriverInventoryItem driver)
        {
            _driver = driver;
        }

        public string DeviceClass => _driver.DeviceClass;
        public string DeviceName => _driver.DeviceName;
        public string? Provider => _driver.Provider ?? _driver.Manufacturer;
        public string? DriverVersion => _driver.DriverVersion ?? "—";
        public string? InfName => _driver.InfName;
        public string? PnpDeviceId => _driver.PnpDeviceId;
        
        public string DriverDateFormatted
        {
            get
            {
                if (string.IsNullOrEmpty(_driver.DriverDate))
                    return "—";
                
                if (DateTime.TryParse(_driver.DriverDate, out var date))
                    return date.ToString("yyyy-MM-dd");
                
                // Try to parse WMI date format (yyyyMMdd...)
                if (_driver.DriverDate.Length >= 8)
                {
                    try
                    {
                        return $"{_driver.DriverDate.Substring(0, 4)}-{_driver.DriverDate.Substring(4, 2)}-{_driver.DriverDate.Substring(6, 2)}";
                    }
                    catch { }
                }
                
                return _driver.DriverDate;
            }
        }

        public string SignedIcon => _driver.IsSigned switch
        {
            true => "✅",
            false => "⚠️",
            _ => "❓"
        };

        public string StatusDisplay
        {
            get
            {
                if (_driver.UpdateStatus == "Outdated")
                    return "⚠️ À MAJ";
                
                if (_driver.IsSigned == false)
                    return "⚠️ Non signé";
                
                if (_driver.Status?.ToLower().Contains("error") == true ||
                    _driver.Status?.ToLower().Contains("problem") == true)
                    return "❌ Erreur";
                
                // Check if driver is old (>24 months)
                if (!string.IsNullOrEmpty(_driver.DriverDate) && 
                    DateTime.TryParse(_driver.DriverDate, out var date) &&
                    (DateTime.Now - date).TotalDays > 730)
                {
                    return "⚡ Ancien";
                }
                
                return "✅ OK";
            }
        }

        public Brush StatusColor
        {
            get
            {
                var status = StatusDisplay;
                if (status.Contains("MAJ") || status.Contains("Non signé"))
                    return new SolidColorBrush(Color.FromRgb(255, 165, 2)); // Orange
                if (status.Contains("Erreur"))
                    return new SolidColorBrush(Color.FromRgb(255, 71, 87)); // Red
                if (status.Contains("Ancien"))
                    return new SolidColorBrush(Color.FromRgb(155, 89, 182)); // Purple
                return new SolidColorBrush(Color.FromRgb(46, 213, 115)); // Green
            }
        }
    }
}
