namespace Installer.Core.Models;

/// <summary>
/// Process exit codes for the unattended (silent) Conduit install, grouped by
/// failure class so RMM/deployment tooling can branch without parsing logs.
/// </summary>
public enum SilentExitCode
{
    /// <summary>Installed, service running, enrollment confirmed successful (or already enrolled).</summary>
    Success = 0,

    /// <summary>Unhandled/unexpected error.</summary>
    UnknownError = 1,

    /// <summary>Installed and service running, but enrollment outcome was not reported within the poll window. Check enroll-status.json.</summary>
    SuccessEnrollPending = 2,

    // 10-19: configuration
    /// <summary>Sidecar missing, unparseable, or failed validation.</summary>
    ConfigError = 10,

    // 20-29: preflight
    PreflightNotElevated = 20,
    PreflightNotDomainJoined = 21,
    PreflightNetworkFailed = 22,
    PreflightRuntimeMissing = 23,

    // 30-39: SQL
    /// <summary>No usable instance found and SQL Express bootstrap unavailable (disabled or setup exe missing).</summary>
    SqlNoUsableInstance = 30,
    SqlExpressInstallFailed = 31,
    SqlConnectFailed = 32,
    /// <summary>The SQL Express redist failed the authenticity gate (Microsoft Authenticode chain / pinned SHA-256) and was NOT executed.</summary>
    SqlRedistVerificationFailed = 33,

    // 40-49: payload
    ExtractFailed = 40,

    // 50-59: configuration stamping
    ConfigStampFailed = 50,

    // 60-69: data dir / event log prep
    DataDirAclFailed = 60,
    EventLogSourceFailed = 61,

    // 70-79: Windows service
    ServiceInstallFailed = 70,
    ServiceStartFailed = 71,

    // 80-89: enrollment (install itself succeeded)
    /// <summary>Service installed and running, but enrollment reported Failed (stale/consumed code, network, tenant mismatch — see log and enroll-status.json).</summary>
    EnrollmentFailed = 80
}
