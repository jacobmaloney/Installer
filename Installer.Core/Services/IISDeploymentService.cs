using Microsoft.Web.Administration;
using System.Security.AccessControl;
using System.Security.Principal;
using Installer.Core.Models;

namespace Installer.Core.Services;

/// <summary>
/// Service for deploying and managing Identity Center in IIS.
/// Supports fresh installs, upgrades, and site management.
/// </summary>
public class IISDeploymentService
{
    public class DeploymentResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public Exception? Exception { get; set; }
        public string? SiteUrl { get; set; }
        public string? PhysicalPath { get; set; }
    }

    /// <summary>
    /// Deploys an application to IIS
    /// </summary>
    public DeploymentResult DeployToIIS(
        string siteName,
        string physicalPath,
        string appPoolName,
        int port,
        string hostName = "",
        bool createNewSite = true)
    {
        try
        {
            using var serverManager = new ServerManager();

            // Create or get app pool
            var appPool = CreateOrGetAppPool(serverManager, appPoolName);

            if (createNewSite)
            {
                // Create new site
                var site = CreateSite(serverManager, siteName, physicalPath, appPoolName, port, hostName);
                serverManager.CommitChanges();

                // Set folder permissions
                SetFolderPermissions(physicalPath, appPool.ProcessModel.IdentityType);

                return new DeploymentResult
                {
                    Success = true,
                    Message = $"Successfully created site '{siteName}' on port {port}"
                };
            }
            else
            {
                // Add as application to default site
                var site = serverManager.Sites["Default Web Site"];
                if (site == null)
                {
                    return new DeploymentResult
                    {
                        Success = false,
                        Message = "Default Web Site not found"
                    };
                }

                var app = site.Applications.Add($"/{siteName}", physicalPath);
                app.ApplicationPoolName = appPoolName;
                serverManager.CommitChanges();

                // Set folder permissions
                SetFolderPermissions(physicalPath, appPool.ProcessModel.IdentityType);

                return new DeploymentResult
                {
                    Success = true,
                    Message = $"Successfully added application to Default Web Site at /{siteName}"
                };
            }
        }
        catch (Exception ex)
        {
            return new DeploymentResult
            {
                Success = false,
                Message = $"Deployment failed: {ex.Message}",
                Exception = ex
            };
        }
    }

    private ApplicationPool CreateOrGetAppPool(ServerManager serverManager, string appPoolName)
    {
        var appPool = serverManager.ApplicationPools[appPoolName];

        if (appPool == null)
        {
            appPool = serverManager.ApplicationPools.Add(appPoolName);
            appPool.ManagedRuntimeVersion = ""; // No Managed Code for .NET Core
            appPool.ProcessModel.IdentityType = ProcessModelIdentityType.ApplicationPoolIdentity;
            appPool.Enable32BitAppOnWin64 = false;
        }

        return appPool;
    }

    private Site CreateSite(
        ServerManager serverManager,
        string siteName,
        string physicalPath,
        string appPoolName,
        int port,
        string hostName)
    {
        // Remove existing site if it exists
        var existingSite = serverManager.Sites[siteName];
        if (existingSite != null)
        {
            serverManager.Sites.Remove(existingSite);
        }

        // Create site with binding
        var site = serverManager.Sites.Add(siteName, "http", $"*:{port}:{hostName}", physicalPath);
        site.ApplicationDefaults.ApplicationPoolName = appPoolName;

        return site;
    }

    private void SetFolderPermissions(string physicalPath, ProcessModelIdentityType identityType)
    {
        try
        {
            var directory = new DirectoryInfo(physicalPath);
            var security = directory.GetAccessControl();

            // Get the app pool identity
            string identity = identityType switch
            {
                ProcessModelIdentityType.ApplicationPoolIdentity => "IIS AppPool\\DefaultAppPool",
                ProcessModelIdentityType.NetworkService => "NETWORK SERVICE",
                ProcessModelIdentityType.LocalService => "LOCAL SERVICE",
                ProcessModelIdentityType.LocalSystem => "LOCAL SYSTEM",
                _ => "IIS_IUSRS"
            };

            // Add read/execute permissions
            var rule = new FileSystemAccessRule(
                identity,
                FileSystemRights.ReadAndExecute | FileSystemRights.ListDirectory,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow);

            security.AddAccessRule(rule);
            directory.SetAccessControl(security);
        }
        catch
        {
            // Permission setting is best-effort
            // Don't fail deployment if this doesn't work
        }
    }

    /// <summary>
    /// Starts an IIS site
    /// </summary>
    public DeploymentResult StartSite(string siteName)
    {
        try
        {
            using var serverManager = new ServerManager();
            var site = serverManager.Sites[siteName];

            if (site == null)
            {
                return new DeploymentResult
                {
                    Success = false,
                    Message = $"Site '{siteName}' not found"
                };
            }

            if (site.State != ObjectState.Started && site.State != ObjectState.Starting)
            {
                site.Start();
                return new DeploymentResult
                {
                    Success = true,
                    Message = $"Site '{siteName}' started successfully"
                };
            }

            return new DeploymentResult
            {
                Success = true,
                Message = $"Site '{siteName}' is already running"
            };
        }
        catch (Exception ex)
        {
            return new DeploymentResult
            {
                Success = false,
                Message = $"Failed to start site: {ex.Message}",
                Exception = ex
            };
        }
    }

    /// <summary>
    /// Stops an IIS site
    /// </summary>
    public DeploymentResult StopSite(string siteName)
    {
        try
        {
            using var serverManager = new ServerManager();
            var site = serverManager.Sites[siteName];

            if (site == null)
            {
                return new DeploymentResult
                {
                    Success = false,
                    Message = $"Site '{siteName}' not found"
                };
            }

            if (site.State != ObjectState.Stopped && site.State != ObjectState.Stopping)
            {
                site.Stop();
                return new DeploymentResult
                {
                    Success = true,
                    Message = $"Site '{siteName}' stopped successfully"
                };
            }

            return new DeploymentResult
            {
                Success = true,
                Message = $"Site '{siteName}' is already stopped"
            };
        }
        catch (Exception ex)
        {
            return new DeploymentResult
            {
                Success = false,
                Message = $"Failed to stop site: {ex.Message}",
                Exception = ex
            };
        }
    }

    /// <summary>
    /// Restarts IIS
    /// </summary>
    public DeploymentResult RestartIIS()
    {
        try
        {
            var processInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "iisreset",
                Arguments = "/restart",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = System.Diagnostics.Process.Start(processInfo);
            process?.WaitForExit();

            if (process?.ExitCode == 0)
            {
                return new DeploymentResult
                {
                    Success = true,
                    Message = "IIS restarted successfully"
                };
            }

            return new DeploymentResult
            {
                Success = false,
                Message = "IIS restart failed"
            };
        }
        catch (Exception ex)
        {
            return new DeploymentResult
            {
                Success = false,
                Message = $"Failed to restart IIS: {ex.Message}",
                Exception = ex
            };
        }
    }

    /// <summary>
    /// Gets information about an existing IIS site
    /// </summary>
    public SiteInfo? GetSiteInfo(string siteName)
    {
        try
        {
            using var serverManager = new ServerManager();
            var site = serverManager.Sites[siteName];

            if (site == null)
                return null;

            var app = site.Applications["/"];
            var vdir = app?.VirtualDirectories["/"];

            return new SiteInfo
            {
                Name = site.Name,
                State = site.State.ToString(),
                PhysicalPath = vdir != null ? Environment.ExpandEnvironmentVariables(vdir.PhysicalPath) : string.Empty,
                AppPoolName = app?.ApplicationPoolName ?? string.Empty,
                Bindings = site.Bindings.Select(b => new BindingInfo
                {
                    Protocol = b.Protocol,
                    BindingInformation = b.BindingInformation,
                    Host = b.Host,
                    Port = b.EndPoint?.Port ?? 0
                }).ToList()
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lists all IIS sites that might be Identity Center installations
    /// </summary>
    public List<SiteInfo> FindIdentityCenterSites()
    {
        var sites = new List<SiteInfo>();

        try
        {
            using var serverManager = new ServerManager();

            foreach (var site in serverManager.Sites)
            {
                var app = site.Applications["/"];
                var vdir = app?.VirtualDirectories["/"];

                if (vdir == null) continue;

                var physicalPath = Environment.ExpandEnvironmentVariables(vdir.PhysicalPath);

                // Check if it looks like an Identity Center installation
                var webConfig = Path.Combine(physicalPath, "web.config");
                var appsettings = Path.Combine(physicalPath, "appsettings.json");
                var webportalDll = Path.Combine(physicalPath, "WebPortal.dll");

                if (File.Exists(webportalDll) || (File.Exists(webConfig) && File.Exists(appsettings)))
                {
                    sites.Add(new SiteInfo
                    {
                        Name = site.Name,
                        State = site.State.ToString(),
                        PhysicalPath = physicalPath,
                        AppPoolName = app?.ApplicationPoolName ?? string.Empty,
                        Bindings = site.Bindings.Select(b => new BindingInfo
                        {
                            Protocol = b.Protocol,
                            BindingInformation = b.BindingInformation,
                            Host = b.Host,
                            Port = b.EndPoint?.Port ?? 0
                        }).ToList()
                    });
                }
            }
        }
        catch
        {
            // IIS not accessible
        }

        return sites;
    }

    /// <summary>
    /// Recycles the application pool for a site
    /// </summary>
    public DeploymentResult RecycleAppPool(string appPoolName)
    {
        try
        {
            using var serverManager = new ServerManager();
            var appPool = serverManager.ApplicationPools[appPoolName];

            if (appPool == null)
            {
                return new DeploymentResult
                {
                    Success = false,
                    Message = $"Application pool '{appPoolName}' not found"
                };
            }

            appPool.Recycle();

            return new DeploymentResult
            {
                Success = true,
                Message = $"Application pool '{appPoolName}' recycled successfully"
            };
        }
        catch (Exception ex)
        {
            return new DeploymentResult
            {
                Success = false,
                Message = $"Failed to recycle app pool: {ex.Message}",
                Exception = ex
            };
        }
    }

    /// <summary>
    /// Stops and waits for worker processes to terminate
    /// </summary>
    public async Task<DeploymentResult> StopSiteAndWaitAsync(string siteName, TimeSpan timeout)
    {
        var stopResult = StopSite(siteName);
        if (!stopResult.Success)
            return stopResult;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            using var serverManager = new ServerManager();
            var site = serverManager.Sites[siteName];

            if (site?.State == ObjectState.Stopped)
            {
                // Also check that app pool has no worker processes
                var appPoolName = site.Applications["/"]?.ApplicationPoolName;
                if (!string.IsNullOrEmpty(appPoolName))
                {
                    var appPool = serverManager.ApplicationPools[appPoolName];
                    if (appPool?.WorkerProcesses.Count == 0)
                    {
                        return new DeploymentResult
                        {
                            Success = true,
                            Message = $"Site '{siteName}' stopped and all worker processes terminated"
                        };
                    }
                }
                else
                {
                    return stopResult;
                }
            }

            await Task.Delay(500);
        }

        return new DeploymentResult
        {
            Success = false,
            Message = $"Timeout waiting for site '{siteName}' to fully stop"
        };
    }

    /// <summary>
    /// Performs a complete upgrade deployment
    /// </summary>
    public async Task<DeploymentResult> DeployUpgradeAsync(
        string siteName,
        string sourcePath,
        string targetPath,
        FileBackupService backupService,
        ConfigurationPreserver configPreserver,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. Stop the site
            progress?.Report("Stopping IIS site...");
            var stopResult = await StopSiteAndWaitAsync(siteName, TimeSpan.FromMinutes(2));
            if (!stopResult.Success)
                return stopResult;

            // 2. Backup current files
            progress?.Report("Creating backup...");
            var backupResult = await backupService.CreateBackupAsync(targetPath, cancellationToken: cancellationToken);
            if (!backupResult.Success)
            {
                // Try to restart site before returning error
                StartSite(siteName);
                return new DeploymentResult
                {
                    Success = false,
                    Message = $"Backup failed: {backupResult.ErrorMessage}"
                };
            }

            // 3. Preserve configuration
            progress?.Report("Preserving configuration...");
            var preservedConfig = await configPreserver.ExtractConfigurationAsync(targetPath, cancellationToken);

            // 4. Deploy new files
            progress?.Report("Deploying new files...");
            await DeployFilesAsync(sourcePath, targetPath, cancellationToken);

            // 5. Restore configuration
            progress?.Report("Restoring configuration...");
            await configPreserver.ApplyConfigurationAsync(targetPath, preservedConfig, cancellationToken);

            // 6. Start the site
            progress?.Report("Starting IIS site...");
            var startResult = StartSite(siteName);

            return new DeploymentResult
            {
                Success = startResult.Success,
                Message = startResult.Success
                    ? "Upgrade completed successfully"
                    : $"Upgrade completed but site failed to start: {startResult.Message}",
                PhysicalPath = targetPath
            };
        }
        catch (Exception ex)
        {
            // Try to restart site on failure
            try { StartSite(siteName); } catch { }

            return new DeploymentResult
            {
                Success = false,
                Message = $"Upgrade failed: {ex.Message}",
                Exception = ex
            };
        }
    }

    private async Task DeployFilesAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        // Ensure target directory exists
        Directory.CreateDirectory(targetPath);

        // Copy all files from source to target
        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourcePath, file);
            var targetFile = Path.Combine(targetPath, relativePath);
            var targetDir = Path.GetDirectoryName(targetFile);

            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // Skip certain files that should be preserved
            var fileName = Path.GetFileName(file).ToLowerInvariant();
            if (fileName == "appsettings.json" || fileName == "appsettings.production.json")
            {
                if (File.Exists(targetFile))
                    continue; // Don't overwrite config files
            }

            await Task.Run(() => File.Copy(file, targetFile, overwrite: true), cancellationToken);
        }
    }
}

/// <summary>
/// Information about an IIS site
/// </summary>
public class SiteInfo
{
    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string PhysicalPath { get; set; } = string.Empty;
    public string AppPoolName { get; set; } = string.Empty;
    public List<BindingInfo> Bindings { get; set; } = new();

    public string GetPrimaryUrl()
    {
        var binding = Bindings.FirstOrDefault(b => b.Protocol == "https")
                   ?? Bindings.FirstOrDefault();

        if (binding == null) return string.Empty;

        var host = string.IsNullOrEmpty(binding.Host) ? "localhost" : binding.Host;
        var port = binding.Port;
        var portSuffix = (binding.Protocol == "https" && port == 443) ||
                        (binding.Protocol == "http" && port == 80)
                        ? "" : $":{port}";

        return $"{binding.Protocol}://{host}{portSuffix}";
    }
}

/// <summary>
/// Information about an IIS binding
/// </summary>
public class BindingInfo
{
    public string Protocol { get; set; } = string.Empty;
    public string BindingInformation { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
}
