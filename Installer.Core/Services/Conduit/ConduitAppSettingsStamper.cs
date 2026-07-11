using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Installer.Core.Services.Conduit;

/// <summary>
/// Stamps the Provision: and Enroll: sections into the BASE appsettings.json
/// (never the environment file — Conduit's SetupService rewrites the env file
/// wholesale mid-setup and would wipe the stamp). All other content in the
/// file is preserved. AdminPassword is intentionally never written: Conduit's
/// ProvisioningService is the single source of truth for generating it.
/// </summary>
public static class ConduitAppSettingsStamper
{
    /// <summary>
    /// Merges the stamp into existing appsettings JSON (pass the extracted
    /// payload's file content, or the pre-upgrade file when one existed).
    /// Pure string-to-string — unit-testable.
    /// </summary>
    public static string Stamp(
        string? existingJson,
        string connectionString,
        string enrollUrl,
        string enrollCode,
        string? adminUsername = null,
        int? serverPort = null,
        Func<string>? jwtSecretGenerator = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("connectionString is required.", nameof(connectionString));

        var root = string.IsNullOrWhiteSpace(existingJson)
            ? new JsonObject()
            : JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject();

        var provision = root["Provision"] as JsonObject ?? new JsonObject();
        provision["ConnectionString"] = connectionString;

        if (!string.IsNullOrWhiteSpace(adminUsername))
            provision["AdminUsername"] = adminUsername;

        // Keep an already-stamped secret (idempotent re-run / upgrade); generate otherwise.
        var existingSecret = provision["JwtSecretKey"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(existingSecret))
            provision["JwtSecretKey"] = (jwtSecretGenerator ?? GenerateJwtSecret)();

        if (serverPort.HasValue)
            provision["ServerPort"] = serverPort.Value;

        root["Provision"] = provision;

        var enroll = root["Enroll"] as JsonObject ?? new JsonObject();
        enroll["Url"] = enrollUrl;
        enroll["Code"] = enrollCode;
        root["Enroll"] = enroll;

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string GenerateJwtSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
}
