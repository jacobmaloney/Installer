using Microsoft.Data.SqlClient;
using Microsoft.Win32;

namespace Installer.Core.Services.Conduit;

/// <summary>
/// Detects local SQL Server instances (registry Instance Names\SQL in both
/// registry views, plus the MSSQL service scan) and tests connectivity with
/// integrated auth. Pure selection/parsing logic is static and unit-testable;
/// only the registry/service/connection reads touch the machine.
/// </summary>
public class SqlInstanceDetector
{
    public const string DefaultInstanceName = "MSSQLSERVER";
    private const string InstanceNamesKey = @"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL";

    /// <summary>Server name for a connection string: default instance → "localhost", named → "localhost\NAME".</summary>
    public static string BuildServerName(string instanceName) =>
        string.Equals(instanceName, DefaultInstanceName, StringComparison.OrdinalIgnoreCase)
            ? "localhost"
            : $@"localhost\{instanceName.ToUpperInvariant()}";

    /// <summary>
    /// Orders candidate instance names by preference: the explicitly configured
    /// instance first, then default instance, SQLEXPRESS, CONDUIT, rest alphabetical.
    /// LocalDB-shaped names are excluded (unsupported by Conduit).
    /// </summary>
    public static IReadOnlyList<string> OrderByPreference(IEnumerable<string> instanceNames, string? preferredInstance)
    {
        var distinct = instanceNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim().ToUpperInvariant())
            .Where(n => !n.Contains("LOCALDB", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        var preferred = preferredInstance?.Trim().ToUpperInvariant();

        return distinct
            .OrderBy(n => n == preferred ? 0
                : n == DefaultInstanceName ? 1
                : n == "SQLEXPRESS" ? 2
                : n == SqlExpressBootstrapper.ConduitInstanceName ? 3
                : 4)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Extracts instance names from MSSQL service names ("MSSQLSERVER" or "MSSQL$NAME").</summary>
    public static string? InstanceNameFromServiceName(string serviceName)
    {
        if (string.Equals(serviceName, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase))
            return DefaultInstanceName;
        if (serviceName.StartsWith("MSSQL$", StringComparison.OrdinalIgnoreCase))
            return serviceName.Substring("MSSQL$".Length);
        return null;
    }

    /// <summary>Reads installed instance names from the registry (64-bit and 32-bit views) plus the service list.</summary>
    public virtual IReadOnlyList<string> GetInstalledInstanceNames()
    {
        var names = new List<string>();

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(InstanceNamesKey);
                if (key != null)
                    names.AddRange(key.GetValueNames());
            }
            catch
            {
                // Registry view unavailable — continue with what we have.
            }
        }

        try
        {
            foreach (var service in System.ServiceProcess.ServiceController.GetServices())
            {
                var name = InstanceNameFromServiceName(service.ServiceName);
                if (name != null)
                    names.Add(name);
            }
        }
        catch
        {
            // Service enumeration failure — registry results still stand.
        }

        return names.Select(n => n.Trim().ToUpperInvariant()).Distinct().ToList();
    }

    /// <summary>Tests connectivity to master with integrated auth. Returns null on success, error text on failure.</summary>
    public virtual string? TestConnect(string serverName, int timeoutSeconds = 5)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = serverName,
            InitialCatalog = "master",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = timeoutSeconds
        };

        try
        {
            using var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
