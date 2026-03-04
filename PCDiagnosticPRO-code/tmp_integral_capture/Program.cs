using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCDiagnosticPro.Services;
using PCDiagnosticPro.ViewModels;
using PCDiagnosticPro.Views;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var phase = args.Length > 0 ? args[0] : "before";
            var defaultJson = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PCDiagnosticPro",
                "Rapports",
                "scan_result_combined.json");
            var jsonPath = args.Length > 1 ? args[1] : defaultJson;

            if (!File.Exists(jsonPath))
            {
                Console.Error.WriteLine($"JSON introuvable: {jsonPath}");
                return 2;
            }

            var json = File.ReadAllText(jsonPath);
            var vm = FullReportBuilder.BuildFromJson(json);
            if (vm == null)
            {
                Console.Error.WriteLine("Impossible de construire FullReportViewModel depuis le JSON.");
                return 3;
            }

            var app = new Application();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/PCDiagnosticPro;component/Styles/FuturisticStyles.xaml", UriKind.Absolute)
            });

            var view = new FullReportView
            {
                Width = 1280,
                Height = 920,
                DataContext = vm
            };

            var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["System"] = "os",
                ["PlatformFirmware"] = "platform",
                ["CPU"] = "cpu",
                ["GPU"] = "gpu"
            };

            foreach (var kv in targets)
            {
                var section = vm.Sections.FirstOrDefault(s => string.Equals(s.Id, kv.Key, StringComparison.OrdinalIgnoreCase));
                if (section == null)
                {
                    Console.Error.WriteLine($"Section absente: {kv.Key}");
                    continue;
                }

                vm.IsSummaryMode = false;
                vm.SelectedSection = section;

                view.Measure(new Size(view.Width, view.Height));
                view.Arrange(new Rect(0, 0, view.Width, view.Height));
                view.UpdateLayout();

                var bitmap = new RenderTargetBitmap((int)view.Width, (int)view.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);

                var fileName = $"integral_{phase}_{kv.Value}.png";
                var outputPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", fileName));
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(outputPath);
                encoder.Save(stream);
                Console.WriteLine(outputPath);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
