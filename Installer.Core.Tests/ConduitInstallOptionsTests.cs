using System.Text.Json;
using Installer.Core.Models;
using Xunit;

namespace Installer.Core.Tests;

public class ConduitInstallOptionsTests
{
    private const string FullSidecar = """
        {
          "enrollUrl": "https://platform.example.com",
          "enrollCode": "AB12CD34",
          "tenantSlug": "acme",
          "mode": "on-prem",
          "installPath": "D:\\Apps\\Conduit",
          "serviceName": "IdentityCenterConduit",
          "serverPort": 5501,
          "adminUsername": "conduit-admin",
          "sql": {
            "instanceName": "SQLEXPRESS",
            "database": "ConduitDb",
            "allowExpressInstall": false,
            "grantServiceAccess": false
          }
        }
        """;

    [Fact]
    public void Parse_FullSidecar_MapsEveryField()
    {
        var options = ConduitInstallOptions.Parse(FullSidecar);

        Assert.Equal("https://platform.example.com", options.EnrollUrl);
        Assert.Equal("AB12CD34", options.EnrollCode);
        Assert.Equal("acme", options.TenantSlug);
        Assert.Equal(ConduitInstallOptions.ModeOnPrem, options.Mode);
        Assert.Equal(@"D:\Apps\Conduit", options.InstallPath);
        Assert.Equal("IdentityCenterConduit", options.ServiceName);
        Assert.Equal(5501, options.ServerPort);
        Assert.Equal("conduit-admin", options.AdminUsername);
        Assert.Equal("SQLEXPRESS", options.Sql.InstanceName);
        Assert.Equal("ConduitDb", options.Sql.Database);
        Assert.False(options.Sql.AllowExpressInstall);
        Assert.False(options.Sql.GrantServiceAccess);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Parse_MinimalSidecar_AppliesDefaults()
    {
        var options = ConduitInstallOptions.Parse(
            """{ "enrollUrl": "https://p.example.com", "enrollCode": "X1", "mode": "cloud-only" }""");

        Assert.Equal(@"C:\Program Files\Conduit", options.InstallPath);
        Assert.Equal("IdentityCenterConduit", options.ServiceName);
        Assert.Null(options.ServerPort);
        Assert.Equal("Conduit", options.Sql.Database);
        Assert.True(options.Sql.AllowExpressInstall);
        Assert.True(options.Sql.GrantServiceAccess);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => ConduitInstallOptions.Parse("{ not json"));
    }

    [Theory]
    [InlineData("", "AB", "on-prem", "enrollUrl is required")]
    [InlineData("not a url", "AB", "on-prem", "must be an absolute https URL")]
    [InlineData("ftp://x.example.com", "AB", "on-prem", "must be an absolute https URL")]
    [InlineData("http://x.example.com", "AB", "on-prem", "must be an absolute https URL")]
    [InlineData("https://x.example.com", "", "on-prem", "enrollCode is required")]
    [InlineData("https://x.example.com", "AB", "onprem", "mode must be")]
    [InlineData("https://x.example.com", "AB", "", "mode must be")]
    public void Validate_RejectsBadCoreFields(string url, string code, string mode, string expectedFragment)
    {
        var options = new ConduitInstallOptions { EnrollUrl = url, EnrollCode = code, Mode = mode };
        Assert.Contains(options.Validate(), e => e.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Validate_RejectsOutOfRangePort(int port)
    {
        var options = ValidOptions();
        options.ServerPort = port;
        Assert.Contains(options.Validate(), e => e.Contains("serverPort"));
    }

    [Fact]
    public void Validate_RejectsLocalDbConnectionString()
    {
        var options = ValidOptions();
        options.Sql.ConnectionString = @"Server=(LocalDB)\MSSQLLocalDB;Database=Conduit;Integrated Security=true";
        Assert.Contains(options.Validate(), e => e.Contains("LocalDB", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsBlankInstallPathServiceNameAndDatabase()
    {
        var options = ValidOptions();
        options.InstallPath = " ";
        options.ServiceName = "";
        options.Sql.Database = "";

        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("installPath"));
        Assert.Contains(errors, e => e.Contains("serviceName"));
        Assert.Contains(errors, e => e.Contains("sql.database"));
    }

    private static ConduitInstallOptions ValidOptions() => new()
    {
        EnrollUrl = "https://platform.example.com",
        EnrollCode = "AB12CD34",
        Mode = ConduitInstallOptions.ModeCloudOnly
    };
}
