using System.Security.AccessControl;
using System.Security.Principal;
using Installer.Core.Models;
using Installer.Core.Services.Conduit;
using Xunit;

namespace Installer.Core.Tests;

public class ConduitDataDirectorySecurerTests
{
    [Fact]
    public void BuildAccessRules_ExactlyAdministratorsAndSystem_FullControl_Inherited()
    {
        var rules = ConduitDataDirectorySecurer.BuildAccessRules();

        Assert.Equal(2, rules.Count);

        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        Assert.Contains(rules, r => r.IdentityReference.Equals(adminsSid));
        Assert.Contains(rules, r => r.IdentityReference.Equals(systemSid));

        Assert.All(rules, r =>
        {
            Assert.Equal(FileSystemRights.FullControl, r.FileSystemRights);
            Assert.Equal(AccessControlType.Allow, r.AccessControlType);
            Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, r.InheritanceFlags);
            Assert.Equal(PropagationFlags.None, r.PropagationFlags);
        });
    }

    [Fact]
    public void BuildLockedDownSecurity_CutsInheritance_NoOtherPrincipals()
    {
        var security = ConduitDataDirectorySecurer.BuildLockedDownSecurity();

        Assert.True(security.AreAccessRulesProtected);

        var aces = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        Assert.Equal(2, aces.Count);

        var allowedSids = new[]
        {
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)
        };
        Assert.All(aces, ace => Assert.Contains((SecurityIdentifier)ace.IdentityReference, allowedSids));
    }
}

public class ConduitServiceInstallerTests
{
    [Fact]
    public void BuildCreateArguments_QuotedBinPath_AutoStart()
    {
        var args = ConduitServiceInstaller.BuildCreateArguments(
            "IdentityCenterConduit", @"C:\Program Files\Conduit\Conduit.Web.exe");

        // sc.exe requires the space after option= and the inner-quoted path.
        Assert.Equal(
            "create IdentityCenterConduit binPath= \"\\\"C:\\Program Files\\Conduit\\Conduit.Web.exe\\\"\" start= auto DisplayName= \"Identity Center Conduit\"",
            args);
    }

    [Fact]
    public void BuildConfigArguments_UpdatesBinPathAndStart()
    {
        var args = ConduitServiceInstaller.BuildConfigArguments(
            "IdentityCenterConduit", @"C:\Program Files\Conduit\Conduit.Web.exe");

        Assert.StartsWith("config IdentityCenterConduit binPath= ", args);
        Assert.Contains("start= auto", args);
    }

    [Fact]
    public void BuildFailureArguments_RestartsWithBackoff_DailyReset()
    {
        var args = ConduitServiceInstaller.BuildFailureArguments("IdentityCenterConduit");

        Assert.Equal("failure IdentityCenterConduit reset= 86400 actions= restart/5000/restart/10000/restart/30000", args);
    }
}

public class EnrollStatusReaderTests
{
    [Fact]
    public void TryParse_ReadsConduitStatusShape()
    {
        var status = EnrollStatusReader.TryParse("""
            {
              "Outcome": "Failed",
              "TimestampUtc": "2026-07-09T12:00:00.0000000Z",
              "ErrorCategory": "http-401",
              "Detail": "Enroll code rejected"
            }
            """);

        Assert.NotNull(status);
        Assert.Equal("Failed", status.Outcome);
        Assert.Equal("http-401", status.ErrorCategory);
        Assert.Equal("Enroll code rejected", status.Detail);
    }

    [Fact]
    public void TryParse_MalformedJson_ReturnsNull()
    {
        Assert.Null(EnrollStatusReader.TryParse("{ nope"));
    }

    [Theory]
    [InlineData("Success", SilentExitCode.Success)]
    [InlineData("Skipped-already-enrolled", SilentExitCode.Success)]
    [InlineData("Failed", SilentExitCode.EnrollmentFailed)]
    [InlineData("Skipped-unconfigured", SilentExitCode.EnrollmentFailed)]
    [InlineData("Something-new", SilentExitCode.SuccessEnrollPending)]
    public void MapToExitCode_CoversAllOutcomes(string outcome, SilentExitCode expected)
    {
        var status = new EnrollStatusReader.EnrollStatus(outcome, null, null);
        Assert.Equal(expected, EnrollStatusReader.MapToExitCode(status));
    }

    [Fact]
    public void MapToExitCode_NoStatus_IsPending()
    {
        Assert.Equal(SilentExitCode.SuccessEnrollPending, EnrollStatusReader.MapToExitCode(null));
    }

    [Theory]
    [InlineData("http-401", "rejected", true)]
    [InlineData(null, "The enroll code is invalid or expired", true)]
    [InlineData("network", "connection timed out", false)]
    public void LooksLikeStaleEnrollCode_Heuristic(string? category, string detail, bool expected)
    {
        var status = new EnrollStatusReader.EnrollStatus("Failed", category, detail);
        Assert.Equal(expected, EnrollStatusReader.LooksLikeStaleEnrollCode(status));
    }
}

public class SilentExitCodeTests
{
    [Fact]
    public void ExitCodes_AreDistinct()
    {
        var values = Enum.GetValues<SilentExitCode>().Cast<int>().ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
    }

    [Fact]
    public void FailureClasses_OccupyDocumentedBands()
    {
        Assert.Equal(0, (int)SilentExitCode.Success);
        Assert.Equal(10, (int)SilentExitCode.ConfigError);
        Assert.InRange((int)SilentExitCode.PreflightNotElevated, 20, 29);
        Assert.InRange((int)SilentExitCode.PreflightRuntimeMissing, 20, 29);
        Assert.InRange((int)SilentExitCode.SqlNoUsableInstance, 30, 39);
        Assert.InRange((int)SilentExitCode.SqlConnectFailed, 30, 39);
        Assert.Equal(40, (int)SilentExitCode.ExtractFailed);
        Assert.Equal(50, (int)SilentExitCode.ConfigStampFailed);
        Assert.InRange((int)SilentExitCode.DataDirAclFailed, 60, 69);
        Assert.InRange((int)SilentExitCode.ServiceInstallFailed, 70, 79);
        Assert.Equal(80, (int)SilentExitCode.EnrollmentFailed);
    }
}
