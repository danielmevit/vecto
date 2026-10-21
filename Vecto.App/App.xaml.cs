using System.IO;
using System.Windows;
using Vecto.Core;

namespace Vecto.App;

public partial class App : Application
{
    void OnStartup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            var log = Path.Combine(Path.GetTempPath(), "vecto-crash.log");
            File.AppendAllText(log, $"{DateTime.Now:O}\n{ex.Exception}\n\n");
            MessageBox.Show(ex.Exception.Message, "Vecto — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        if (e.Args.Contains("--smoke"))
        {
            RunSmoke();
            return;
        }
        var window = new MainWindow();
        window.Show();
        var file = e.Args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (file != null && File.Exists(file)) window.TryLoad(file);
    }

    /// <summary>Headless self-test (`Vecto.exe --smoke`): trace a generated image, write the SVG, exit 0/11.</summary>
    void RunSmoke()
    {
        try
        {
            var img = new RasterImage(96, 96);
            for (int y = 0; y < 96; y++)
            {
                for (int x = 0; x < 96; x++)
                {
                    double d = Math.Sqrt((x - 48.0) * (x - 48.0) + (y - 48.0) * (y - 48.0));
                    img.SetPixel(x, y, d < 30 ? new Rgba32(20, 60, 200, 255) : new Rgba32(255, 255, 255, 255));
                }
            }
            var result = Tracer.Trace(img, new TraceOptions());
            var svg = SvgWriter.Write(result.Document);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "vecto-smoke.svg"), svg);
            if (result.Document.Regions.Count != 2 || !svg.Contains("<path"))
            {
                Environment.Exit(11);
            }
            RenderingSmoke.Run();
            Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "vecto-smoke-error.log"), ex.ToString());
            Environment.Exit(11);
        }
    }
}
