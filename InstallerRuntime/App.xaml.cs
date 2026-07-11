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
    /// <summary>
    /// Indicates if running in uninstall mode
    /// </summary>
    public static bool IsUninstallMode { get; private set; }

    /// <summary>
    /// Product code passed for uninstall
    /// </summary>
    public static string? UninstallProductCode { get; private set; }

    /// <summary>
    /// True when running the no-UI unattended Conduit install (--silent /
    /// conduit.provision.json sidecar). No windows, no message boxes; the
    /// process exit code reports the outcome.
    /// </summary>
    public static bool IsSilentMode { get; private set; }

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

        // Parse command line arguments
        ParseCommandLineArgs(e.Args);

        // Silent unattended Conduit install: no windows, exit code is the result.
        // Explicit /uninstall always wins over a sidecar sitting next to the exe.
        var installerDirectory = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var silentInvocation = Installer.Core.Services.Conduit.ConduitSilentInvocation.Resolve(e.Args, installerDirectory);
        if (silentInvocation.IsSilent && !IsUninstallMode)
        {
            IsSilentMode = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);

            Task.Run(async () =>
            {
                var exitCode = await SilentInstall.RunAsync(silentInvocation.ConfigPath);
                Dispatcher.Invoke(() => Shutdown(exitCode));
            });
            return;
        }

        try
        {
            base.OnStartup(e);

            // Show appropriate window based on mode
            if (IsUninstallMode)
            {
                var uninstallWindow = new UninstallWindow();
                uninstallWindow.Show();
            }
            else
            {
                var mainWindow = new MainWindow();
                mainWindow.Show();
            }
        }
        catch (Exception ex)
        {
            ShowError("Startup Exception", ex.ToString());
        }
    }

    private void ParseCommandLineArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i].ToLowerInvariant();

            if (arg == "/uninstall" || arg == "-uninstall" || arg == "--uninstall")
            {
                IsUninstallMode = true;

                // Check for product code as next argument
                if (i + 1 < args.Length && !args[i + 1].StartsWith("/") && !args[i + 1].StartsWith("-"))
                {
                    UninstallProductCode = args[i + 1];
                    i++;
                }
            }
            else if (arg.StartsWith("/productcode=") || arg.StartsWith("-productcode=") || arg.StartsWith("--productcode="))
            {
                UninstallProductCode = arg.Substring(arg.IndexOf('=') + 1);
            }
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

        if (IsSilentMode)
            return;

        System.Windows.MessageBox.Show(message, $"Installer Error: {title}",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

