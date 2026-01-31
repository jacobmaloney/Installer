using System.IO.Compression;

namespace Installer.Core.Services;

/// <summary>
/// Creates compressed ZIP files from published application directories
/// </summary>
public class ResourceEmbedder
{
    /// <summary>
    /// Compresses a directory into a ZIP file
    /// </summary>
    /// <param name="sourceDirectory">Directory containing files to compress</param>
    /// <param name="destinationZipPath">Path where ZIP file will be created</param>
    /// <param name="progressCallback">Optional callback for progress (file name, current count, total count)</param>
    /// <returns>Size of created ZIP file in bytes</returns>
    public async Task<long> CompressDirectoryAsync(
        string sourceDirectory,
        string destinationZipPath,
        Action<string, int, int>? progressCallback = null)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");
        }

        // Delete existing ZIP if present
        if (File.Exists(destinationZipPath))
        {
            File.Delete(destinationZipPath);
        }

        // Ensure destination directory exists
        var destinationDir = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrEmpty(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
        }

        // Get all files to compress
        var files = Directory.GetFiles(sourceDirectory, "*.*", SearchOption.AllDirectories);
        var totalFiles = files.Length;
        var currentFile = 0;

        await Task.Run(() =>
        {
            using (var archive = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    // Calculate relative path
                    var relativePath = Path.GetRelativePath(sourceDirectory, file);

                    // Add to archive with optimal compression
                    archive.CreateEntryFromFile(file, relativePath, CompressionLevel.Optimal);

                    currentFile++;
                    progressCallback?.Invoke(relativePath, currentFile, totalFiles);
                }
            }
        });

        var zipInfo = new FileInfo(destinationZipPath);
        return zipInfo.Length;
    }

    /// <summary>
    /// Gets information about files that would be compressed
    /// </summary>
    /// <param name="sourceDirectory">Directory to analyze</param>
    /// <returns>Tuple of (file count, total size in bytes)</returns>
    public (int FileCount, long TotalSize) GetDirectoryInfo(string sourceDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");
        }

        var files = Directory.GetFiles(sourceDirectory, "*.*", SearchOption.AllDirectories);
        var totalSize = files.Sum(f => new FileInfo(f).Length);

        return (files.Length, totalSize);
    }

    /// <summary>
    /// Validates that a directory is suitable for compression
    /// </summary>
    /// <param name="sourceDirectory">Directory to validate</param>
    /// <returns>Validation result with any error messages</returns>
    public (bool IsValid, string? ErrorMessage) ValidateDirectory(string sourceDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return (false, $"Directory does not exist: {sourceDirectory}");
        }

        var files = Directory.GetFiles(sourceDirectory, "*.*", SearchOption.AllDirectories);

        if (files.Length == 0)
        {
            return (false, "Directory is empty - no files to compress");
        }

        // Check for any locked files
        foreach (var file in files.Take(10)) // Sample first 10 files
        {
            try
            {
                using (File.OpenRead(file)) { }
            }
            catch (IOException ex)
            {
                return (false, $"File is locked or inaccessible: {Path.GetFileName(file)} - {ex.Message}");
            }
        }

        return (true, null);
    }
}
