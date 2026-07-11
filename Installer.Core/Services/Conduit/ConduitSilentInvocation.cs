using Installer.Core.Models;

namespace Installer.Core.Services.Conduit;

/// <summary>
/// Resolves whether the installer was invoked in silent (unattended Conduit)
/// mode and which sidecar config to use: --silent forces silent mode,
/// --config sets the sidecar path, and a conduit.provision.json next to the
/// exe both triggers silent mode and serves as the config.
/// </summary>
public sealed record ConduitSilentInvocation(bool IsSilent, string? ConfigPath)
{
    public static ConduitSilentInvocation Resolve(string[] args, string installerDirectory)
    {
        var silent = false;
        string? configPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i].ToLowerInvariant();
            if (arg is "--silent" or "-silent" or "/silent" or "/s" or "--quiet" or "/q")
                silent = true;
            else if (arg is "--config" or "-config" or "/config" && i + 1 < args.Length)
                configPath = args[++i];
        }

        if (configPath == null)
        {
            var sidecar = Path.Combine(installerDirectory, ConduitInstallOptions.SidecarFileName);
            if (File.Exists(sidecar))
            {
                configPath = sidecar;
                silent = true;
            }
        }

        return new ConduitSilentInvocation(silent, configPath);
    }
}
