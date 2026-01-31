using System.IO.Compression;
using Installer.Core.Models;

namespace Installer.Core.Services;

/// <summary>
/// Service for backing up and restoring application files during upgrades.
/// Creates timestamped backups that can be used for rollback.
/// </summary>
public class FileBackupService
{
    private const string BackupFolderName = "_backups";
    private const string BackupExtension = ".zip";

    /// <summary>
    /// Creates a backup of the entire installation directory
    /// </summary>
    public async Task<BackupResult> CreateBackupAsync(
        string installPath,
        VersionInfo? version = null,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new BackupResult
        {
            StartTime = DateTime.UtcNow,
            SourcePath = installPath
        };

        try
        {
            // Create backup folder outside the install path
            var backupFolder = GetBackupFolder(installPath);
            Directory.CreateDirectory(backupFolder);

            // Generate backup filename with timestamp and version
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var versionStr = version?.ShortVersion ?? "unknown";
            var backupFileName = $"IdentityCenter_v{versionStr}_{timestamp}{BackupExtension}";
            var backupPath = Path.Combine(backupFolder, backupFileName);

            result.BackupPath = backupPath;

            // Create the zip archive
            await Task.Run(() =>
            {
                using var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create);

                var files = GetFilesToBackup(installPath);
                var totalFiles = files.Count;
                var processedFiles = 0;

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var relativePath = Path.GetRelativePath(installPath, file);

                    // Skip the backup folder itself and temp files
                    if (relativePath.StartsWith(BackupFolderName) ||
                        relativePath.EndsWith(".tmp") ||
                        relativePath.Contains("\\logs\\"))
                        continue;

                    try
                    {
                        archive.CreateEntryFromFile(file, relativePath, CompressionLevel.Optimal);
                    }
                    catch
                    {
                        // File might be locked, skip it
                        result.SkippedFiles.Add(relativePath);
                    }

                    processedFiles++;
                    progress?.Report((int)((processedFiles / (double)totalFiles) * 100));
                }
            }, cancellationToken);

            // Get backup file size
            var backupInfo = new FileInfo(backupPath);
            result.BackupSizeBytes = backupInfo.Length;

