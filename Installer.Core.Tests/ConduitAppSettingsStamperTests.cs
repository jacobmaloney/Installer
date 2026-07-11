using System.Text.Json;
using Installer.Core.Services.Conduit;
using Xunit;

namespace Installer.Core.Tests;

public class ConduitAppSettingsStamperTests
{
    private const string PristinePayloadSettings = """
        {
          "Logging": { "LogLevel": { "Default": "Information" } },
          "AllowedHosts": "*",
          "ConnectionStrings": { "DefaultConnection": "" },
          "Jwt": { "SecretKey": "", "Issuer": "Conduit" }
        }
        """;

    [Fact]
    public void Stamp_AddsProvisionAndEnroll_PreservingExistingContent()
    {
        var result = ConduitAppSettingsStamper.Stamp(
            PristinePayloadSettings,
            connectionString: @"Data Source=localhost\CONDUIT;Initial Catalog=Conduit;Integrated Security=True",
            enrollUrl: "https://platform.example.com",
            enrollCode: "AB12CD34",
            adminUsername: "admin",
            serverPort: 5500,
            jwtSecretGenerator: () => "GENERATED-SECRET");

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        // Existing content preserved
        Assert.Equal("*", root.GetProperty("AllowedHosts").GetString());
        Assert.Equal("Conduit", root.GetProperty("Jwt").GetProperty("Issuer").GetString());
        Assert.Equal("Information", root.GetProperty("Logging").GetProperty("LogLevel").GetProperty("Default").GetString());

        // Stamp applied
        var provision = root.GetProperty("Provision");
        Assert.Contains(@"localhost\CONDUIT", provision.GetProperty("ConnectionString").GetString());
        Assert.Equal("admin", provision.GetProperty("AdminUsername").GetString());
        Assert.Equal("GENERATED-SECRET", provision.GetProperty("JwtSecretKey").GetString());
        Assert.Equal(5500, provision.GetProperty("ServerPort").GetInt32());

        var enroll = root.GetProperty("Enroll");
        Assert.Equal("https://platform.example.com", enroll.GetProperty("Url").GetString());
        Assert.Equal("AB12CD34", enroll.GetProperty("Code").GetString());
    }

    [Fact]
    public void Stamp_NeverWritesAdminPassword()
    {
        var result = ConduitAppSettingsStamper.Stamp(
            PristinePayloadSettings, "Server=.;Database=Conduit;Integrated Security=True",
            "https://p.example.com", "X1");

        Assert.DoesNotContain("AdminPassword", result);
    }

    [Fact]
    public void Stamp_KeepsExistingJwtSecret_OnRerun()
    {
        var first = ConduitAppSettingsStamper.Stamp(
            PristinePayloadSettings, "Server=.;Database=Conduit;Integrated Security=True",
            "https://p.example.com", "X1", jwtSecretGenerator: () => "FIRST");

        var second = ConduitAppSettingsStamper.Stamp(
            first, "Server=.;Database=Conduit;Integrated Security=True",
            "https://p.example.com", "X2", jwtSecretGenerator: () => "SECOND");

        using var doc = JsonDocument.Parse(second);
        Assert.Equal("FIRST", doc.RootElement.GetProperty("Provision").GetProperty("JwtSecretKey").GetString());
        // But the enroll code IS refreshed on re-run.
        Assert.Equal("X2", doc.RootElement.GetProperty("Enroll").GetProperty("Code").GetString());
    }

    [Fact]
    public void Stamp_NullOrEmptyExisting_ProducesValidStandaloneFile()
    {
        var result = ConduitAppSettingsStamper.Stamp(
            null, "Server=.;Database=Conduit;Integrated Security=True",
            "https://p.example.com", "X1");

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("Provision", out _));
        Assert.True(doc.RootElement.TryGetProperty("Enroll", out _));
    }

    [Fact]
    public void Stamp_OmitsOptionalFieldsWhenAbsent()
    {
        var result = ConduitAppSettingsStamper.Stamp(
            null, "Server=.;Database=Conduit;Integrated Security=True",
            "https://p.example.com", "X1", adminUsername: null, serverPort: null);

        using var doc = JsonDocument.Parse(result);
        var provision = doc.RootElement.GetProperty("Provision");
        Assert.False(provision.TryGetProperty("AdminUsername", out _));
        Assert.False(provision.TryGetProperty("ServerPort", out _));
        // JWT secret is always generated.
        Assert.False(string.IsNullOrWhiteSpace(provision.GetProperty("JwtSecretKey").GetString()));
    }

    [Fact]
    public void Stamp_MissingConnectionString_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ConduitAppSettingsStamper.Stamp(null, "", "https://p.example.com", "X1"));
    }

    [Fact]
    public void GenerateJwtSecret_Is64RandomBytesBase64()
    {
        var secret = ConduitAppSettingsStamper.GenerateJwtSecret();
        Assert.Equal(64, Convert.FromBase64String(secret).Length);
        Assert.NotEqual(secret, ConduitAppSettingsStamper.GenerateJwtSecret());
    }
}
