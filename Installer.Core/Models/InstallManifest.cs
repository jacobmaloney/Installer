using System.Text.Json;
using System.Text.Json.Serialization;

namespace Installer.Core.Models;

/// <summary>
/// Installation manifest that tracks what was installed, when, and configuration details.
/// Stored as install-manifest.json in the installation directory.
/// </summary>
public class InstallManifest
{
    public const string FileName = "install-manifest.json";

    /// <summary>
    /// Unique identifier for this installation
    /// </summary>
    public Guid InstallationId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Current version installed
    /// </summary>
    public VersionInfo Version { get; set; } = new();

    /// <summary>
    /// When the initial installation occurred
    /// </summary>
    public DateTime InstalledAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the last update was applied
    /// </summary>
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Physical installation path
    /// </summary>
    public string InstallPath { get; set; } = string.Empty;

    /// <summary>
    /// IIS site name
    /// </summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>
    /// IIS application pool name
    /// </summary>
    public string AppPoolName { get; set; } = string.Empty;

    /// <summary>
    /// Port the site is configured on
    /// </summary>
    public int Port { get; set; } = 443;

    /// <summary>
    /// Whether HTTPS is configured
    /// </summary>
    public bool UsesHttps { get; set; } = true;

    /// <summary>
    /// Hostname binding (if any)
    /// </summary>
    public string? HostName { get; set; }

    /// <summary>
    /// History of all updates applied to this installation
    /// </summary>
    public List<UpdateHistoryEntry> UpdateHistory { get; set; } = new();

    /// <summary>
    /// Database connection name from appsettings.json (not the actual connection string for security)
    /// </summary>
    public string DatabaseConnectionName { get; set; } = "DefaultConnection";

    /// <summary>
    /// Last known database name (for reference during upgrades)
    /// </summary>
    public string? LastKnownDatabaseName { get; set; }

    /// <summary>
    /// Machine name where installed
    /// </summary>
    public string MachineName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Product code for Programs and Features registry entry
    /// </summary>
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>
    /// Display name shown in Programs and Features
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Publisher shown in Programs and Features
    /// </summary>
    public string Publisher { get; set; } = string.Empty;

    /// <summary>
    /// Custom metadata that can be added by plugins or extensions
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>
    /// Gets the full path to the manifest file
    /// </summary>
    [JsonIgnore]
    public string ManifestPath => Path.Combine(InstallPath, FileName);

    /// <summary>
    /// Adds an entry to the update history
    /// </summary>
    public void AddUpdateEntry(VersionInfo fromVersion, VersionInfo toVersion, string? notes = null)
    {
        UpdateHistory.Add(new UpdateHistoryEntry
        {
            FromVersion = fromVersion,
            ToVersion = toVersion,
            UpdatedAt = DateTime.UtcNow,
            Notes = notes
        });
        LastUpdatedAt = DateTime.UtcNow;
        Version = toVersion;
    }

    /// <summary>
    /// Saves the manifest to disk
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(this, options);
        await File.WriteAllTextAsync(ManifestPath, json, cancellationToken);
    }

    /// <summary>
    /// Saves the manifest to a specific path
    /// </summary>
    public async Task SaveToAsync(string path, CancellationToken cancellationToken = default)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var json = JsonSerializer.Serialize(this, options);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    /// <summary>
    /// Loads a manifest from an installation directory
    /// </summary>
    public static async Task<InstallManifest?> LoadAsync(string installPath, CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(installPath, FileName);
        return await LoadFromAsync(manifestPath, cancellationToken);
    }

    /// <summary>
    /// Loads a manifest from a specific file path
    /// </summary>
    public static async Task<InstallManifest?> LoadFromAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(manifestPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };

            return JsonSerializer.Deserialize<InstallManifest>(json, options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if a manifest exists in the given directory
    /// </summary>
    public static bool Exists(string installPath)
    {
        return File.Exists(Path.Combine(installPath, FileName));
    }
}

/// <summary>
/// Represents a single update in the installation history
/// </summary>
public class UpdateHistoryEntry
{
    /// <summary>
    /// Version before the update
    /// </summary>
    public VersionInfo FromVersion { get; set; } = new();

    /// <summary>
    /// Version after the update
    /// </summary>
    public VersionInfo ToVersion { get; set; } = new();

    /// <summary>
    /// When the update was applied
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Optional notes about the update
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Whether the update completed successfully
    /// </summary>
    public bool Success { get; set; } = true;

    /// <summary>
    /// Error message if the update failed
    /// </summary>
    public string? ErrorMessage { get; set; }
}
