using Installer.Core.Models;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Installer.Core.Services;

public class SolutionPublisher
{
    public class PublishResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? PublishPath { get; set; }
        public Exception? Exception { get; set; }
        public List<string> Output { get; set; } = new();
        public List<string> Errors { get; set; } = new();
    }

    /// <summary>
    /// Publishes a project using MSBuild directly to avoid NuGet task issues
    /// </summary>
    public async Task<PublishResult> PublishProjectAsync(
        ProjectInfo project,
        string outputPath,
        string configuration = "Release")
    {
        try
        {
            Directory.CreateDirectory(outputPath);

            var publishPath = Path.Combine(outputPath, "publish");

            // Find MSBuild.exe from Visual Studio
            var msbuildPath = FindMSBuildPath();

            if (string.IsNullOrEmpty(msbuildPath))
            {
                return new PublishResult
                {
                    Success = false,
                    Message = "MSBuild.exe not found. Please ensure Visual Studio is installed."
                };
            }

            // Use MSBuild directly with the Publish target
            // Escape the PublishDir path properly for MSBuild
            var escapedPublishPath = publishPath.Replace("\\", "\\\\");

            var arguments = $"\"{project.ProjectPath}\" " +
                          $"/t:Publish " +
                          $"/p:Configuration={configuration} " +
                          $"/p:PublishDir={escapedPublishPath}\\ " +
                          $"/p:SelfContained=false " +
                          $"/verbosity:minimal";

            var processInfo = new ProcessStartInfo
            {
                FileName = msbuildPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(project.ProjectPath) ?? ""
            };

            var output = new List<string>();
            var errors = new List<string>();

            using var process = new Process { StartInfo = processInfo };

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    output.Add(e.Data);
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    errors.Add(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                return new PublishResult
                {
                    Success = true,
                    Message = $"Successfully published {project.ProjectName}",
                    PublishPath = publishPath,
                    Output = output,
                    Errors = errors
                };
            }

            var errorMessage = errors.Any()
                ? string.Join(Environment.NewLine, errors)
                : "Unknown error - no error output captured";

            return new PublishResult
            {
                Success = false,
                Message = $"Publish failed (Exit code: {process.ExitCode}): {errorMessage}",
                Output = output,
                Errors = errors
            };
        }
        catch (Exception ex)
        {
            return new PublishResult
            {
                Success = false,
                Message = $"Publish failed: {ex.Message}",
                Exception = ex
            };
        }
    }

    /// <summary>
    /// Finds MSBuild.exe from Visual Studio installation
    /// </summary>
    private static string? FindMSBuildPath()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        // Check VS 2022 first (most recent)
        var vs2022Paths = new[]
        {
            Path.Combine(programFiles, @"Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"),
            Path.Combine(programFiles, @"Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"),
            Path.Combine(programFiles, @"Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe")
        };

        foreach (var path in vs2022Paths)
        {
            if (File.Exists(path))
                return path;
        }

        // Fallback to VS 2019
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var vs2019Paths = new[]
        {
            Path.Combine(programFilesX86, @"Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"),
            Path.Combine(programFilesX86, @"Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe"),
            Path.Combine(programFilesX86, @"Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe")
        };

        foreach (var path in vs2019Paths)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    /// <summary>
    /// Cleans the project before publishing
    /// </summary>
    public async Task<bool> CleanProjectAsync(string projectPath)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"clean \"{projectPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(processInfo);
            if (process == null) return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sanitizes appsettings.json files in the publish folder to remove database connection strings
    /// and reset configuration for a fresh install
    /// </summary>
    public void SanitizeAppSettings(string publishPath, Action<string>? logCallback = null)
    {
        var appSettingsFiles = new[]
        {
            Path.Combine(publishPath, "appsettings.json"),
            Path.Combine(publishPath, "appsettings.Production.json"),
            Path.Combine(publishPath, "appsettings.Development.json")
        };

        foreach (var filePath in appSettingsFiles)
        {
            if (!File.Exists(filePath))
                continue;

            try
            {
                logCallback?.Invoke($"Sanitizing {Path.GetFileName(filePath)}...");

                var json = File.ReadAllText(filePath);
                var jsonNode = JsonNode.Parse(json);

                if (jsonNode == null)
                    continue;

                bool modified = false;

                // Set connection strings to localdb placeholder (recognized as "not configured" by the app)
                // Using localdb prevents crashes while signaling setup is needed
                if (jsonNode["ConnectionStrings"] is JsonObject connectionStrings)
                {
                    foreach (var prop in connectionStrings.ToList())
                    {
                        connectionStrings[prop.Key] = "Server=(localdb)\\mssqllocaldb;Database=IdentityCenter_NotConfigured;Trusted_Connection=True;";
                        modified = true;
                    }
                    logCallback?.Invoke("  - Set ConnectionStrings to 'not configured' placeholder");
                }

                // Reset QuickSetup.IsConfigured to false if it exists
                if (jsonNode["QuickSetup"] is JsonObject quickSetup)
                {
                    if (quickSetup["IsConfigured"] != null)
                    {
                        quickSetup["IsConfigured"] = false;
                        modified = true;
                        logCallback?.Invoke("  - Reset QuickSetup.IsConfigured to false");
                    }
                }

                // Clear any sensitive data markers
                if (jsonNode["DatabaseConfigured"] != null)
                {
                    jsonNode["DatabaseConfigured"] = false;
                    modified = true;
                }

                if (modified)
                {
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    var sanitizedJson = jsonNode.ToJsonString(options);
                    File.WriteAllText(filePath, sanitizedJson);
                    logCallback?.Invoke($"  - Saved sanitized {Path.GetFileName(filePath)}");
                }
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"  - Warning: Could not sanitize {Path.GetFileName(filePath)}: {ex.Message}");
            }
        }
    }
}
