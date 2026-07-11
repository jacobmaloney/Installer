using System.Diagnostics;
using System.ServiceProcess;

namespace Installer.Core.Services.Conduit;

/// <summary>
/// Installs and starts the Conduit Windows service via sc.exe, registers the
/// "Conduit" event-log source (needs elevation — the service account cannot
/// create it at runtime), and configures failure recovery (restart on crash).
/// Command lines are built by static methods so they are unit-testable.
/// </summary>
public class ConduitServiceInstaller
{
    public const string DefaultServiceName = "IdentityCenterConduit";
    public const string EventLogSource = "Conduit";
    private const string DisplayName = "Identity Center Conduit";

    // sc.exe syntax quirk: the space AFTER each option= is required.
    // binPath needs embedded quotes because the exe path contains spaces.
    public static string BuildCreateArguments(string serviceName, string exePath) =>
        $"create {serviceName} binPath= \"\\\"{exePath}\\\"\" start= auto DisplayName= \"{DisplayName}\"";

    public static string BuildConfigArguments(string serviceName, string exePath) =>
        $"config {serviceName} binPath= \"\\\"{exePath}\\\"\" start= auto";

    public static string BuildDescriptionArguments(string serviceName) =>
        $"description {serviceName} \"Identity Center Conduit sync service.\"";

    public static string BuildFailureArguments(string serviceName) =>
        $"failure {serviceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000";

    public virtual bool ServiceExists(string serviceName) =>
        ServiceController.GetServices()
            .Any(s => string.Equals(s.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Registers the event-log source if missing. Returns null on success, error text on failure.</summary>
    public virtual string? RegisterEventLogSource()
    {
        try
        {
            if (!EventLog.SourceExists(EventLogSource))
                EventLog.CreateEventSource(EventLogSource, "Application");
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not register event-log source '{EventLogSource}': {ex.Message}";
        }
    }

    /// <summary>Stops the service and waits. Returns null on success (or not installed/already stopped).</summary>
    public virtual string? StopIfRunning(string serviceName, TimeSpan? timeout = null)
    {
        try
        {
            if (!ServiceExists(serviceName))
                return null;

            using var controller = new ServiceController(serviceName);
            if (controller.Status is ServiceControllerStatus.Stopped)
                return null;

            if (controller.Status is not ServiceControllerStatus.StopPending)
                controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout ?? TimeSpan.FromMinutes(2));
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not stop service '{serviceName}': {ex.Message}";
        }
    }

    /// <summary>Creates the service (or updates binPath on upgrade) and sets description + failure recovery.</summary>
    public virtual string? CreateOrUpdate(string serviceName, string exePath, SilentInstallLog log)
    {
        var exists = ServiceExists(serviceName);
        var error = RunSc(exists
            ? BuildConfigArguments(serviceName, exePath)
            : BuildCreateArguments(serviceName, exePath), log);
        if (error != null)
            return error;

        error = RunSc(BuildDescriptionArguments(serviceName), log);
        if (error != null)
            log.Warn(error); // cosmetic — not fatal

        error = RunSc(BuildFailureArguments(serviceName), log);
        if (error != null)
            log.Warn($"Failure-recovery flags not applied: {error}");

        return null;
    }

    /// <summary>Starts the service and waits for Running (service start includes DB init, so the timeout is generous).</summary>
    public virtual string? Start(string serviceName, TimeSpan? timeout = null)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            if (controller.Status != ServiceControllerStatus.Running)
            {
                if (controller.Status != ServiceControllerStatus.StartPending)
                    controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, timeout ?? TimeSpan.FromMinutes(5));
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"Service '{serviceName}' did not reach Running: {ex.Message}";
        }
    }

    private static string? RunSc(string arguments, SilentInstallLog log)
    {
        log.Info($"sc.exe {arguments}");
        var startInfo = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
            return "sc.exe failed to start.";

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);

        if (process.ExitCode != 0)
            return $"sc.exe exited {process.ExitCode}: {output.Trim()}";
        return null;
    }
}
