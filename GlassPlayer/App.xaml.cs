using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace GlassPlayer;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Report(e.Exception);
            e.Handled = true;
            if (MainWindow == null || !MainWindow.IsVisible) Shutdown(); // failed while starting
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
    }

    static void Report(Exception? ex)
    {
        string path = "";
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassPlayer");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, "crash.log");
            File.AppendAllText(path, $"{DateTime.Now}\n{ex}\n\n");
        }
        catch { }
        MessageBox.Show("Glass ran into a problem:\n\n" + ex?.Message + (path.Length > 0 ? "\n\nDetails saved to:\n" + path : ""), "Glass");
    }
}
