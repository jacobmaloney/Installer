using Installer.Core.Models;

namespace Installer.Core.Services.Conduit;

/// <summary>
/// Unattended Conduit install orchestrator. Sequence:
/// preflight → resolve SQL (detect / skip / bootstrap Express from a verified,
/// staged copy) → stop existing service → extract payload → lock down
/// %PROGRAMDATA%\Conduit ACL (BEFORE any secret lands there) → stamp Provision +
/// Enroll into the restricted secrets.json (the Program Files appsettings.json
/// gets nothing secret) → register event-log source → create/start the Windows
/// service → poll enroll-status.json for the enrollment outcome.
/// Every failure class maps to a distinct <see cref="SilentExitCode"/>.
/// </summary>
public class ConduitSilentInstaller
{
    private readonly ConduitPreflight _preflight;
    private readonly SqlInstanceDetector _sqlDetector;
    private readonly SqlExpressBootstrapper _sqlBootstrapper;
    private readonly ConduitServiceInstaller _serviceInstaller;
    private readonly ResourceExtractor _extractor;
    private readonly RedistAuthenticityVerifier _redistVerifier;
    private readonly RedistStager _redistStager;
    private readonly ConduitSecretsWriter _secretsWriter;

    public ConduitSilentInstaller(
        ConduitPreflight? preflight = null,
        SqlInstanceDetector? sqlDetector = null,
        SqlExpressBootstrapper? sqlBootstrapper = null,
        ConduitServiceInstaller? serviceInstaller = null,
        ResourceExtractor? extractor = null,
        RedistAuthenticityVerifier? redistVerifier = null,
        RedistStager? redistStager = null,
        ConduitSecretsWriter? secretsWriter = null)
    {
        _preflight = preflight ?? new ConduitPreflight();
        _sqlDetector = sqlDetector ?? new SqlInstanceDetector();
        _sqlBootstrapper = sqlBootstrapper ?? new SqlExpressBootstrapper();
        _serviceInstaller = serviceInstaller ?? new ConduitServiceInstaller();
        _extractor = extractor ?? new ResourceExtractor();
        _redistVerifier = redistVerifier ?? new RedistAuthenticityVerifier();
        _redistStager = redistStager ?? new RedistStager();
        _secretsWriter = secretsWriter ?? new ConduitSecretsWriter();
    }

