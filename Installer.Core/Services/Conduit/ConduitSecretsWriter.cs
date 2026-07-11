using System.Text.Json.Nodes;

namespace Installer.Core.Services.Conduit;

/// <summary>
/// Writes the installer's Provision + Enroll stamp into Conduit's machine-local
/// secret store (%PROGRAMDATA%\Conduit\secrets.json) instead of the
/// world-readable Program Files appsettings.json. The data directory must
/// already be locked down by <see cref="ConduitDataDirectorySecurer"/>; the
/// file itself is written ACL-first via <see cref="RestrictedFileWriter"/>.
///
/// The Program Files appsettings.json receives NOTHING from the installer —
/// the payload's own non-secret defaults are extracted as-is. Conduit loads
/// secrets.json last, so the stamp outranks them.
/// </summary>
public class ConduitSecretsWriter
{
    public static string DefaultSecretsPath =>
        Path.Combine(ConduitDataDirectorySecurer.DefaultDataDirectory, "secrets.json");

    /// <summary>Overridable for tests (points writes at a temp directory).</summary>
    public virtual string SecretsPath => DefaultSecretsPath;

    /// <summary>
    /// Merges the Provision + Enroll stamp into secrets.json. Idempotent:
    /// an existing Provision:JwtSecretKey in secrets.json — or, on upgrade from a
    /// pre-secrets build, in the old appsettings.json (pass its content as
    /// <paramref name="legacyAppSettingsJson"/>) — survives so tokens outlive the
    /// re-run. Returns null on success, error text on failure.
    /// </summary>
    public virtual string? StampProvisionAndEnroll(
        string connectionString,
        string enrollUrl,
        string enrollCode,
        string? adminUsername,
        int? serverPort,
        string? legacyAppSettingsJson,
        SilentInstallLog log)
    {
        try
        {
            var existing = File.Exists(SecretsPath) ? File.ReadAllText(SecretsPath) : null;
            existing = CarryLegacyJwtSecret(existing, legacyAppSettingsJson);

            var stamped = ConduitAppSettingsStamper.Stamp(
                existing, connectionString, enrollUrl, enrollCode, adminUsername, serverPort);

            var directory = Path.GetDirectoryName(SecretsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory); // already ACL-locked by ConduitDataDirectorySecurer

            RestrictedFileWriter.Write(SecretsPath, stamped);
            log.Info($"Stamped Provision + Enroll into the restricted secret store: {SecretsPath}.");
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not write {SecretsPath}: {ex.Message}";
        }
    }

    /// <summary>
    /// Pre-secrets-era installs carried Provision:JwtSecretKey in the base
    /// appsettings.json. When secrets.json has no JwtSecretKey yet, carry the
    /// legacy one over so an upgrade does not rotate the JWT key. Pure — testable.
    /// </summary>
    public static string? CarryLegacyJwtSecret(string? secretsJson, string? legacyAppSettingsJson)
    {
        if (string.IsNullOrWhiteSpace(legacyAppSettingsJson))
            return secretsJson;

        try
        {
            var secrets = string.IsNullOrWhiteSpace(secretsJson)
                ? new JsonObject()
                : JsonNode.Parse(secretsJson) as JsonObject ?? new JsonObject();

            var existingSecret = (secrets["Provision"] as JsonObject)?["JwtSecretKey"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(existingSecret))
                return secretsJson; // secrets.json already authoritative

            var legacy = JsonNode.Parse(legacyAppSettingsJson) as JsonObject;
            var legacySecret = (legacy?["Provision"] as JsonObject)?["JwtSecretKey"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(legacySecret))
                return secretsJson;

            if (secrets["Provision"] is not JsonObject provision)
            {
                provision = new JsonObject();
                secrets["Provision"] = provision;
            }
            provision["JwtSecretKey"] = legacySecret;
            return secrets.ToJsonString();
        }
        catch
        {
            // Unparseable legacy content — never block the install over it.
            return secretsJson;
        }
    }
}
