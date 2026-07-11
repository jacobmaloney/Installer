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

                    var destinationPath = ResolveDestinationPath(targetDirectory, entry.FullName);

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
    /// Resolves a zip entry's destination path, rejecting entries that would
    /// escape the target directory (zip-slip: "..\..\evil", rooted paths).
    /// </summary>
    public static string ResolveDestinationPath(string targetDirectory, string entryFullName)
    {
        var root = Path.GetFullPath(targetDirectory);
        var destination = Path.GetFullPath(Path.Combine(root, entryFullName));

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!destination.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Zip entry '{entryFullName}' resolves outside the target directory and was rejected.");

        return destination;
    }

    /// <summary>
    /// Extracts the ZIP data appended to the EXE.
    /// Format: [EXE][MARKER:8][ZIP:N][SIZE:4][MARKER:8], optionally followed by
    /// zero padding + the Authenticode certificate table when the exe was
    /// signed AFTER embedding (the required order — the signature then covers
    /// the payload).
    /// </summary>
    private async Task<Stream?> ExtractAppendedZipAsync(string exePath)
    {
        try
        {
            using var exeStream = File.OpenRead(exePath);
            var trailer = LocateTrailer(exeStream);
            if (trailer == null)
                return null;

            exeStream.Seek(trailer.Value.ZipStart, SeekOrigin.Begin);
            var zipData = new byte[trailer.Value.ZipSize];
            var read = 0;
            while (read < zipData.Length)
            {
                var n = await exeStream.ReadAsync(zipData.AsMemory(read));
                if (n == 0)
                    return null;
                read += n;
            }

            return new MemoryStream(zipData);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Locates the appended payload trailer. On an unsigned exe the final
    /// marker is the last 8 bytes of the file. On a signed exe the trailer was
    /// embedded BEFORE signing, so the Authenticode certificate table (and up
    /// to 7 bytes of alignment padding signtool inserts) sits AFTER it — the
    /// search end is then the certificate table's file offset, taken from the
    /// PE security data directory.
    /// </summary>
    public static (long ZipStart, int ZipSize)? LocateTrailer(Stream exeStream)
    {
        var marker = System.Text.Encoding.ASCII.GetBytes("APPDATA\0");
        const int markerLen = 8;
        const int sizeLen = 4;
        const int minTrailer = markerLen + sizeLen + markerLen;

        var searchEnd = GetTrailerSearchEnd(exeStream);

        for (var padding = 0; padding <= 7; padding++)
        {
            var markerEnd = searchEnd - padding;
            if (markerEnd < minTrailer)
                break;

            exeStream.Seek(markerEnd - markerLen, SeekOrigin.Begin);
            var finalMarker = ReadExactly(exeStream, markerLen);
            if (finalMarker == null || !finalMarker.SequenceEqual(marker))
                continue;

            exeStream.Seek(markerEnd - markerLen - sizeLen, SeekOrigin.Begin);
            var sizeBytes = ReadExactly(exeStream, sizeLen);
            if (sizeBytes == null)
                continue;
            var zipSize = BitConverter.ToInt32(sizeBytes, 0);
            if (zipSize <= 0 || zipSize > markerEnd - minTrailer)
                continue;

            var initialMarkerStart = markerEnd - markerLen - sizeLen - zipSize - markerLen;
            exeStream.Seek(initialMarkerStart, SeekOrigin.Begin);
            var initialMarker = ReadExactly(exeStream, markerLen);
            if (initialMarker == null || !initialMarker.SequenceEqual(marker))
                continue;

            return (initialMarkerStart + markerLen, zipSize);
        }

        return null;
    }

    /// <summary>
    /// End of the region the trailer can occupy: the Authenticode certificate
    /// table's file offset when present (IMAGE_DIRECTORY_ENTRY_SECURITY holds a
    /// FILE offset, not an RVA), else the stream length. Any parse trouble
    /// falls back to the stream length (unsigned behavior).
    /// </summary>
    public static long GetTrailerSearchEnd(Stream exeStream)
    {
        var length = exeStream.Length;
        try
        {
            if (length < 0x40)
                return length;

            exeStream.Seek(0, SeekOrigin.Begin);
            var mz = ReadExactly(exeStream, 2);
            if (mz == null || mz[0] != (byte)'M' || mz[1] != (byte)'Z')
                return length;

            exeStream.Seek(0x3C, SeekOrigin.Begin);
            var lfanewBytes = ReadExactly(exeStream, 4);
            if (lfanewBytes == null)
                return length;
            var peOffset = BitConverter.ToInt32(lfanewBytes, 0);
            if (peOffset <= 0 || peOffset > length - 0x100)
                return length;

            exeStream.Seek(peOffset, SeekOrigin.Begin);
            var peSig = ReadExactly(exeStream, 4);
            if (peSig == null || peSig[0] != (byte)'P' || peSig[1] != (byte)'E' || peSig[2] != 0 || peSig[3] != 0)
                return length;

            var optionalHeaderOffset = peOffset + 4 + 20;
            exeStream.Seek(optionalHeaderOffset, SeekOrigin.Begin);
            var magicBytes = ReadExactly(exeStream, 2);
            if (magicBytes == null)
                return length;
            var magic = BitConverter.ToUInt16(magicBytes, 0);

            // Data directory array offset within the optional header:
            // PE32+ (0x20B) = 112, PE32 (0x10B) = 96. Security entry is index 4.
            int dataDirectoryOffset;
            if (magic == 0x20B)
                dataDirectoryOffset = optionalHeaderOffset + 112;
            else if (magic == 0x10B)
                dataDirectoryOffset = optionalHeaderOffset + 96;
            else
                return length;

            exeStream.Seek(dataDirectoryOffset + 4 * 8, SeekOrigin.Begin);
            var securityEntry = ReadExactly(exeStream, 8);
            if (securityEntry == null)
                return length;

            var certTableOffset = BitConverter.ToUInt32(securityEntry, 0);
            var certTableSize = BitConverter.ToUInt32(securityEntry, 4);
            if (certTableOffset > 0 && certTableSize > 0 && certTableOffset < length)
                return certTableOffset;

            return length;
        }
        catch
        {
            return length;
        }
    }

    private static byte[]? ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n == 0)
                return null;
            read += n;
        }
        return buffer;
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
                var searchEnd = GetTrailerSearchEnd(stream);
                sb.AppendLine($"Trailer search end: {searchEnd:N0} (file length {stream.Length:N0}; " +
                              (searchEnd == stream.Length ? "no Authenticode certificate table)" : "Authenticode certificate table follows)"));

                var trailer = LocateTrailer(stream);
                sb.AppendLine($"Trailer found: {trailer != null}");
                if (trailer != null)
                    sb.AppendLine($"ZIP start: {trailer.Value.ZipStart:N0}, ZIP size: {trailer.Value.ZipSize:N0} bytes");
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
            return LocateTrailer(exeStream) != null;
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
            return LocateTrailer(exeStream)?.ZipSize;
        }
        catch
        {
            return null;
        }
    }
}
