using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Installer.Core.Models;
using Installer.Core.Services.Conduit;

namespace InstallerRuntime;

/// <summary>
/// No-UI entry path for the unattended Conduit install. Triggered by --silent
/// (with optional --config &lt;path&gt;) or by a conduit.provision.json sidecar
/// next to the installer exe. All output goes to a log file in %TEMP%
/// (mirrored to the parent console when one is attached), and the process
/// exit code follows <see cref="SilentExitCode"/>.
/// </summary>
public static class SilentInstall
{
    public static async Task<int> RunAsync(string? configPath)
    {
        AttachParentConsole();

        var logPath = Path.Combine(Path.GetTempPath(), $"ConduitInstall-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        using var log = new SilentInstallLog(logPath, mirror: WriteConsoleLine);
        WriteConsoleLine($"Conduit unattended install — log: {logPath}");

        ConduitInstallOptions options;
        try
        {
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            {
                log.Error($"Sidecar config not found. Place {ConduitInstallOptions.SidecarFileName} next to the installer or pass --config <path>.");
                return (int)SilentExitCode.ConfigError;
            }

            options = ConduitInstallOptions.Parse(await File.ReadAllTextAsync(configPath));
        }
        catch (JsonException ex)
        {
            log.Error($"Sidecar config is not valid JSON: {ex.Message}");
            return (int)SilentExitCode.ConfigError;
        }

        var validationErrors = options.Validate();
        if (validationErrors.Count > 0)
        {
            foreach (var error in validationErrors)
                log.Error($"Sidecar config invalid: {error}");
            return (int)SilentExitCode.ConfigError;
        }

        try
        {
            var installerDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var installer = new ConduitSilentInstaller();
            var exitCode = await installer.RunAsync(options, installerDirectory, log);
            log.Info($"Exit code: {(int)exitCode} ({exitCode}).");
            CopyLogToInstallDir(logPath, options.InstallPath);
            return (int)exitCode;
        }
        catch (Exception ex)
        {
            log.Error($"Unexpected error: {ex}");
            return (int)SilentExitCode.UnknownError;
        }
    }

    private static void CopyLogToInstallDir(string logPath, string installPath)
    {
        try
        {
            if (Directory.Exists(installPath))
                File.Copy(logPath, Path.Combine(installPath, "install.log"), overwrite: true);
        }
        catch
        {
            // The %TEMP% log remains authoritative.
        }
    }

    // ── Parent-console attach (WinExe has no console of its own) ───────
    private const int AttachParentProcess = -1;
    private static bool _consoleAttached;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private static void AttachParentConsole()
    {
        try
        {
            _consoleAttached = AttachConsole(AttachParentProcess);
        }
        catch
        {
            _consoleAttached = false;
        }
    }

    private static void WriteConsoleLine(string line)
    {
        if (!_consoleAttached)
            return;
        try { Console.WriteLine(line); } catch { }
    }
}
