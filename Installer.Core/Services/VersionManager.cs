using System.Reflection;
using Installer.Core.Models;

namespace Installer.Core.Services;

/// <summary>
/// Manages version detection, comparison, and tracking for Identity Center installations.
/// </summary>
public class VersionManager
{
    /// <summary>
    /// Gets the current version from the installer assembly
    /// </summary>
    public VersionInfo GetInstallerVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version ?? new Version(1, 0, 0, 0);
        return VersionInfo.FromVersion(version);
    }

    /// <summary>
    /// Gets the version from a published application's assembly
    /// </summary>
    public VersionInfo? GetApplicationVersion(string installPath)
    {
        try
        {
            var webDll = Path.Combine(installPath, "WebPortal.dll");
            if (!File.Exists(webDll))
            {
                // Try alternate name
                webDll = Path.Combine(installPath, "IdentityCenter.dll");
            }

            if (!File.Exists(webDll))
                return null;

            var assemblyName = AssemblyName.GetAssemblyName(webDll);
            var version = assemblyName.Version;
            return version != null ? VersionInfo.FromVersion(version) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Detects an existing installation at the specified path
    /// </summary>
    public async Task<InstallationInfo?> DetectExistingInstallationAsync(string installPath, CancellationToken cancellationToken = default)
    {
        // First check for manifest
        var manifest = await InstallManifest.LoadAsync(installPath, cancellationToken);
        if (manifest != null)
        {
            return new InstallationInfo
            {
                Found = true,
                InstallPath = installPath,
                Version = manifest.Version,
                InstalledAt = manifest.InstalledAt,
                LastUpdatedAt = manifest.LastUpdatedAt,
                SiteName = manifest.SiteName,
                HasManifest = true,
                Manifest = manifest
            };
        }

        // Fall back to detecting from assemblies
        var appVersion = GetApplicationVersion(installPath);
        if (appVersion != null)
        {
            // Check for web.config to confirm it's an Identity Center installation
            var webConfig = Path.Combine(installPath, "web.config");
            var appsettings = Path.Combine(installPath, "appsettings.json");

            if (File.Exists(webConfig) || File.Exists(appsettings))
            {
                return new InstallationInfo
                {
                    Found = true,
                    InstallPath = installPath,
                    Version = appVersion,
                    HasManifest = false,
                    NeedsManifestCreation = true
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Scans common installation locations for existing Identity Center installations
    /// </summary>
    public async Task<List<InstallationInfo>> ScanForInstallationsAsync(CancellationToken cancellationToken = default)
    {
        var installations = new List<InstallationInfo>();

        // Common installation paths to check
        var pathsToCheck = new List<string>
        {
            @"C:\inetpub\wwwroot\IdentityCenter",
            @"C:\inetpub\IdentityCenter",
            @"C:\Program Files\IdentityCenter",
            @"C:\Program Files (x86)\IdentityCenter",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "IdentityCenter"),
        };

        // Also check IIS sites via ServerManager if available
        try
        {
            var iisPaths = await GetIISSitePathsAsync(cancellationToken);
            pathsToCheck.AddRange(iisPaths);
        }
        catch
        {
            // IIS might not be accessible
        }

        foreach (var path in pathsToCheck.Distinct())
        {
            if (!Directory.Exists(path))
                continue;

            var info = await DetectExistingInstallationAsync(path, cancellationToken);
            if (info != null)
            {
                installations.Add(info);
            }
        }

        return installations;
    }

    /// <summary>
    /// Creates a manifest for an existing installation that doesn't have one
    /// </summary>
    public async Task<InstallManifest> CreateManifestForExistingAsync(
        string installPath,
        string siteName,
        string appPoolName,
        int port,
        CancellationToken cancellationToken = default)
    {
        var version = GetApplicationVersion(installPath) ?? new VersionInfo(1, 0, 0, 0);

        var manifest = new InstallManifest
        {
            InstallPath = installPath,
            Version = version,
            SiteName = siteName,
            AppPoolName = appPoolName,
            Port = port,
            InstalledAt = Directory.GetCreationTimeUtc(installPath),
            LastUpdatedAt = DateTime.UtcNow
        };

        // Try to detect database name from appsettings
        var dbName = await TryGetDatabaseNameAsync(installPath, cancellationToken);
        if (!string.IsNullOrEmpty(dbName))
        {
            manifest.LastKnownDatabaseName = dbName;
        }

        await manifest.SaveAsync(cancellationToken);
        return manifest;
    }

    /// <summary>
    /// Compares two versions to determine upgrade path
    /// </summary>
    public UpgradePathInfo GetUpgradePath(VersionInfo current, VersionInfo target)
    {
        var comparison = target.CompareTo(current);

        return new UpgradePathInfo
        {
            CurrentVersion = current,
            TargetVersion = target,
            IsUpgrade = comparison > 0,
            IsDowngrade = comparison < 0,
            IsSameVersion = comparison == 0,
            IsMajorUpgrade = comparison > 0 && target.Major > current.Major,
            IsMinorUpgrade = comparison > 0 && target.Major == current.Major && target.Minor > current.Minor,
            IsPatchUpgrade = comparison > 0 && target.Major == current.Major && target.Minor == current.Minor && target.Patch > current.Patch,
            RequiresDatabaseMigration = comparison > 0 && (target.Major > current.Major || target.Minor > current.Minor)
        };
    }

    private async Task<List<string>> GetIISSitePathsAsync(CancellationToken cancellationToken)
    {
        var paths = new List<string>();

        try
        {
            // Use Microsoft.Web.Administration to get site paths
            using var serverManager = new Microsoft.Web.Administration.ServerManager();

            foreach (var site in serverManager.Sites)
            {
                foreach (var app in site.Applications)
                {
                    foreach (var vdir in app.VirtualDirectories)
                    {
                        if (!string.IsNullOrEmpty(vdir.PhysicalPath))
                        {
                            // Expand environment variables
                            var expandedPath = Environment.ExpandEnvironmentVariables(vdir.PhysicalPath);
                            paths.Add(expandedPath);
                        }
                    }
                }
            }
        }
        catch
        {
            // IIS not available or accessible
        }

        return await Task.FromResult(paths);
    }

    private async Task<string?> TryGetDatabaseNameAsync(string installPath, CancellationToken cancellationToken)
    {
        try
        {
            var appsettingsPath = Path.Combine(installPath, "appsettings.json");
            if (!File.Exists(appsettingsPath))
                return null;

            var json = await File.ReadAllTextAsync(appsettingsPath, cancellationToken);
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("ConnectionStrings", out var connStrings) &&
                connStrings.TryGetProperty("DefaultConnection", out var defaultConn))
            {
                var connectionString = defaultConn.GetString();
                if (!string.IsNullOrEmpty(connectionString))
                {
                    // Parse database name from connection string
                    var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
                    return builder.InitialCatalog;
                }
            }
        }
        catch
        {
            // Couldn't parse appsettings
        }

        return null;
    }
}

/// <summary>
/// Information about a detected installation
/// </summary>
public class InstallationInfo
{
    public bool Found { get; set; }
    public string InstallPath { get; set; } = string.Empty;
    public VersionInfo? Version { get; set; }
    public DateTime? InstalledAt { get; set; }
    public DateTime? LastUpdatedAt { get; set; }
    public string? SiteName { get; set; }
    public bool HasManifest { get; set; }
    public bool NeedsManifestCreation { get; set; }
    public InstallManifest? Manifest { get; set; }
}

/// <summary>
/// Information about an upgrade path between versions
/// </summary>
public class UpgradePathInfo
{
    public VersionInfo CurrentVersion { get; set; } = new();
    public VersionInfo TargetVersion { get; set; } = new();
    public bool IsUpgrade { get; set; }
    public bool IsDowngrade { get; set; }
    public bool IsSameVersion { get; set; }
    public bool IsMajorUpgrade { get; set; }
    public bool IsMinorUpgrade { get; set; }
    public bool IsPatchUpgrade { get; set; }
    public bool RequiresDatabaseMigration { get; set; }

    public string GetDescription()
    {
        if (IsSameVersion)
            return "Same version - reinstall/repair";
        if (IsDowngrade)
            return "Downgrade - not recommended";
        if (IsMajorUpgrade)
            return "Major upgrade - database migration required";
        if (IsMinorUpgrade)
            return "Minor upgrade - database migration may be required";
        if (IsPatchUpgrade)
            return "Patch upgrade - no database changes expected";
        return "Unknown upgrade path";
    }
}
