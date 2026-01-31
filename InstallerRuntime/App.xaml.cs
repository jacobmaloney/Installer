using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;

namespace InstallerRuntime;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Global exception handlers to catch any unhandled errors
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            ShowError("Unhandled Exception", ex?.ToString() ?? "Unknown error");
        };

        DispatcherUnhandledException += (s, args) =>
        {
            ShowError("Dispatcher Exception", args.Exception.ToString());
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            ShowError("Task Exception", args.Exception.ToString());
            args.SetObserved();
        };

        try
        {
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            ShowError("Startup Exception", ex.ToString());
        }
    }

    private void ShowError(string title, string message)
    {
        try
        {
            // Try to write to a log file
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "InstallerRuntime-Error.log");
            File.WriteAllText(logPath, $"{title}\n\n{message}");
        }
        catch { }

        System.Windows.MessageBox.Show(message, $"Installer Error: {title}",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