            result.Success = true;
            result.EndTime = DateTime.UtcNow;

            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.Exception = ex;
            result.EndTime = DateTime.UtcNow;
            return result;
        }
    }

    /// <summary>
    /// Creates a quick backup of only critical configuration files
    /// </summary>
    public async Task<BackupResult> CreateConfigBackupAsync(
        string installPath,
        CancellationToken cancellationToken = default)
    {
        var result = new BackupResult
        {
            StartTime = DateTime.UtcNow,
            SourcePath = installPath,
            IsConfigOnly = true
        };

        try
        {
            var backupFolder = GetBackupFolder(installPath);
            Directory.CreateDirectory(backupFolder);

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupFileName = $"Config_{timestamp}{BackupExtension}";
            var backupPath = Path.Combine(backupFolder, backupFileName);

            result.BackupPath = backupPath;

            var configFiles = new[]
            {
                "appsettings.json",
                "appsettings.Production.json",
                "web.config",
                InstallManifest.FileName
            };

            await Task.Run(() =>
            {
                using var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create);

                foreach (var configFile in configFiles)
                {
                    var fullPath = Path.Combine(installPath, configFile);
                    if (File.Exists(fullPath))
                    {
                        archive.CreateEntryFromFile(fullPath, configFile, CompressionLevel.Optimal);
                        result.BackedUpFiles.Add(configFile);
                    }
                }
            }, cancellationToken);

            result.Success = true;
            result.EndTime = DateTime.UtcNow;

            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.Exception = ex;
            result.EndTime = DateTime.UtcNow;
            return result;
        }
    }

    /// <summary>
    /// Restores from a backup archive
    /// </summary>
    public async Task<RestoreResult> RestoreBackupAsync(
        string backupPath,
        string targetPath,
        bool overwriteExisting = true,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new RestoreResult
        {
            StartTime = DateTime.UtcNow,
            BackupPath = backupPath,
            TargetPath = targetPath
        };

        try
        {
            if (!File.Exists(backupPath))
            {
                result.Success = false;
                result.ErrorMessage = $"Backup file not found: {backupPath}";
                return result;
            }

            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(backupPath);
                var totalEntries = archive.Entries.Count;
                var processedEntries = 0;

                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var destinationPath = Path.Combine(targetPath, entry.FullName);
                    var destinationDir = Path.GetDirectoryName(destinationPath);

                    if (!string.IsNullOrEmpty(destinationDir))
                    {
                        Directory.CreateDirectory(destinationDir);
                    }

                    if (!string.IsNullOrEmpty(entry.Name))
                    {
                        try
                        {
                            entry.ExtractToFile(destinationPath, overwriteExisting);
                            result.RestoredFiles.Add(entry.FullName);
                        }
                        catch
                        {
                            result.FailedFiles.Add(entry.FullName);
                        }
                    }

                    processedEntries++;
                    progress?.Report((int)((processedEntries / (double)totalEntries) * 100));
                }
            }, cancellationToken);

            result.Success = result.FailedFiles.Count == 0;
            result.EndTime = DateTime.UtcNow;

            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.Exception = ex;
            result.EndTime = DateTime.UtcNow;
            return result;
        }
    }

    /// <summary>
    /// Lists available backups for an installation
    /// </summary>
    public List<BackupInfo> ListBackups(string installPath)
    {
        var backups = new List<BackupInfo>();
        var backupFolder = GetBackupFolder(installPath);

        if (!Directory.Exists(backupFolder))
            return backups;

        foreach (var file in Directory.GetFiles(backupFolder, $"*{BackupExtension}"))
        {
            var fileInfo = new FileInfo(file);
            var fileName = Path.GetFileNameWithoutExtension(file);

            // Parse version and timestamp from filename
            // Format: IdentityCenter_v1.2.3_20240129_123456
            var parts = fileName.Split('_');

            backups.Add(new BackupInfo
            {
                FileName = fileInfo.Name,
                FullPath = file,
                SizeBytes = fileInfo.Length,
                CreatedAt = fileInfo.CreationTimeUtc,
                Version = parts.Length > 1 ? parts[1].TrimStart('v') : null,
                IsConfigOnly = fileName.StartsWith("Config_")
            });
        }

        return backups.OrderByDescending(b => b.CreatedAt).ToList();
    }

    /// <summary>
    /// Deletes old backups, keeping only the specified number of most recent
    /// </summary>
    public int CleanupOldBackups(string installPath, int keepCount = 5)
    {
        var backups = ListBackups(installPath);
        var toDelete = backups.Skip(keepCount).ToList();
        var deleted = 0;

        foreach (var backup in toDelete)
        {
            try
            {
                File.Delete(backup.FullPath);
                deleted++;
            }
            catch
            {
                // Skip if can't delete
            }
        }

        return deleted;
    }

    private string GetBackupFolder(string installPath)
    {
        // Store backups one level up from install path
        var parentDir = Directory.GetParent(installPath)?.FullName ?? installPath;
        return Path.Combine(parentDir, "IdentityCenter_Backups");
    }

    private List<string> GetFilesToBackup(string installPath)
    {
        return Directory.GetFiles(installPath, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"\\{BackupFolderName}\\"))
            .ToList();
    }
}

/// <summary>
/// Result of a backup operation
/// </summary>
public class BackupResult
{
    public bool Success { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration => EndTime - StartTime;

    public string SourcePath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public long BackupSizeBytes { get; set; }
    public bool IsConfigOnly { get; set; }

    public List<string> BackedUpFiles { get; set; } = new();
    public List<string> SkippedFiles { get; set; } = new();

    public string? ErrorMessage { get; set; }
    public Exception? Exception { get; set; }

    public string GetSizeDisplay()
    {
        if (BackupSizeBytes < 1024)
            return $"{BackupSizeBytes} B";
        if (BackupSizeBytes < 1024 * 1024)
            return $"{BackupSizeBytes / 1024.0:F1} KB";
        if (BackupSizeBytes < 1024 * 1024 * 1024)
            return $"{BackupSizeBytes / (1024.0 * 1024.0):F1} MB";
        return $"{BackupSizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}

/// <summary>
/// Result of a restore operation
/// </summary>
public class RestoreResult
{
    public bool Success { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration => EndTime - StartTime;

    public string BackupPath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;

    public List<string> RestoredFiles { get; set; } = new();
    public List<string> FailedFiles { get; set; } = new();

    public string? ErrorMessage { get; set; }
    public Exception? Exception { get; set; }
}

/// <summary>
/// Information about an available backup
/// </summary>
public class BackupInfo
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Version { get; set; }
    public bool IsConfigOnly { get; set; }

    public string GetSizeDisplay()
    {
        if (SizeBytes < 1024)
            return $"{SizeBytes} B";
        if (SizeBytes < 1024 * 1024)
            return $"{SizeBytes / 1024.0:F1} KB";
        if (SizeBytes < 1024 * 1024 * 1024)
            return $"{SizeBytes / (1024.0 * 1024.0):F1} MB";
        return $"{SizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}
