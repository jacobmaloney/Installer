using Installer.Core.Services.Conduit;
using Xunit;

namespace Installer.Core.Tests;

public class SqlInstanceDetectorTests
{
    [Theory]
    [InlineData("MSSQLSERVER", "localhost")]
    [InlineData("mssqlserver", "localhost")]
    [InlineData("SQLEXPRESS", @"localhost\SQLEXPRESS")]
    [InlineData("conduit", @"localhost\CONDUIT")]
    public void BuildServerName_MapsDefaultAndNamedInstances(string instance, string expected)
    {
        Assert.Equal(expected, SqlInstanceDetector.BuildServerName(instance));
    }

    [Fact]
    public void OrderByPreference_PreferredThenDefaultThenExpressThenConduitThenAlpha()
    {
        var ordered = SqlInstanceDetector.OrderByPreference(
            new[] { "ZEBRA", "CONDUIT", "SQLEXPRESS", "MSSQLSERVER", "ALPHA" },
            preferredInstance: "zebra");

        Assert.Equal(new[] { "ZEBRA", "MSSQLSERVER", "SQLEXPRESS", "CONDUIT", "ALPHA" }, ordered);
    }

    [Fact]
    public void OrderByPreference_NoPreferred_DefaultFirst()
    {
        var ordered = SqlInstanceDetector.OrderByPreference(
            new[] { "ALPHA", "SQLEXPRESS", "MSSQLSERVER" }, null);

        Assert.Equal(new[] { "MSSQLSERVER", "SQLEXPRESS", "ALPHA" }, ordered);
    }

    [Fact]
    public void OrderByPreference_FiltersLocalDbAndDuplicatesAndBlanks()
    {
        var ordered = SqlInstanceDetector.OrderByPreference(
            new[] { "MSSQLLocalDB", "LOCALDB2022", "sqlexpress", "SQLEXPRESS", " ", "" }, null);

        Assert.Equal(new[] { "SQLEXPRESS" }, ordered);
    }

    [Theory]
    [InlineData("MSSQLSERVER", "MSSQLSERVER")]
    [InlineData("MSSQL$SQLEXPRESS", "SQLEXPRESS")]
    [InlineData("MSSQL$CONDUIT", "CONDUIT")]
    [InlineData("SQLBrowser", null)]
    [InlineData("W3SVC", null)]
    public void InstanceNameFromServiceName_ParsesSqlServiceNames(string serviceName, string? expected)
    {
        Assert.Equal(expected, SqlInstanceDetector.InstanceNameFromServiceName(serviceName));
    }
}

public class SqlExpressBootstrapperTests
{
    [Fact]
    public void BuildSetupArguments_SilentDedicatedInstance_WindowsAuthOnly_NoNetworkSurface()
    {
        var args = SqlExpressBootstrapper.BuildSetupArguments();

        Assert.Contains("/Q ", args);
        Assert.Contains("/ACTION=Install", args);
        Assert.Contains("/INSTANCENAME=CONDUIT", args);
        Assert.Contains("/IACCEPTSQLSERVERLICENSETERMS", args);
        Assert.Contains("/SQLSVCSTARTUPTYPE=Automatic", args);
        Assert.Contains("/TCPENABLED=0", args);
        Assert.Contains("/NPENABLED=0", args);
        Assert.Contains("/BROWSERSVCSTARTUPTYPE=Disabled", args);
        Assert.Contains("/SQLSYSADMINACCOUNTS=\"BUILTIN\\Administrators\" \"NT AUTHORITY\\SYSTEM\"", args);
        // Mixed mode must stay OFF: no SECURITYMODE, no sa password.
        Assert.DoesNotContain("SECURITYMODE", args);
        Assert.DoesNotContain("SAPWD", args);
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(3010, true, true)]
    [InlineData(1, false, false)]
    [InlineData(-2068119551, false, false)]
    public void IsSuccessExitCode_TreatsZeroAndRebootRequiredAsSuccess(int code, bool expectedSuccess, bool expectedReboot)
    {
        var success = SqlExpressBootstrapper.IsSuccessExitCode(code, out var rebootRequired);
        Assert.Equal(expectedSuccess, success);
        Assert.Equal(expectedReboot, rebootRequired);
    }

    [Fact]
    public void BuildConnectionString_IntegratedAuthTrustedCert()
    {
        var connectionString = SqlExpressBootstrapper.BuildConnectionString(@"localhost\CONDUIT", "Conduit");

        Assert.Contains(@"Data Source=localhost\CONDUIT", connectionString);
        Assert.Contains("Initial Catalog=Conduit", connectionString);
        Assert.Contains("Integrated Security=True", connectionString);
        Assert.Contains("Trust Server Certificate=True", connectionString);
        Assert.DoesNotContain("Password", connectionString);
    }

    [Fact]
    public void BuildGrantServiceAccessScript_IsIdempotentAndTargetsDbcreatorOnly()
    {
        var script = SqlExpressBootstrapper.BuildGrantServiceAccessScript();

        Assert.Contains("IF NOT EXISTS", script);
        Assert.Contains(@"CREATE LOGIN [NT AUTHORITY\SYSTEM] FROM WINDOWS", script);
        Assert.Contains("IS_SRVROLEMEMBER('dbcreator'", script);
        Assert.Contains("ALTER SERVER ROLE [dbcreator]", script);
        Assert.DoesNotContain("sysadmin", script);
    }

    [Fact]
    public void FindSetupExecutable_OverridePathWins_MissingOverrideReturnsNull()
    {
        var temp = Directory.CreateTempSubdirectory();
        try
        {
            var setupExe = Path.Combine(temp.FullName, "SQLEXPR_x64_ENU.exe");
            File.WriteAllText(setupExe, "stub");

            Assert.Equal(setupExe, SqlExpressBootstrapper.FindSetupExecutable(@"C:\anywhere", setupExe));
            Assert.Null(SqlExpressBootstrapper.FindSetupExecutable(@"C:\anywhere", Path.Combine(temp.FullName, "missing.exe")));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void FindSetupExecutable_FindsRedistNextToInstaller()
    {
        var temp = Directory.CreateTempSubdirectory();
        try
        {
            var redist = Directory.CreateDirectory(Path.Combine(temp.FullName, "redist"));
            var setupExe = Path.Combine(redist.FullName, "SQLEXPR_x64_ENU.exe");
            File.WriteAllText(setupExe, "stub");
            File.WriteAllText(Path.Combine(redist.FullName, "unrelated.exe"), "stub");

            Assert.Equal(setupExe, SqlExpressBootstrapper.FindSetupExecutable(temp.FullName, null));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void FindSetupExecutable_NoRedist_ReturnsNull()
    {
        var temp = Directory.CreateTempSubdirectory();
        try
        {
            Assert.Null(SqlExpressBootstrapper.FindSetupExecutable(temp.FullName, null));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }
}
