using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

namespace Installer.Core.Services;

/// <summary>
/// Extracts embedded application files from the installer executable
/// </summary>
public class ResourceExtractor
{
    private const string EmbeddedResourceName = "InstallerRuntime.AppPayload.zip";

    /// <summary>
    /// Extracts embedded application files to the target directory
    /// </summary>
    /// <param name="targetDirectory">Directory to extract files to</param>
    /// <param name="progressCallback">Optional callback for progress updates (file name, current count, total count)</param>
    /// <returns>Number of files extracted</returns>
    public async Task<int> ExtractEmbeddedFilesAsync(
        string targetDirectory,
        Action<string, int, int>? progressCallback = null)
    {
        // Ensure target directory exists
        Directory.CreateDirectory(targetDirectory);

        // Get the current EXE path (handles single-file apps)
        var exePath = GetCurrentExecutablePath();

        // First, try to extract from appended ZIP data
        var zipStream = await ExtractAppendedZipAsync(exePath);

        if (zipStream == null)
        {
            // Fallback: try manifest resources (for development/testing)
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(r => r.EndsWith("AppPayload.zip"));

            if (resourceName != null)
            {
                zipStream = assembly.GetManifestResourceStream(resourceName);
            }
        }

        if (zipStream == null)
        {
            throw new InvalidOperationException(
                "Embedded application files not found. This installer may be corrupted or not properly packaged.");
        }

        int filesExtracted = 0;
        int totalFiles = 0;

        try
        {
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                totalFiles = archive.Entries.Count;

                foreach (var entry in archive.Entries)
                {
                    // Skip directories
                    if (string.IsNullOrEmpty(entry.Name))
                        continue;

                    var destinationPath = Path.Combine(targetDirectory, entry.FullName);

                    // Create directory if needed
                    var destinationDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destinationDir))
                    {
                        Directory.CreateDirectory(destinationDir);
                    }

                    // Extract file
                    await Task.Run(() => entry.ExtractToFile(destinationPath, overwrite: true));

                    filesExtracted++;
                    progressCallback?.Invoke(entry.FullName, filesExtracted, totalFiles);
                }
            }
        }
        finally
        {
            zipStream?.Dispose();
        }

        return filesExtracted;
    }

    /// <summary>
    /// Extracts the ZIP data appended to the end of the EXE
    /// Format: [EXE][MARKER:8][ZIP:N][SIZE:4][MARKER:8]
    /// </summary>
    private async Task<Stream?> ExtractAppendedZipAsync(string exePath)
    {
        try
        {
            using (var exeStream = File.OpenRead(exePath))
            {
                var marker = System.Text.Encoding.ASCII.GetBytes("APPDATA\0");
                const int markerLen = 8;
                const int sizeLen = 4;

                // Minimum size: marker + size + marker = 20 bytes
                if (exeStream.Length < markerLen + sizeLen + markerLen)
                    return null;

                // Step 1: Read final marker (last 8 bytes)
                exeStream.Seek(-markerLen, SeekOrigin.End);
                var finalMarker = new byte[markerLen];
                await exeStream.ReadAsync(finalMarker, 0, markerLen);

                if (!finalMarker.SequenceEqual(marker))
                    return null;

                // Step 2: Read size (4 bytes before final marker)
                exeStream.Seek(-(markerLen + sizeLen), SeekOrigin.End);
                var sizeBytes = new byte[sizeLen];
                await exeStream.ReadAsync(sizeBytes, 0, sizeLen);
                var zipSize = BitConverter.ToInt32(sizeBytes, 0);

                // Validate size
                if (zipSize <= 0 || zipSize > exeStream.Length - markerLen - sizeLen - markerLen)
                    return null;

                // Step 3: Verify initial marker
                exeStream.Seek(-(markerLen + sizeLen + zipSize + markerLen), SeekOrigin.End);
                var initialMarker = new byte[markerLen];
                await exeStream.ReadAsync(initialMarker, 0, markerLen);

                if (!initialMarker.SequenceEqual(marker))
                    return null;

                // Step 4: Read ZIP data (right after initial marker)
                var zipData = new byte[zipSize];
                await exeStream.ReadAsync(zipData, 0, zipSize);

                return new MemoryStream(zipData);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if embedded application files are present in the installer
    /// </summary>
    /// <returns>True if embedded files are found</returns>
    public bool HasEmbeddedFiles()
    {
        // First check for appended ZIP data
        if (HasAppendedZipData())
            return true;

        // Fallback: check manifest resources
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceNames()
            .Any(r => r.EndsWith("AppPayload.zip"));
    }

    /// <summary>
    /// Gets diagnostic information about the executable and embedded data detection
    /// </summary>
    public string GetDiagnosticInfo()
    {
        var sb = new System.Text.StringBuilder();
        var exePath = GetCurrentExecutablePath();

        sb.AppendLine($"Executable path: {exePath}");
        sb.AppendLine($"File exists: {File.Exists(exePath)}");

        if (File.Exists(exePath))
        {
            var fileInfo = new FileInfo(exePath);
            sb.AppendLine($"File size: {fileInfo.Length:N0} bytes");

            try
            {
                using var stream = File.OpenRead(exePath);
                var marker = System.Text.Encoding.ASCII.GetBytes("APPDATA\0");

                // Check final marker
                stream.Seek(-8, SeekOrigin.End);
                var finalBytes = new byte[8];
                stream.Read(finalBytes, 0, 8);
                var finalMarkerText = System.Text.Encoding.ASCII.GetString(finalBytes);
                sb.AppendLine($"Final 8 bytes (as text): '{finalMarkerText}'");
                sb.AppendLine($"Final 8 bytes (hex): {BitConverter.ToString(finalBytes)}");
                sb.AppendLine($"Marker match: {finalBytes.SequenceEqual(marker)}");

                if (finalBytes.SequenceEqual(marker))
                {
                    // Read size
                    stream.Seek(-12, SeekOrigin.End);
                    var sizeBytes = new byte[4];
                    stream.Read(sizeBytes, 0, 4);
                    var zipSize = BitConverter.ToInt32(sizeBytes, 0);
                    sb.AppendLine($"ZIP size from footer: {zipSize:N0} bytes");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Error reading file: {ex.Message}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Checks if ZIP data is appended to the current executable
    /// </summary>
    private bool HasAppendedZipData()
    {
        try
        {
            var exePath = GetCurrentExecutablePath();
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return false;

            using var exeStream = File.OpenRead(exePath);
            var marker = System.Text.Encoding.ASCII.GetBytes("APPDATA\0");

            if (exeStream.Length < marker.Length + 4)
                return false;

            // Seek to the end to find the final marker
            exeStream.Seek(-marker.Length, SeekOrigin.End);

            var finalMarker = new byte[marker.Length];
            exeStream.Read(finalMarker, 0, marker.Length);

            return finalMarker.SequenceEqual(marker);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the path to the current executable, handling single-file scenarios
    /// </summary>
    private static string GetCurrentExecutablePath()
    {
        // For single-file apps, Assembly.Location returns empty string
        // Use Process.GetCurrentProcess().MainModule.FileName instead
        var location = Assembly.GetExecutingAssembly().Location;
        if (!string.IsNullOrEmpty(location))
            return location;

        return Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
    }

    /// <summary>
    /// Gets the estimated size of the embedded application files
    /// </summary>
    /// <returns>Size in bytes, or null if not available</returns>
    public long? GetEmbeddedFilesSize()
    {
        // First check for appended ZIP data size
        var appendedSize = GetAppendedZipSize();
        if (appendedSize.HasValue)
            return appendedSize;

        // Fallback: check manifest resources
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(r => r.EndsWith("AppPayload.zip"));

        if (resourceName == null)
            return null;

        using var resourceStream = assembly.GetManifestResourceStream(resourceName);
        return resourceStream?.Length;
    }

    /// <summary>
    /// Gets the size of the appended ZIP data
    /// Format: [EXE][MARKER:8][ZIP:N][SIZE:4][MARKER:8]
    /// </summary>
    private long? GetAppendedZipSize()
    {
        try
        {
            var exePath = GetCurrentExecutablePath();
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return null;

            using var exeStream = File.OpenRead(exePath);
            const int markerLen = 8;
            const int sizeLen = 4;

            if (exeStream.Length < markerLen + sizeLen + markerLen)
                return null;

            // Seek to read size (4 bytes before final marker)
            exeStream.Seek(-(markerLen + sizeLen), SeekOrigin.End);

            var sizeBytes = new byte[sizeLen];
            exeStream.Read(sizeBytes, 0, sizeLen);

            return BitConverter.ToInt32(sizeBytes, 0);
        }
        catch
        {
            return null;
        }
    }
}
