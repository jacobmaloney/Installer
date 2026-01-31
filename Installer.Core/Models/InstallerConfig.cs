namespace Installer.Core.Models;

/// <summary>
/// Configuration for the Identity Center installer/upgrade process.
/// </summary>
public class InstallerConfig
{
    // Source settings
    public string SolutionPath { get; set; } = string.Empty;
    public string ApplicationName { get; set; } = "IdentityCenter";
    public string OutputPath { get; set; } = string.Empty;

    // IIS settings
    public string SiteName { get; set; } = "IdentityCenter";
    public int Port { get; set; } = 443;
    public string HostName { get; set; } = string.Empty;
    public bool CreateNewSite { get; set; } = true;
    public string AppPoolName { get; set; } = "IdentityCenterAppPool";
    public bool UseHttps { get; set; } = true;
    public string? SslCertificateThumbprint { get; set; }

    // Installation settings
    public string InstallPath { get; set; } = @"C:\inetpub\wwwroot\IdentityCenter";
    public InstallationType InstallationType { get; set; } = InstallationType.Fresh;

    // Upgrade settings
    public bool PreserveConfiguration { get; set; } = true;
    public bool CreateBackup { get; set; } = true;
    public string? ExistingInstallPath { get; set; }
    public bool VerifyAfterInstall { get; set; } = true;
    public bool LaunchBrowserAfterInstall { get; set; } = true;

    // Version info (populated during packaging)
    public VersionInfo? Version { get; set; }
    public string? PackageSource { get; set; }
}

/// <summary>
/// Type of installation being performed.
/// </summary>
public enum InstallationType
{
    /// <summary>Fresh installation to a new location</summary>
    Fresh,

    /// <summary>Upgrade an existing installation</summary>
    Upgrade,

    /// <summary>Repair an existing installation</summary>
    Repair,

    /// <summary>Uninstall (remove files and IIS config)</summary>
    Uninstall
}