    public async Task<SilentExitCode> RunAsync(ConduitInstallOptions options, string installerDirectory, SilentInstallLog log)
    {
        log.Info($"Conduit unattended install starting. Mode: {options.Mode}. Install path: {options.InstallPath}." +
                 (string.IsNullOrWhiteSpace(options.TenantSlug) ? "" : $" Tenant: {options.TenantSlug}."));

        // ── 1. Preflight ────────────────────────────────────────────────
        if (!_preflight.IsElevated())
        {
            log.Error("Not running elevated. Run the installer as Administrator (the exe manifest requests elevation; RMM tools must launch it with an admin token).");
            return SilentExitCode.PreflightNotElevated;
        }
        log.Info("Preflight: elevation OK.");

        if (options.Mode == ConduitInstallOptions.ModeOnPrem)
        {
            if (!_preflight.IsDomainJoined())
            {
                log.Error("Preflight: this host is not domain-joined. Mode 'on-prem' requires a domain-joined host for Active Directory sync. " +
                          "Use mode 'cloud-only' if this Conduit will only sync cloud sources.");
                return SilentExitCode.PreflightNotDomainJoined;
            }
            log.Info("Preflight: domain-joined OK.");
        }
        else
        {
            log.Info("Preflight: domain check skipped (cloud-only mode).");
        }

        if (!_preflight.IsAspNetCore8RuntimeInstalled(out var runtimeVersion))
        {
            log.Error("Preflight: ASP.NET Core Runtime 8.x (x64) is not installed and the Conduit payload requires it. " +
                      "Install the 'ASP.NET Core Runtime 8 – Windows x64' package from dotnet.microsoft.com and re-run.");
            return SilentExitCode.PreflightRuntimeMissing;
        }
        log.Info($"Preflight: ASP.NET Core runtime {runtimeVersion} found.");

        var probe = await _preflight.ProbeEnrollHostAsync(options.EnrollUrl);
        if (!probe.Success)
        {
            log.Error($"Preflight: cannot reach the enrollment host. {probe.Message}");
            return SilentExitCode.PreflightNetworkFailed;
        }
        log.Info($"Preflight: {probe.Message}");
        log.Info("Note: the enroll code itself cannot be validated before use (codes are single-use); a stale code will surface as an enrollment failure after service start.");

        // ── 2. Resolve SQL ──────────────────────────────────────────────
        var (connectionString, sqlExit) = ResolveSqlConnection(options, installerDirectory, log);
        if (sqlExit != SilentExitCode.Success)
            return sqlExit;

        // ── 3. Stop existing service (upgrade) + extract payload ───────
        var stopError = _serviceInstaller.StopIfRunning(options.ServiceName);
        if (stopError != null)
        {
            log.Error($"{stopError} Files would be locked during extraction.");
            return SilentExitCode.ExtractFailed;
        }

        var baseSettingsPath = Path.Combine(options.InstallPath, "appsettings.json");
        var productionSettingsPath = Path.Combine(options.InstallPath, "appsettings.Production.json");
        string? preUpgradeBaseSettings = TryReadFile(baseSettingsPath);
        string? preUpgradeProductionSettings = TryReadFile(productionSettingsPath);
        if (preUpgradeBaseSettings != null)
            log.Info("Existing installation detected — preserving appsettings content across the upgrade.");

        try
        {
            log.Info($"Extracting application payload to {options.InstallPath}...");
            var fileCount = await _extractor.ExtractEmbeddedFilesAsync(options.InstallPath);
            log.Info($"Extracted {fileCount} files.");
        }
        catch (Exception ex)
        {
            log.Error($"Payload extraction failed: {ex.Message}");
            return SilentExitCode.ExtractFailed;
        }

        // ── 4. Lock down the data directory BEFORE any secret lands in it ──
        var aclError = ConduitDataDirectorySecurer.Secure();
        if (aclError != null)
        {
            log.Error($"{aclError} Refusing to continue — secrets.json and the generated admin password would be world-readable.");
            return SilentExitCode.DataDirAclFailed;
        }
        log.Info(@"Locked %PROGRAMDATA%\Conduit to Administrators + SYSTEM.");

        // ── 5. Stamp secrets.json (+ restore env file on upgrade) ──────────
        // The Program Files appsettings.json is left exactly as the payload
        // shipped it — the installer writes NOTHING secret outside the locked
        // data directory. A pre-secrets-era upgrade's Provision:JwtSecretKey
        // (old base-file stamp) is carried over so tokens survive the upgrade.
        var stampError = _secretsWriter.StampProvisionAndEnroll(
            connectionString!,
            options.EnrollUrl,
            options.EnrollCode,
            options.AdminUsername,
            options.ServerPort,
            preUpgradeBaseSettings,
            log);
        if (stampError != null)
        {
            log.Error(stampError);
            return SilentExitCode.ConfigStampFailed;
        }

        if (preUpgradeProductionSettings != null)
        {
            try
            {
                await File.WriteAllTextAsync(productionSettingsPath, preUpgradeProductionSettings);
                log.Info("Restored pre-upgrade appsettings.Production.json (owned by Conduit's setup; its secrets self-relocate on Conduit's next boot).");
            }
            catch (Exception ex)
            {
                log.Error($"Could not restore appsettings.Production.json: {ex.Message}");
                return SilentExitCode.ConfigStampFailed;
            }
        }

        // ── 6. Event-log source ─────────────────────────────────────────
        var eventLogError = _serviceInstaller.RegisterEventLogSource();
        if (eventLogError != null)
        {
            log.Error(eventLogError);
            return SilentExitCode.EventLogSourceFailed;
        }
        log.Info("Registered Windows event-log source 'Conduit'.");

        // ── 7. Windows service ──────────────────────────────────────────
        var exePath = Path.Combine(options.InstallPath, "Conduit.Web.exe");
        if (!File.Exists(exePath))
        {
            log.Error($"Expected service executable not found after extraction: {exePath}. Was the payload built with a Windows folder publish of Conduit.Web?");
            return SilentExitCode.ServiceInstallFailed;
        }

        var installError = _serviceInstaller.CreateOrUpdate(options.ServiceName, exePath, log);
        if (installError != null)
        {
            log.Error(installError);
            return SilentExitCode.ServiceInstallFailed;
        }

        var enrollBaselineUtc = DateTime.UtcNow;
        log.Info($"Starting service '{options.ServiceName}' (first start runs database initialization — this can take a few minutes)...");
        var startError = _serviceInstaller.Start(options.ServiceName);
        if (startError != null)
        {
            log.Error($"{startError} Check the Application event log and {_secretsWriter.SecretsPath}.");
            return SilentExitCode.ServiceStartFailed;
        }
        log.Info("Service is running.");

        // ── 8. Enrollment outcome ───────────────────────────────────────
        var exitCode = await EnrollStatusReader.PollAsync(enrollBaselineUtc, log);

        if (exitCode is SilentExitCode.Success or SilentExitCode.SuccessEnrollPending)
            log.Info($"Install complete. Admin credentials (if generated) are in {Path.Combine(ConduitDataDirectorySecurer.DefaultDataDirectory, "admin-initial-password.txt")} — sign in, change the password, delete the file.");

        return exitCode;
    }

