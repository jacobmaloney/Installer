using System.IO.Compression;
using Installer.Core.Models;

namespace Installer.Core.Services;

/// <summary>
/// Creates self-contained installer executables by embedding application files into the InstallerRuntime
/// </summary>
public class InstallerPackager
{
    private readonly ResourceEmbedder _embedder;

    public InstallerPackager()
    {
        _embedder = new ResourceEmbedder();
    }

    /// <summary>
    /// Creates a self-contained installer executable
    /// </summary>
    /// <param name="publishedAppDirectory">Directory containing the published application files</param>
    /// <param name="installerRuntimePath">Path to the InstallerRuntime.exe (template)</param>
    /// <param name="outputInstallerPath">Path where the final installer EXE will be created</param>
    /// <param name="progressCallback">Optional callback for progress updates</param>
    /// <returns>Result with success status and output path</returns>
    public async Task<OperationResult> CreateInstallerAsync(
        string publishedAppDirectory,
        string installerRuntimePath,
        string outputInstallerPath,
        Action<string>? progressCallback = null)
    {
        try
        {
            // Validate inputs
            if (!Directory.Exists(publishedAppDirectory))
            {
                return new OperationResult
                {
                    Success = false,
                    Message = $"Published app directory not found: {publishedAppDirectory}"
                };
            }

            if (!File.Exists(installerRuntimePath))
            {
                return new OperationResult
                {
                    Success = false,
                    Message = $"InstallerRuntime.exe not found: {installerRuntimePath}"
                };
            }

            // Validate published directory
            var validation = _embedder.ValidateDirectory(publishedAppDirectory);
            if (!validation.IsValid)
            {
                return new OperationResult
                {
                    Success = false,
                    Message = validation.ErrorMessage ?? "Directory validation failed"
                };
            }

            // Create temporary directory for packaging
            var tempDir = Path.Combine(Path.GetTempPath(), $"Installer_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // Step 1: Compress the published app into a ZIP
                progressCallback?.Invoke("Compressing application files...");
                var tempZipPath = Path.Combine(tempDir, "AppPayload.zip");

                var (fileCount, totalSize) = _embedder.GetDirectoryInfo(publishedAppDirectory);
                progressCallback?.Invoke($"Compressing {fileCount} files ({FormatBytes(totalSize)})...");

                await _embedder.CompressDirectoryAsync(
                    publishedAppDirectory,
                    tempZipPath,
                    (file, current, total) =>
                    {
                        if (current % 10 == 0 || current == total)
                        {
                            progressCallback?.Invoke($"Compressed {current}/{total} files...");
                        }
                    });

                var zipSize = new FileInfo(tempZipPath).Length;
                progressCallback?.Invoke($"Compressed to {FormatBytes(zipSize)} ZIP file");

                // Step 2: Copy InstallerRuntime.exe to output location
                progressCallback?.Invoke("Creating installer executable...");

                var outputDir = Path.GetDirectoryName(outputInstallerPath);
                if (!string.IsNullOrEmpty(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                File.Copy(installerRuntimePath, outputInstallerPath, overwrite: true);

                // Step 3: Embed the ZIP as a resource into the installer EXE
                progressCallback?.Invoke("Embedding application files into installer...");

                // Use ResourceHacker approach: append ZIP to end of EXE and update resource table
                // For now, we'll use a simpler approach: append as a custom section
                await EmbedZipIntoExecutableAsync(outputInstallerPath, tempZipPath);

                progressCallback?.Invoke("Verifying installer...");

                // Verify the result
                if (!File.Exists(outputInstallerPath))
                {
                    return new OperationResult
                    {
                        Success = false,
                        Message = "Failed to create installer executable"
                    };
                }

                var finalSize = new FileInfo(outputInstallerPath).Length;
                progressCallback?.Invoke($"Installer created: {FormatBytes(finalSize)}");

                return new OperationResult
                {
                    Success = true,
                    Message = $"Installer created successfully: {outputInstallerPath}",
                    Data = new { InstallerPath = outputInstallerPath, Size = finalSize }
                };
            }
            finally
            {
                // Cleanup temp directory
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
        catch (Exception ex)
        {
            return new OperationResult
            {
                Success = false,
                Message = $"Error creating installer: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Embeds a ZIP file into an executable as an embedded resource
    /// Note: This is a simplified approach - appends ZIP to the end of the EXE
    /// The ResourceExtractor will need to read it from the end
    ///
    /// Format: [EXE][MARKER:8][ZIP:N][SIZE:4][MARKER:8]
    /// - Size is placed before final marker so we can read it without knowing ZIP size
    /// </summary>
    private async Task EmbedZipIntoExecutableAsync(string exePath, string zipPath)
    {
        // Read the ZIP file
        var zipBytes = await File.ReadAllBytesAsync(zipPath);

        // Append ZIP to the end of the EXE with markers
        using (var exeStream = File.Open(exePath, FileMode.Append, FileAccess.Write))
        {
            // Write initial marker (8 bytes: "APPDATA\0")
            var marker = System.Text.Encoding.ASCII.GetBytes("APPDATA\0");
            await exeStream.WriteAsync(marker, 0, marker.Length);

            // Write ZIP data
            await exeStream.WriteAsync(zipBytes, 0, zipBytes.Length);

            // Write ZIP size (4 bytes) - placed here so we can find it from the end
            var sizeBytes = BitConverter.GetBytes(zipBytes.Length);
            await exeStream.WriteAsync(sizeBytes, 0, sizeBytes.Length);

            // Write final marker
            await exeStream.WriteAsync(marker, 0, marker.Length);
        }
    }

    /// <summary>
    /// Formats bytes into human-readable size
    /// </summary>
    private string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double size = bytes;
        int order = 0;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {sizes[order]}";
    }
}
