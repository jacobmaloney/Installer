using System.Text.Json;
using System.Text.Json.Nodes;

namespace Installer.Core.Services;

/// <summary>
/// Service for preserving and restoring configuration files (especially appsettings.json)
/// across application upgrades.
/// </summary>
public class ConfigurationPreserver
{
    /// <summary>
    /// Configuration settings that should be preserved during upgrades
    /// </summary>
    private static readonly string[] PreservedSettings = new[]
    {
        "ConnectionStrings",
        "Logging",
        "SmtpSettings",
        "Authentication",
        "IdentityProviders",
        "DirectorySettings",
        "EncryptionKey",
        "JwtSettings",
        "ApplicationUrl",
        "AllowedHosts"
    };

    /// <summary>
    /// Extracts preservable configuration from appsettings.json
    /// </summary>
    public async Task<PreservedConfiguration> ExtractConfigurationAsync(
        string installPath,
        CancellationToken cancellationToken = default)
    {
        var config = new PreservedConfiguration
        {
            ExtractedAt = DateTime.UtcNow,
            SourcePath = installPath
        };

        var appsettingsPath = Path.Combine(installPath, "appsettings.json");
        var appsettingsProdPath = Path.Combine(installPath, "appsettings.Production.json");

        try
        {
            // Extract from main appsettings.json
            if (File.Exists(appsettingsPath))
            {
                var json = await File.ReadAllTextAsync(appsettingsPath, cancellationToken);
                config.AppSettings = ExtractPreservedNodes(json);
                config.OriginalAppSettingsJson = json;
            }

            // Extract from production appsettings if exists
            if (File.Exists(appsettingsProdPath))
            {
                var json = await File.ReadAllTextAsync(appsettingsProdPath, cancellationToken);
                config.AppSettingsProduction = ExtractPreservedNodes(json);
                config.OriginalAppSettingsProdJson = json;
            }

            config.Success = true;
        }
        catch (Exception ex)
        {
            config.Success = false;
            config.ErrorMessage = ex.Message;
        }

        return config;
    }

