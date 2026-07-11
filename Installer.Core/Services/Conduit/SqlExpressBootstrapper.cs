using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace Installer.Core.Services.Conduit;

/// <summary>
/// Silent SQL Server Express bootstrap. The distribution package carries the
/// SQL Express setup exe side-by-side (redist\SQLEXPR*.exe next to the
/// installer) — it is never embedded in the installer executable because it
/// must land on disk to run anyway and would roughly quadruple the exe size.
///
/// Instance decisions:
/// - Dedicated instance name CONDUIT (not SQLEXPRESS): never collides with a
///   pre-existing/broken SQLEXPRESS remnant, and ownership is obvious.
/// - Windows auth only (no /SECURITYMODE=SQL — mixed mode off).
/// - TCP and SQL Browser disabled: Conduit runs on the same box and connects
///   via shared memory; smallest network surface.
/// - NT AUTHORITY\SYSTEM added as sysadmin so the Conduit service (LocalSystem)
///   can create and own its database on THIS dedicated instance.
/// </summary>
public class SqlExpressBootstrapper
{
    public const string ConduitInstanceName = "CONDUIT";
    private const int ExitSuccess = 0;
    private const int ExitSuccessRebootRequired = 3010;

    /// <summary>Builds the silent setup command line for SQL Express.</summary>
    public static string BuildSetupArguments(string instanceName = ConduitInstanceName) =>
        "/Q /ACTION=Install /FEATURES=SQLEngine " +
        $"/INSTANCENAME={instanceName} " +
        "/IACCEPTSQLSERVERLICENSETERMS " +
        "/SQLSVCSTARTUPTYPE=Automatic " +
        "/BROWSERSVCSTARTUPTYPE=Disabled " +
        "/TCPENABLED=0 /NPENABLED=0 " +
        "/SQLSYSADMINACCOUNTS=\"BUILTIN\\Administrators\" \"NT AUTHORITY\\SYSTEM\"";

    public static bool IsSuccessExitCode(int exitCode, out bool rebootRequired)
    {
        rebootRequired = exitCode == ExitSuccessRebootRequired;
        return exitCode is ExitSuccess or ExitSuccessRebootRequired;
    }

    /// <summary>Connection string stamped into Provision:ConnectionString.</summary>
    public static string BuildConnectionString(string serverName, string database)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = serverName,
            InitialCatalog = database,
            IntegratedSecurity = true,
            TrustServerCertificate = true
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Idempotent T-SQL run against an EXISTING instance so the Conduit service
    /// (LocalSystem) can create its database. Not used on the bootstrapped
    /// CONDUIT instance (setup already grants SYSTEM sysadmin there).
    /// </summary>
    public static string BuildGrantServiceAccessScript() =>
        "IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'NT AUTHORITY\\SYSTEM') " +
        "CREATE LOGIN [NT AUTHORITY\\SYSTEM] FROM WINDOWS; " +
        "IF IS_SRVROLEMEMBER('dbcreator', N'NT AUTHORITY\\SYSTEM') = 0 " +
        "ALTER SERVER ROLE [dbcreator] ADD MEMBER [NT AUTHORITY\\SYSTEM];";

    /// <summary>
    /// Locates the SQL Express setup exe: explicit override first, then
    /// redist\SQLEXPR*.exe beside the installer. Null when not found.
    /// </summary>
    public static string? FindSetupExecutable(string installerDirectory, string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return File.Exists(overridePath) ? overridePath : null;

        var redist = Path.Combine(installerDirectory, "redist");
        if (!Directory.Exists(redist))
            return null;

        return Directory.GetFiles(redist, "SQLEXPR*.exe")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>Runs SQL Express setup silently. Returns null on success, error text on failure.</summary>
    public virtual string? Install(string setupExePath, SilentInstallLog log, TimeSpan? timeout = null)
    {
        var arguments = BuildSetupArguments();
        log.Info($"Installing SQL Server Express: \"{setupExePath}\" {arguments}");
        log.Info("This can take 10+ minutes on first install.");

        var startInfo = new ProcessStartInfo
        {
            FileName = setupExePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
            return "SQL Express setup process failed to start.";

        var limit = timeout ?? TimeSpan.FromMinutes(45);
        if (!process.WaitForExit((int)limit.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return $"SQL Express setup did not finish within {limit.TotalMinutes:0} minutes.";
        }

        if (!IsSuccessExitCode(process.ExitCode, out var rebootRequired))
            return $"SQL Express setup failed with exit code {process.ExitCode} (0x{process.ExitCode:X8}). " +
                   @"See %ProgramFiles%\Microsoft SQL Server\160\Setup Bootstrap\Log\Summary.txt.";

        if (rebootRequired)
            log.Warn("SQL Express setup succeeded but requested a reboot (3010). Continuing; reboot the host after install.");

        log.Info("SQL Server Express installed.");
        return null;
    }

    /// <summary>Grants LocalSystem dbcreator on an existing instance. Returns null on success, error text on failure.</summary>
    public virtual string? GrantServiceAccess(string serverName)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = serverName,
            InitialCatalog = "master",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = 10
        };

        try
        {
            using var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = BuildGrantServiceAccessScript();
            command.ExecuteNonQuery();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