    /// <summary>
    /// SQL resolution: explicit connection string wins; else first usable local
    /// instance (preference-ordered); else bootstrap the bundled SQL Express.
    /// Never force-installs over a usable existing instance.
    /// </summary>
    private (string? ConnectionString, SilentExitCode Exit) ResolveSqlConnection(
        ConduitInstallOptions options, string installerDirectory, SilentInstallLog log)
    {
        if (!string.IsNullOrWhiteSpace(options.Sql.ConnectionString))
        {
            log.Info("Using the connection string supplied in the sidecar (detection and Express bootstrap skipped).");
            return (options.Sql.ConnectionString, SilentExitCode.Success);
        }

        var detected = _sqlDetector.GetInstalledInstanceNames();
        var ordered = SqlInstanceDetector.OrderByPreference(detected, options.Sql.InstanceName);
        log.Info(ordered.Count == 0
            ? "SQL detection: no local SQL Server instances found."
            : $"SQL detection: local instances found: {string.Join(", ", ordered)}.");

        foreach (var instance in ordered)
        {
            var server = SqlInstanceDetector.BuildServerName(instance);
            var connectError = _sqlDetector.TestConnect(server);
            if (connectError == null)
            {
                log.Info($"Using existing SQL instance '{instance}' ({server}). SQL Express bootstrap skipped.");

                if (options.Sql.GrantServiceAccess)
                {
                    var grantError = _sqlBootstrapper.GrantServiceAccess(server);
                    if (grantError != null)
                        log.Warn($"Could not grant NT AUTHORITY\\SYSTEM dbcreator on '{server}': {grantError}. " +
                                 "If the Conduit service cannot create its database, grant access manually or supply sql.connectionString.");
                    else
                        log.Info(@"Granted NT AUTHORITY\SYSTEM dbcreator on the existing instance (service runs as LocalSystem).");
                }

                return (SqlExpressBootstrapper.BuildConnectionString(server, options.Sql.Database), SilentExitCode.Success);
            }
            log.Warn($"Instance '{instance}' is not usable: {connectError}");
        }

        if (!options.Sql.AllowExpressInstall)
        {
            log.Error("No usable SQL instance found and sql.allowExpressInstall is false. Supply sql.connectionString or enable the Express bootstrap.");
            return (null, SilentExitCode.SqlNoUsableInstance);
        }

        var setupExe = SqlExpressBootstrapper.FindSetupExecutable(installerDirectory, options.Sql.ExpressSetupPath);
        if (setupExe == null)
        {
            log.Error(@"No usable SQL instance found and the SQL Express setup exe is missing (expected redist\SQLEXPR*.exe next to the installer, or sql.expressSetupPath).");
            return (null, SilentExitCode.SqlNoUsableInstance);
        }

        // TOCTOU closure: copy the redist into an admin-only staging directory
        // FIRST, then verify THAT copy and execute THAT copy — the bytes checked
        // are the bytes run; no swap window in the world-writable source location
        // between the check and the elevated launch.
        var stagedSetupExe = _redistStager.Stage(setupExe, log, out var stageError);
        if (stagedSetupExe == null)
        {
            log.Error(stageError!);
            return (null, SilentExitCode.SqlRedistVerificationFailed);
        }

        try
        {
            // The redist sits OUTSIDE the installer exe's Authenticode coverage
            // (side-by-side file) and is about to run elevated — it must prove it
            // is Microsoft's binary (or match the configured pin) first.
            var verifyError = _redistVerifier.Verify(stagedSetupExe, options.Sql.ExpressSetupSha256, log);
            if (verifyError != null)
            {
                log.Error(verifyError);
                return (null, SilentExitCode.SqlRedistVerificationFailed);
            }

            var installErrorText = _sqlBootstrapper.Install(stagedSetupExe, log);
            if (installErrorText != null)
            {
                log.Error(installErrorText);
                return (null, SilentExitCode.SqlExpressInstallFailed);
            }
        }
        finally
        {
            _redistStager.Cleanup();
        }

        var conduitServer = SqlInstanceDetector.BuildServerName(SqlExpressBootstrapper.ConduitInstanceName);
        var postInstallConnectError = _sqlDetector.TestConnect(conduitServer, timeoutSeconds: 15);
        if (postInstallConnectError != null)
        {
            log.Error($"SQL Express installed but connection to {conduitServer} failed: {postInstallConnectError}");
            return (null, SilentExitCode.SqlConnectFailed);
        }

        log.Info($"SQL Express instance {conduitServer} is up.");
        return (SqlExpressBootstrapper.BuildConnectionString(conduitServer, options.Sql.Database), SilentExitCode.Success);
    }

    private static string? TryReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }
}
