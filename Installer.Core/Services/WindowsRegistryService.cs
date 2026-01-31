using Microsoft.Win32;
using Installer.Core.Models;

namespace Installer.Core.Services;

/// <summary>
/// Service for registering/unregistering applications in Windows Programs and Features.
/// Handles registry entries for Add/Remove Programs.
/// </summary>
public class WindowsRegistryService
{
    private const string UninstallRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// Registers an application in Windows Programs and Features (Add/Remove Programs)
    /// </summary>
    public OperationResult RegisterApplication(ApplicationRegistration registration)
    {
        try
        {
            var keyPath = $@"{UninstallRegistryKey}\{registration.ProductCode}";

            using var key = Registry.LocalMachine.CreateSubKey(keyPath, writable: true);
            if (key == null)
            {
                return new OperationResult
                {
                    Success = false,
                    ErrorMessage = "Failed to create registry key. Ensure you have administrator privileges."
                };
            }

            // Required values
            key.SetValue("DisplayName", registration.DisplayName);
            key.SetValue("DisplayVersion", registration.DisplayVersion);
            key.SetValue("Publisher", registration.Publisher);
            key.SetValue("InstallLocation", registration.InstallLocation);
            key.SetValue("UninstallString", registration.UninstallString);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));

            // Optional values
            if (!string.IsNullOrEmpty(registration.DisplayIcon))
                key.SetValue("DisplayIcon", registration.DisplayIcon);

            if (!string.IsNullOrEmpty(registration.QuietUninstallString))
                key.SetValue("QuietUninstallString", registration.QuietUninstallString);

            if (!string.IsNullOrEmpty(registration.HelpLink))
                key.SetValue("HelpLink", registration.HelpLink);

            if (!string.IsNullOrEmpty(registration.URLInfoAbout))
                key.SetValue("URLInfoAbout", registration.URLInfoAbout);

            if (registration.EstimatedSizeKB > 0)
                key.SetValue("EstimatedSize", registration.EstimatedSizeKB, RegistryValueKind.DWord);

            // Behavior flags
            key.SetValue("NoModify", registration.NoModify ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("NoRepair", registration.NoRepair ? 1 : 0, RegistryValueKind.DWord);

            // Windows Installer detection (set to 0 for non-MSI installs)
            key.SetValue("WindowsInstaller", 0, RegistryValueKind.DWord);

            return new OperationResult
            {
                Success = true,
                Message = $"Successfully registered '{registration.DisplayName}' in Programs and Features"
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            return new OperationResult
            {
                Success = false,
                ErrorMessage = $"Access denied. Run as administrator. Details: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new OperationResult
            {
                Success = false,
                ErrorMessage = $"Failed to register application: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Unregisters an application from Windows Programs and Features
    /// </summary>
    public OperationResult UnregisterApplication(string productCode)
    {
        try
        {
            var keyPath = $@"{UninstallRegistryKey}\{productCode}";

            using var parentKey = Registry.LocalMachine.OpenSubKey(UninstallRegistryKey, writable: true);
            if (parentKey == null)
            {
                return new OperationResult
                {
                    Success = false,
                    ErrorMessage = "Could not open uninstall registry key"
                };
            }

            // Check if the key exists
            using var existingKey = parentKey.OpenSubKey(productCode);
            if (existingKey == null)
            {
                return new OperationResult
                {
                    Success = true,
                    Message = "Application was not registered (already unregistered)"
                };
            }

            parentKey.DeleteSubKeyTree(productCode);

            return new OperationResult
            {
                Success = true,
                Message = "Successfully removed application from Programs and Features"
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            return new OperationResult
            {
                Success = false,
                ErrorMessage = $"Access denied. Run as administrator. Details: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new OperationResult
            {
                Success = false,
                ErrorMessage = $"Failed to unregister application: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Checks if an application is registered in Programs and Features
    /// </summary>
    public bool IsApplicationRegistered(string productCode)
    {
        try
        {
            var keyPath = $@"{UninstallRegistryKey}\{productCode}";
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets registration info for an installed application
    /// </summary>
    public ApplicationRegistration? GetRegistration(string productCode)
    {
        try
        {
            var keyPath = $@"{UninstallRegistryKey}\{productCode}";
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);

            if (key == null)
                return null;

            return new ApplicationRegistration
            {
                ProductCode = productCode,
                DisplayName = key.GetValue("DisplayName")?.ToString() ?? string.Empty,
                DisplayVersion = key.GetValue("DisplayVersion")?.ToString() ?? string.Empty,
                Publisher = key.GetValue("Publisher")?.ToString() ?? string.Empty,
                InstallLocation = key.GetValue("InstallLocation")?.ToString() ?? string.Empty,
                UninstallString = key.GetValue("UninstallString")?.ToString() ?? string.Empty,
                DisplayIcon = key.GetValue("DisplayIcon")?.ToString(),
                QuietUninstallString = key.GetValue("QuietUninstallString")?.ToString(),
                EstimatedSizeKB = key.GetValue("EstimatedSize") is int size ? size : 0
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Calculates the size of an installation folder in KB
    /// </summary>
    public static int CalculateFolderSizeKB(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath))
                return 0;

            var dirInfo = new DirectoryInfo(folderPath);
            long totalBytes = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories)
                                     .Sum(file => file.Length);

            return (int)(totalBytes / 1024);
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>
/// Information needed to register an application in Programs and Features
/// </summary>
public class ApplicationRegistration
{
    /// <summary>
    /// Unique identifier for the application (e.g., "IdentityCenter" or a GUID)
    /// </summary>
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>
    /// Name shown in Programs and Features
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Version string (e.g., "1.0.0")
    /// </summary>
    public string DisplayVersion { get; set; } = string.Empty;

    /// <summary>
    /// Company/Publisher name
    /// </summary>
    public string Publisher { get; set; } = string.Empty;

    /// <summary>
    /// Installation folder path
    /// </summary>
    public string InstallLocation { get; set; } = string.Empty;

    /// <summary>
    /// Command to uninstall the application
    /// </summary>
    public string UninstallString { get; set; } = string.Empty;

    /// <summary>
    /// Command for silent uninstall (optional)
    /// </summary>
    public string? QuietUninstallString { get; set; }

    /// <summary>
    /// Path to icon file (optional)
    /// </summary>
    public string? DisplayIcon { get; set; }

    /// <summary>
    /// Help/support URL (optional)
    /// </summary>
    public string? HelpLink { get; set; }

    /// <summary>
    /// Product information URL (optional)
    /// </summary>
    public string? URLInfoAbout { get; set; }

    /// <summary>
    /// Estimated size in KB (optional)
    /// </summary>
    public int EstimatedSizeKB { get; set; }

    /// <summary>
    /// Hide Modify button in Programs and Features
    /// </summary>
    public bool NoModify { get; set; } = true;

    /// <summary>
    /// Hide Repair button in Programs and Features
    /// </summary>
    public bool NoRepair { get; set; } = true;
}