    /// <summary>
    /// Applies preserved configuration to a new installation's appsettings.json
    /// </summary>
    public async Task<ApplyConfigResult> ApplyConfigurationAsync(
        string installPath,
        PreservedConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var result = new ApplyConfigResult();

        try
        {
            var appsettingsPath = Path.Combine(installPath, "appsettings.json");

            if (!File.Exists(appsettingsPath))
            {
                result.Success = false;
                result.ErrorMessage = "appsettings.json not found in target installation";
                return result;
            }

            // Read the new appsettings.json
            var newJson = await File.ReadAllTextAsync(appsettingsPath, cancellationToken);
            var newNode = JsonNode.Parse(newJson) as JsonObject;

            if (newNode == null)
            {
                result.Success = false;
                result.ErrorMessage = "Could not parse new appsettings.json";
                return result;
            }

            // Apply preserved settings
            foreach (var kvp in config.AppSettings)
            {
                if (kvp.Value != null)
                {
                    var clonedValue = JsonNode.Parse(kvp.Value.ToJsonString());
                    newNode[kvp.Key] = clonedValue;
                    result.AppliedSettings.Add(kvp.Key);
                }
            }

            // Write the merged configuration
            var options = new JsonSerializerOptions { WriteIndented = true };
            var mergedJson = newNode.ToJsonString(options);
            await File.WriteAllTextAsync(appsettingsPath, mergedJson, cancellationToken);

            // Handle production appsettings if we have preserved settings
            if (config.AppSettingsProduction.Count > 0)
            {
                var appsettingsProdPath = Path.Combine(installPath, "appsettings.Production.json");

                if (File.Exists(appsettingsProdPath))
                {
                    var newProdJson = await File.ReadAllTextAsync(appsettingsProdPath, cancellationToken);
                    var newProdNode = JsonNode.Parse(newProdJson) as JsonObject;

                    if (newProdNode != null)
                    {
                        foreach (var kvp in config.AppSettingsProduction)
                        {
                            if (kvp.Value != null)
                            {
                                var clonedValue = JsonNode.Parse(kvp.Value.ToJsonString());
                                newProdNode[kvp.Key] = clonedValue;
                            }
                        }

                        var mergedProdJson = newProdNode.ToJsonString(options);
                        await File.WriteAllTextAsync(appsettingsProdPath, mergedProdJson, cancellationToken);
                    }
                }
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.Exception = ex;
        }

        return result;
    }

    /// <summary>
    /// Creates a backup of the current configuration before modifying
    /// </summary>
    public async Task<string?> BackupConfigurationAsync(
        string installPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var appsettingsPath = Path.Combine(installPath, "appsettings.json");
            if (!File.Exists(appsettingsPath))
                return null;

            var backupPath = appsettingsPath + $".backup_{DateTime.Now:yyyyMMddHHmmss}";
            await File.WriteAllTextAsync(backupPath,
                await File.ReadAllTextAsync(appsettingsPath, cancellationToken),
                cancellationToken);

            return backupPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Updates just the connection string in appsettings.json
    /// </summary>
    public async Task<bool> UpdateConnectionStringAsync(
        string installPath,
        string connectionString,
        string connectionName = "DefaultConnection",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var appsettingsPath = Path.Combine(installPath, "appsettings.json");
            if (!File.Exists(appsettingsPath))
                return false;

            var json = await File.ReadAllTextAsync(appsettingsPath, cancellationToken);
            var node = JsonNode.Parse(json) as JsonObject;

            if (node == null)
                return false;

            // Ensure ConnectionStrings section exists
            if (!node.ContainsKey("ConnectionStrings"))
            {
                node["ConnectionStrings"] = new JsonObject();
            }

            var connStrings = node["ConnectionStrings"] as JsonObject;
            if (connStrings != null)
            {
                connStrings[connectionName] = connectionString;
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(appsettingsPath, node.ToJsonString(options), cancellationToken);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the current connection string from appsettings.json
    /// </summary>
    public async Task<string?> GetConnectionStringAsync(
        string installPath,
        string connectionName = "DefaultConnection",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var appsettingsPath = Path.Combine(installPath, "appsettings.json");
            if (!File.Exists(appsettingsPath))
                return null;

            var json = await File.ReadAllTextAsync(appsettingsPath, cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("ConnectionStrings", out var connStrings) &&
                connStrings.TryGetProperty(connectionName, out var connString))
            {
                return connString.GetString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private Dictionary<string, JsonNode?> ExtractPreservedNodes(string json)
    {
        var result = new Dictionary<string, JsonNode?>();

        try
        {
            var node = JsonNode.Parse(json) as JsonObject;
            if (node == null) return result;

            foreach (var setting in PreservedSettings)
            {
                if (node.ContainsKey(setting))
                {
                    result[setting] = node[setting]?.DeepClone();
                }
            }
        }
        catch
        {
            // If parsing fails, return empty dictionary
        }

        return result;
    }
}

/// <summary>
/// Configuration extracted from an installation to be preserved
/// </summary>
public class PreservedConfiguration
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime ExtractedAt { get; set; }
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Preserved settings from appsettings.json
    /// </summary>
    public Dictionary<string, JsonNode?> AppSettings { get; set; } = new();

    /// <summary>
    /// Preserved settings from appsettings.Production.json
    /// </summary>
    public Dictionary<string, JsonNode?> AppSettingsProduction { get; set; } = new();

    /// <summary>
    /// Original full JSON for reference
    /// </summary>
    public string? OriginalAppSettingsJson { get; set; }
    public string? OriginalAppSettingsProdJson { get; set; }
}

/// <summary>
/// Result of applying preserved configuration
/// </summary>
public class ApplyConfigResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public Exception? Exception { get; set; }
    public List<string> AppliedSettings { get; set; } = new();
}
