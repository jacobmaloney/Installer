using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Installer.Core.Services.Conduit;
using Xunit;

namespace Installer.Core.Tests;

/// <summary>
/// HIGH-2 installer half: the Provision/Enroll stamp goes to the restricted
/// secrets.json; the Program Files appsettings.json receives NOTHING secret;
/// ACLs are exact; the JWT secret survives re-runs and legacy upgrades.
/// </summary>
public class ConduitSecretsWriterTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory().FullName;
    private readonly string _installDir = Directory.CreateTempSubdirectory().FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, true); } catch { }
        try { Directory.Delete(_installDir, true); } catch { }
    }

    private sealed class TestSecretsWriter : ConduitSecretsWriter
    {
        private readonly string _path;
        public TestSecretsWriter(string path) => _path = path;
        public override string SecretsPath => _path;
    }

    private static SilentInstallLog NullLog() =>
        new(Path.Combine(Path.GetTempPath(), $"secrets-writer-test-{Guid.NewGuid():N}.log"));

    [Fact]
    public void Stamp_WritesSecretsJson_AndLeavesProgramFilesAppSettingsUntouched()
    {
        // A payload appsettings.json sits in the install dir, as extraction left it.
        var appSettingsPath = Path.Combine(_installDir, "appsettings.json");
        var payloadContent = """{ "Jwt": { "SecretKey": "", "Issuer": "Conduit" }, "ConnectionStrings": { "DefaultConnection": "" } }""";
        File.WriteAllText(appSettingsPath, payloadContent);

        var secretsPath = Path.Combine(_dataDir, "secrets.json");
        var writer = new TestSecretsWriter(secretsPath);
        using var log = NullLog();

        var error = writer.StampProvisionAndEnroll(
            "Server=sql01;Database=Conduit", "https://platform.example.com", "CODE123",
            adminUsername: "admin", serverPort: 5500, legacyAppSettingsJson: null, log);

        Assert.Null(error);

        // Secrets landed in secrets.json only.
        var secrets = JsonNode.Parse(File.ReadAllText(secretsPath))!.AsObject();
        Assert.Equal("Server=sql01;Database=Conduit", secrets["Provision"]?["ConnectionString"]?.GetValue<string>());
        Assert.Equal("CODE123", secrets["Enroll"]?["Code"]?.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(secrets["Provision"]?["JwtSecretKey"]?.GetValue<string>()));

        // KEY-ABSENCE on the Program Files file: byte-identical, and provably
        // free of every stamped secret shape.
        Assert.Equal(payloadContent, File.ReadAllText(appSettingsPath));
        var appSettings = JsonNode.Parse(File.ReadAllText(appSettingsPath))!.AsObject();
        Assert.Null(appSettings["Provision"]);
        Assert.Null(appSettings["Enroll"]);
        Assert.Equal("", appSettings["ConnectionStrings"]?["DefaultConnection"]?.GetValue<string>());
        Assert.Equal("", appSettings["Jwt"]?["SecretKey"]?.GetValue<string>());
    }

    [Fact]
    public void Stamp_IsIdempotent_JwtSecretSurvivesRerun()
    {
        var secretsPath = Path.Combine(_dataDir, "secrets.json");
        var writer = new TestSecretsWriter(secretsPath);
        using var log = NullLog();

        Assert.Null(writer.StampProvisionAndEnroll("Server=a;Database=D", "https://x.example.com", "CODE1", null, null, null, log));
        var first = JsonNode.Parse(File.ReadAllText(secretsPath))!.AsObject()["Provision"]!["JwtSecretKey"]!.GetValue<string>();

        Assert.Null(writer.StampProvisionAndEnroll("Server=a;Database=D", "https://x.example.com", "CODE2", null, null, null, log));
        var after = JsonNode.Parse(File.ReadAllText(secretsPath))!.AsObject();

        Assert.Equal(first, after["Provision"]?["JwtSecretKey"]?.GetValue<string>());
        Assert.Equal("CODE2", after["Enroll"]?["Code"]?.GetValue<string>()); // fresh code re-stamped
    }

    [Fact]
    public void Stamp_ExactAcl_NoBuiltinUsers_NoInheritance()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var secretsPath = Path.Combine(_dataDir, "secrets.json");
        using var log = NullLog();
        Assert.Null(new TestSecretsWriter(secretsPath).StampProvisionAndEnroll(
            "Server=a;Database=D", "https://x.example.com", "CODE", null, null, null, log));

        var security = new FileInfo(secretsPath).GetAccessControl(AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.NotEmpty(rules);

        var allowed = new[]
        {
            WindowsIdentity.GetCurrent().User,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        };
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        foreach (var rule in rules)
        {
            Assert.False(rule.IsInherited);
            var sid = (SecurityIdentifier)rule.IdentityReference;
            Assert.NotEqual(users, sid);
            Assert.Contains(sid, allowed!);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        }
    }

    [Fact]
    public void CarryLegacyJwtSecret_UpgradeFromBaseFileStamp_KeepsOldKey()
    {
        var legacy = """{ "Provision": { "ConnectionString": "Server=old", "JwtSecretKey": "legacy-key" }, "Enroll": { "Code": "OLD" } }""";

        var composed = ConduitSecretsWriter.CarryLegacyJwtSecret(secretsJson: null, legacyAppSettingsJson: legacy);

        Assert.NotNull(composed);
        var root = JsonNode.Parse(composed!)!.AsObject();
        Assert.Equal("legacy-key", root["Provision"]?["JwtSecretKey"]?.GetValue<string>());
        // ONLY the JWT key is carried — stale legacy connection/code are not.
        Assert.Null(root["Provision"]?["ConnectionString"]);
        Assert.Null(root["Enroll"]);
    }

    [Fact]
    public void CarryLegacyJwtSecret_SecretsJsonValueWins()
    {
        var secrets = """{ "Provision": { "JwtSecretKey": "authoritative" } }""";
        var legacy = """{ "Provision": { "JwtSecretKey": "legacy-key" } }""";

        var composed = ConduitSecretsWriter.CarryLegacyJwtSecret(secrets, legacy);

        Assert.Equal(secrets, composed); // untouched
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json at all")]
    [InlineData("""{ "Provision": { } }""")]
    public void CarryLegacyJwtSecret_NoUsableLegacyKey_ReturnsSecretsUnchanged(string? legacy)
    {
        Assert.Null(ConduitSecretsWriter.CarryLegacyJwtSecret(null, legacy));
    }
}

