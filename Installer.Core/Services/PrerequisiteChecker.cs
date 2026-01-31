using Microsoft.Win32;

namespace Installer.Core.Services;

public class PrerequisiteChecker
{
    public class PrerequisiteResult
    {
        public bool IsInstalled { get; set; }
        public string? Version { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Checks if the ASP.NET Core Hosting Bundle is installed
    /// </summary>
    public PrerequisiteResult CheckAspNetCoreHostingBundle(string requiredVersion = "8.0")
    {
        var result = new PrerequisiteResult();

        try
        {
            // Check for aspnetcorev2.dll in the expected location
            string aspNetCoreModulePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "IIS", "Asp.Net Core Module", "V2", "aspnetcorev2.dll"
            );

            if (File.Exists(aspNetCoreModulePath))
            {
                var fileInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(aspNetCoreModulePath);
                result.IsInstalled = true;
                result.Version = fileInfo.FileVersion;
                result.Message = $"ASP.NET Core Hosting Bundle v{fileInfo.FileVersion} is installed";
                return result;
            }

            // Alternative check via registry
            result = CheckHostingBundleRegistry(requiredVersion);
            if (result.IsInstalled)
            {
                return result;
            }

            result.IsInstalled = false;
            result.Message = $"ASP.NET Core Hosting Bundle {requiredVersion}.x is not installed";
        }
        catch (Exception ex)
        {
            result.IsInstalled = false;
            result.Message = $"Error checking ASP.NET Core Hosting Bundle: {ex.Message}";
        }

        return result;
    }

    private PrerequisiteResult CheckHostingBundleRegistry(string requiredVersion)
    {
        var result = new PrerequisiteResult();

        try
        {
            // Check registry for .NET installations
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost");
            if (key != null)
            {
                var version = key.GetValue("Version")?.ToString();
                if (!string.IsNullOrEmpty(version) && version.StartsWith(requiredVersion))
                {
                    result.IsInstalled = true;
                    result.Version = version;
                    result.Message = $"ASP.NET Core Hosting Bundle v{version} detected";
                    return result;
                }
            }
        }
        catch
        {
            // Registry check failed, continue with false result
        }

        result.IsInstalled = false;
        return result;
    }

    /// <summary>
    /// Checks if IIS is installed and enabled
    /// </summary>
    public PrerequisiteResult CheckIIS()
    {
        var result = new PrerequisiteResult();

        try
        {
            string iisPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "inetsrv", "w3wp.exe"
            );

            if (File.Exists(iisPath))
            {
                result.IsInstalled = true;
                result.Message = "IIS is installed";
            }
            else
            {
                result.IsInstalled = false;
                result.Message = "IIS is not installed or not enabled";
            }
        }
        catch (Exception ex)
        {
            result.IsInstalled = false;
            result.Message = $"Error checking IIS: {ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// Gets the download URL for the ASP.NET Core Hosting Bundle
    /// </summary>
    public string GetHostingBundleDownloadUrl(string version = "8.0")
    {
        return $"https://dotnet.microsoft.com/download/dotnet/{version}";
    }

    /// <summary>
    /// Checks all prerequisites for IIS deployment
    /// </summary>
    public Dictionary<string, PrerequisiteResult> CheckAllPrerequisites(string dotnetVersion = "8.0")
    {
        return new Dictionary<string, PrerequisiteResult>
        {
            ["IIS"] = CheckIIS(),
            ["ASP.NET Core Hosting Bundle"] = CheckAspNetCoreHostingBundle(dotnetVersion)
        };
    }
}
