using System.Text.Json;
using System.Text.Json.Serialization;

namespace Installer.Core.Models;

/// <summary>
/// Sidecar configuration for the unattended Conduit install
/// (conduit.provision.json next to the installer executable, or a path
/// passed via --config).
/// </summary>
public class ConduitInstallOptions
{
    public const string SidecarFileName = "conduit.provision.json";
    public const string ModeOnPrem = "on-prem";
    public const string ModeCloudOnly = "cloud-only";

    [JsonPropertyName("enrollUrl")]
    public string EnrollUrl { get; set; } = string.Empty;

    [JsonPropertyName("enrollCode")]
    public string EnrollCode { get; set; } = string.Empty;

    /// <summary>Informational only — logged for traceability; the platform derives the tenant from the enroll code.</summary>
    [JsonPropertyName("tenantSlug")]
    public string? TenantSlug { get; set; }

    /// <summary>"on-prem" (AD sync; requires domain-joined host) or "cloud-only" (no domain requirement).</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("installPath")]
    public string InstallPath { get; set; } = @"C:\Program Files\Conduit";

    [JsonPropertyName("serviceName")]
    public string ServiceName { get; set; } = "IdentityCenterConduit";

    [JsonPropertyName("serverPort")]
    public int? ServerPort { get; set; }

    [JsonPropertyName("adminUsername")]
    public string? AdminUsername { get; set; }

    [JsonPropertyName("sql")]
    public ConduitSqlOptions Sql { get; set; } = new();

    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Parses sidecar JSON. Throws JsonException on malformed input.</summary>
    public static ConduitInstallOptions Parse(string json)
    {
        var options = JsonSerializer.Deserialize<ConduitInstallOptions>(json, ParseOptions);
        return options ?? throw new JsonException("Sidecar deserialized to null.");
    }

    /// <summary>Returns validation errors; empty list means valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(EnrollUrl))
            errors.Add("enrollUrl is required.");
        else if (!Uri.TryCreate(EnrollUrl, UriKind.Absolute, out var uri) ||
                 uri.Scheme != Uri.UriSchemeHttps)
            errors.Add($"enrollUrl '{EnrollUrl}' must be an absolute https URL — the single-use enroll code must never cross the wire in cleartext.");

        if (string.IsNullOrWhiteSpace(EnrollCode))
            errors.Add("enrollCode is required.");

        if (Mode != ModeOnPrem && Mode != ModeCloudOnly)
            errors.Add($"mode must be '{ModeOnPrem}' or '{ModeCloudOnly}' (got '{Mode}').");

        if (string.IsNullOrWhiteSpace(InstallPath))
            errors.Add("installPath must not be blank.");

        if (string.IsNullOrWhiteSpace(ServiceName))
            errors.Add("serviceName must not be blank.");

        if (ServerPort is < 1 or > 65535)
            errors.Add($"serverPort must be 1-65535 (got {ServerPort}).");

        if (!string.IsNullOrWhiteSpace(Sql.ConnectionString) &&
            Sql.ConnectionString.Contains("(localdb)", StringComparison.OrdinalIgnoreCase))
            errors.Add("sql.connectionString points at LocalDB, which Conduit does not support (the service account cannot reach a per-user LocalDB instance).");

        if (string.IsNullOrWhiteSpace(Sql.Database))
            errors.Add("sql.database must not be blank.");

        if (!string.IsNullOrWhiteSpace(Sql.ExpressSetupSha256) &&
            !Services.Conduit.RedistAuthenticityVerifier.TryNormalizeSha256Pin(Sql.ExpressSetupSha256, out _))
            errors.Add($"sql.expressSetupSha256 must be a 64-character hex SHA-256 (got '{Sql.ExpressSetupSha256}').");

        return errors;
    }
}

public class ConduitSqlOptions
{
    /// <summary>Full connection string. When set, instance detection and SQL Express bootstrap are skipped entirely.</summary>
    [JsonPropertyName("connectionString")]
    public string? ConnectionString { get; set; }

    /// <summary>Preferred existing local instance name (e.g. "SQLEXPRESS"). Checked first during detection.</summary>
    [JsonPropertyName("instanceName")]
    public string? InstanceName { get; set; }

    [JsonPropertyName("database")]
    public string Database { get; set; } = "Conduit";

    /// <summary>When no usable instance exists, install the bundled SQL Express. Default true.</summary>
    [JsonPropertyName("allowExpressInstall")]
    public bool AllowExpressInstall { get; set; } = true;

    /// <summary>Override path to the SQL Express setup exe. Default: redist\SQLEXPR*.exe next to the installer.</summary>
    [JsonPropertyName("expressSetupPath")]
    public string? ExpressSetupPath { get; set; }

    /// <summary>
    /// Optional pinned SHA-256 of the SQL Express setup exe. When set, the redist must match
    /// this hash exactly before it is executed elevated (replaces the default check that its
    /// Authenticode signature is valid and chains to a Microsoft root CA).
    /// </summary>
    [JsonPropertyName("expressSetupSha256")]
    public string? ExpressSetupSha256 { get; set; }

    /// <summary>
    /// When reusing an EXISTING instance, grant NT AUTHORITY\SYSTEM a login + dbcreator so the
    /// Conduit service (LocalSystem) can create and own its database. Default true.
    /// Not applied on the freshly bootstrapped CONDUIT instance (setup grants it sysadmin there).
    /// </summary>
    [JsonPropertyName("grantServiceAccess")]
    public bool GrantServiceAccess { get; set; } = true;
}