public class RedistStagerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class TestStager : RedistStager
    {
        private readonly string _dir;
        public TestStager(string dir) => _dir = dir;
        public override string StagingRoot => _dir;
    }

    private static SilentInstallLog NullLog() =>
        new(Path.Combine(Path.GetTempPath(), $"stager-test-{Guid.NewGuid():N}.log"));

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [Fact]
    public void Stage_CopiesIntoFreshGuidDirectory_WithExactAcl_AndCleanupRemovesIt()
    {
        // Setting the directory owner to Administrators requires an elevated
        // token; on a non-elevated run there is nothing meaningful to assert.
        if (!IsElevated())
            return;

        var source = Path.Combine(_root, "SQLEXPR_x64_ENU.exe");
        File.WriteAllText(source, "redist-bytes");
        var stagingRoot = Path.Combine(_root, "data", "redist-staging");
        var stager = new TestStager(stagingRoot);
        using var log = NullLog();

        var staged = stager.Stage(source, log, out var error);

        Assert.Null(error);
        Assert.NotNull(staged);
        Assert.StartsWith(stagingRoot, staged!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("redist-bytes", File.ReadAllText(staged!));

        // A fresh GUID-named run directory sits between the root and the file.
        var runDirectory = Path.GetDirectoryName(staged)!;
        Assert.NotEqual(Path.GetFullPath(stagingRoot), Path.GetFullPath(runDirectory));
        Assert.True(Guid.TryParseExact(Path.GetFileName(runDirectory), "N", out _), "run directory must be GUID-named");

        // Root + run directory ACLs: protected, exactly Administrators + SYSTEM.
        foreach (var directory in new[] { stagingRoot, runDirectory })
        {
            var security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access);
            Assert.True(security.AreAccessRulesProtected);
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            var allowed = new[]
            {
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
            };
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            Assert.NotEmpty(rules);
            foreach (var rule in rules)
            {
                Assert.False(rule.IsInherited);
                var sid = (SecurityIdentifier)rule.IdentityReference;
                Assert.NotEqual(users, sid);
                Assert.Contains(sid, allowed);
            }
        }

        stager.Cleanup();
        Assert.False(Directory.Exists(stagingRoot));
    }

    [Fact]
    public void Stage_PreExistingStagingRoot_IsTornDownAndNeverReused()
    {
        if (!IsElevated())
            return;

        var source = Path.Combine(_root, "SQLEXPR_x64_ENU.exe");
        File.WriteAllText(source, "redist-bytes");
        var stagingRoot = Path.Combine(_root, "data", "redist-staging");

        // Adversary pre-creates the staging root (lax ACL) with planted content.
        Directory.CreateDirectory(stagingRoot);
        var planted = Path.Combine(stagingRoot, "SQLEXPR_x64_ENU.exe");
        File.WriteAllText(planted, "planted-bytes");

        using var log = NullLog();
        var staged = new TestStager(stagingRoot).Stage(source, log, out var error);

        Assert.Null(error);
        Assert.NotNull(staged);
        Assert.False(File.Exists(planted), "pre-existing staging content must be torn down");
        Assert.Equal("redist-bytes", File.ReadAllText(staged!));
    }

    [Fact]
    public void Stage_PreExistingRootWithOpenHandle_FailsClosed()
    {
        // The fail-closed teardown runs before any privileged work, so this
        // test is meaningful without elevation.
        var stagingRoot = Path.Combine(_root, "data", "redist-staging");
        Directory.CreateDirectory(stagingRoot);
        var held = Path.Combine(stagingRoot, "held.bin");
        File.WriteAllText(held, "x");

        var source = Path.Combine(_root, "SQLEXPR_x64_ENU.exe");
        File.WriteAllText(source, "redist-bytes");

        using var log = NullLog();
        using (File.Open(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var staged = new TestStager(stagingRoot).Stage(source, log, out var error);

            Assert.Null(staged);
            Assert.NotNull(error);
            Assert.Contains("stage", error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Stage_MissingSource_FailsWithErrorNotThrow()
    {
        var stager = new TestStager(Path.Combine(_root, "staging"));
        using var log = NullLog();

        var staged = stager.Stage(Path.Combine(_root, "does-not-exist.exe"), log, out var error);

        Assert.Null(staged);
        Assert.NotNull(error);
        Assert.Contains("stage", error, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Mirror of the Conduit-repo RestrictedFileWriter tests (hand-mirrored code).</summary>
public class RestrictedFileWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Write_ReplacesExistingContentCompletely_AndLeavesNoTempFiles()
    {
        var path = Path.Combine(_dir, "secret.json");
        RestrictedFileWriter.Write(path, "first-version-with-longer-content");
        RestrictedFileWriter.Write(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir)); // no *.tmp leftovers
    }

    [Fact]
    public void Write_FailedRewrite_PreservesTheOnlyCopy_AndCleansTemp()
    {
        // HIGH-2 regression: a failed rewrite (here: destination locked, the
        // same observable class as disk-full/crash) must never truncate or
        // delete the pre-existing secret — only the temp file may die.
        var path = Path.Combine(_dir, "secret.json");
        RestrictedFileWriter.Write(path, "precious-only-copy");

        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<Exception>(() => RestrictedFileWriter.Write(path, "replacement"));
        }

        Assert.Equal("precious-only-copy", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir)); // temp cleaned, original intact
    }

    [Fact]
    public void Write_PreExistingLaxFile_GetsLockedAclOnRewrite()
    {
        // MEDIUM-1 regression: a secrets file created earlier with inherited
        // (lax) permissions must come out of the next Write fully locked.
        if (!OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(_dir, "secret.json");
        File.WriteAllText(path, "created-lax");
        var laxSecurity = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        Assert.False(laxSecurity.AreAccessRulesProtected); // sanity: it really was inheriting

        RestrictedFileWriter.Write(path, "now-locked");

        Assert.Equal("now-locked", File.ReadAllText(path));
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            Assert.False(rule.IsInherited);
            Assert.NotEqual(users, (SecurityIdentifier)rule.IdentityReference);
        }
    }
}
