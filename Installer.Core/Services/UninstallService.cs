using Microsoft.Web.Administration;

namespace Installer.Core.Services;

/// <summary>
/// Service for uninstalling applications including IIS cleanup and file removal.
/// </summary>
public class UninstallService
{
    private readonly IISDeploymentService _iisService;
    private readonly WindowsRegistryService _registryService;

    public UninstallService()
    {
        _iisService = new IISDeploymentService();
        _registryService = new WindowsRegistryService();
    }

    public UninstallService(IISDeploymentService iisService, WindowsRegistryService registryService)
    {
        _iisService = iisService;
        _registryService = registryService;
    }

    /// <summary>
    /// Performs a complete uninstallation of the application
    /// </summary>
    public async Task<UninstallResult> UninstallAsync(
        UninstallOptions options,
        IProgress<UninstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new UninstallResult();
        var steps = new List<string>();

        try
        {
            // Step 1: Stop and remove IIS site if specified
            if (!string.IsNullOrEmpty(options.IISSiteName))
            {
                progress?.Report(new UninstallProgress
                {
                    Step = "Stopping IIS site...",
                    PercentComplete = 10
                });

                var stopResult = await _iisService.StopSiteAndWaitAsync(options.IISSiteName, TimeSpan.FromMinutes(2));
                if (stopResult.Success)
                {
                    steps.Add($"Stopped IIS site '{options.IISSiteName}'");
                }

                if (options.RemoveIISSite)
                {
                    progress?.Report(new UninstallProgress
                    {
                        Step = "Removing IIS site...",
                        PercentComplete = 20
                    });

                    var removeResult = RemoveIISSite(options.IISSiteName, options.RemoveAppPool);
                    if (removeResult.Success)
                    {
                        steps.Add($"Removed IIS site '{options.IISSiteName}'");
                        if (options.RemoveAppPool)
                        {
                            steps.Add("Removed associated application pool");
                        }
                    }
                    else
                    {
                        result.Warnings.Add($"Could not remove IIS site: {removeResult.ErrorMessage}");
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Step 2: Remove application files
            if (options.RemoveFiles && !string.IsNullOrEmpty(options.InstallLocation))
            {
                progress?.Report(new UninstallProgress
                {
                    Step = "Removing application files...",
                    PercentComplete = 50
                });

                var filesResult = await RemoveFilesAsync(options.InstallLocation, options.PreserveConfig, cancellationToken);
                if (filesResult.Success)
                {
                    steps.Add($"Removed files from '{options.InstallLocation}'");
                    if (options.PreserveConfig)
                    {
                        steps.Add("Configuration files were preserved");
                    }
                }
                else
                {
                    result.Warnings.Add($"Some files could not be removed: {filesResult.ErrorMessage}");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Step 3: Remove from Programs and Features
            if (!string.IsNullOrEmpty(options.ProductCode))
            {
                progress?.Report(new UninstallProgress
                {
                    Step = "Removing from Programs and Features...",
                    PercentComplete = 80
                });

                var regResult = _registryService.UnregisterApplication(options.ProductCode);
                if (regResult.Success)
                {
                    steps.Add("Removed from Programs and Features");
                }
                else
                {
                    result.Warnings.Add($"Could not remove registry entry: {regResult.ErrorMessage}");
                }
            }

            progress?.Report(new UninstallProgress
            {
                Step = "Uninstall complete",
                PercentComplete = 100
            });

            result.Success = true;
            result.StepsCompleted = steps;
            result.Message = "Application uninstalled successfully";

            return result;
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Message = "Uninstall was cancelled";
            result.StepsCompleted = steps;
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Uninstall failed: {ex.Message}";
            result.StepsCompleted = steps;
            result.Exception = ex;
            return result;
        }
    }

    /// <summary>
    /// Removes an IIS site and optionally its application pool
    /// </summary>
    private UninstallStepResult RemoveIISSite(string siteName, bool removeAppPool)
    {
        try
        {
            using var serverManager = new ServerManager();

            var site = serverManager.Sites[siteName];
            if (site == null)
            {
                return new UninstallStepResult
                {
                    Success = true,
                    Message = "Site was already removed"
                };
            }

            // Get app pool name before removing site
            string? appPoolName = null;
            if (removeAppPool)
            {
                appPoolName = site.Applications["/"]?.ApplicationPoolName;
            }

            // Remove site
            serverManager.Sites.Remove(site);

            // Remove app pool if requested and not used by other sites
            if (removeAppPool && !string.IsNullOrEmpty(appPoolName))
            {
                var appPool = serverManager.ApplicationPools[appPoolName];
                if (appPool != null)
                {
                    // Check if any other site uses this app pool
                    bool inUse = serverManager.Sites
                        .Any(s => s.Name != siteName &&
                                  s.Applications.Any(a => a.ApplicationPoolName == appPoolName));

                    if (!inUse)
                    {
                        serverManager.ApplicationPools.Remove(appPool);
                    }
                }
            }

            serverManager.CommitChanges();

            return new UninstallStepResult
            {
                Success = true,
                Message = "IIS site removed successfully"
            };
        }
        catch (Exception ex)
        {
            return new UninstallStepResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Removes application files from the install location
    /// </summary>
    private async Task<UninstallStepResult> RemoveFilesAsync(
        string installLocation,
        bool preserveConfig,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(installLocation))
            {
                return new UninstallStepResult
                {
                    Success = true,
                    Message = "Install location does not exist"
                };
            }

            var configFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "appsettings.json",
                "appsettings.production.json",
                "appsettings.development.json",
                "connectionstrings.json",
                "web.config"
            };

            var failedFiles = new List<string>();

            // Delete files
            foreach (var file in Directory.GetFiles(installLocation, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileName = Path.GetFileName(file);

                // Skip config files if preserving
                if (preserveConfig && configFiles.Contains(fileName))
                    continue;

                try
                {
                    File.Delete(file);
                }
                catch
                {
                    failedFiles.Add(file);
                }
            }

            // Delete empty directories (bottom-up)
            await Task.Run(() =>
            {
                foreach (var dir in Directory.GetDirectories(installLocation, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length))
                {
                    try
                    {
                        if (Directory.GetFileSystemEntries(dir).Length == 0)
                        {
                            Directory.Delete(dir);
                        }
                    }
                    catch
                    {
                        // Ignore directory deletion failures
                    }
                }

                // Try to delete the root folder if empty or not preserving config
                if (!preserveConfig || Directory.GetFileSystemEntries(installLocation).Length == 0)
                {
                    try
                    {
                        Directory.Delete(installLocation, recursive: false);
                    }
                    catch
                    {
                        // Root folder might not be empty
                    }
                }
            }, cancellationToken);

            if (failedFiles.Count > 0)
            {
                return new UninstallStepResult
                {
                    Success = true,
                    Message = $"Removed files with {failedFiles.Count} failures",
                    ErrorMessage = $"Could not delete: {string.Join(", ", failedFiles.Take(5))}"
                };
            }

            return new UninstallStepResult
            {
                Success = true,
                Message = "All files removed successfully"
            };
        }
        catch (Exception ex)
        {
            return new UninstallStepResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Gets uninstall information from the registry
    /// </summary>
    public UninstallOptions? GetUninstallInfo(string productCode)
    {
        var registration = _registryService.GetRegistration(productCode);
        if (registration == null)
            return null;

        // Try to determine IIS site name from install location
        string? siteName = null;
        try
        {
            using var serverManager = new ServerManager();
            foreach (var site in serverManager.Sites)
            {
                var vdir = site.Applications["/"]?.VirtualDirectories["/"];
                if (vdir != null)
                {
                    var physPath = Environment.ExpandEnvironmentVariables(vdir.PhysicalPath);
                    if (string.Equals(physPath, registration.InstallLocation, StringComparison.OrdinalIgnoreCase))
                    {
                        siteName = site.Name;
                        break;
                    }
                }
            }
        }
        catch
        {
            // IIS not available
        }

        return new UninstallOptions
        {
            ProductCode = productCode,
            InstallLocation = registration.InstallLocation,
            IISSiteName = siteName,
            RemoveIISSite = true,
            RemoveAppPool = true,
            RemoveFiles = true,
            PreserveConfig = false
        };
    }
}

/// <summary>
/// Options for uninstalling an application
/// </summary>
public class UninstallOptions
{
    /// <summary>
    /// Product code used for registry lookup
    /// </summary>
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>
    /// Installation folder path
    /// </summary>
    public string InstallLocation { get; set; } = string.Empty;

    /// <summary>
    /// IIS site name (if deployed to IIS)
    /// </summary>
    public string? IISSiteName { get; set; }

    /// <summary>
    /// Whether to remove the IIS site
    /// </summary>
    public bool RemoveIISSite { get; set; } = true;

    /// <summary>
    /// Whether to remove the application pool
    /// </summary>
    public bool RemoveAppPool { get; set; } = true;

    /// <summary>
    /// Whether to remove application files
    /// </summary>
    public bool RemoveFiles { get; set; } = true;

    /// <summary>
    /// Whether to preserve configuration files during uninstall
    /// </summary>
    public bool PreserveConfig { get; set; } = false;
}

/// <summary>
/// Result of an uninstall operation
/// </summary>
public class UninstallResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> StepsCompleted { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public Exception? Exception { get; set; }
}

/// <summary>
/// Progress information during uninstall
/// </summary>
public class UninstallProgress
{
    public string Step { get; set; } = string.Empty;
    public int PercentComplete { get; set; }
}

/// <summary>
/// Result of a single uninstall step
/// </summary>
internal class UninstallStepResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}
