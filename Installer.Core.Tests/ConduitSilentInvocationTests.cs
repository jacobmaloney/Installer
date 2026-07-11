using Installer.Core.Services.Conduit;
using Xunit;

namespace Installer.Core.Tests;

public class ConduitSilentInvocationTests
{
    [Theory]
    [InlineData("--silent")]
    [InlineData("/silent")]
    [InlineData("/s")]
    [InlineData("/q")]
    [InlineData("--quiet")]
    public void Resolve_SilentFlags_TriggerSilentMode(string flag)
    {
        var invocation = ConduitSilentInvocation.Resolve(new[] { flag }, @"C:\nonexistent-dir-for-test");

        Assert.True(invocation.IsSilent);
        Assert.Null(invocation.ConfigPath);
    }

    [Fact]
    public void Resolve_ConfigArgument_SetsPath()
    {
        var invocation = ConduitSilentInvocation.Resolve(
            new[] { "--silent", "--config", @"D:\deploy\acme.provision.json" }, @"C:\nonexistent-dir-for-test");

        Assert.True(invocation.IsSilent);
        Assert.Equal(@"D:\deploy\acme.provision.json", invocation.ConfigPath);
    }

    [Fact]
    public void Resolve_SidecarNextToExe_AutoTriggersSilentMode()
    {
        var temp = Directory.CreateTempSubdirectory();
        try
        {
            var sidecar = Path.Combine(temp.FullName, "conduit.provision.json");
            File.WriteAllText(sidecar, "{}");

            var invocation = ConduitSilentInvocation.Resolve(Array.Empty<string>(), temp.FullName);

            Assert.True(invocation.IsSilent);
            Assert.Equal(sidecar, invocation.ConfigPath);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void Resolve_NoFlagsNoSidecar_StaysInteractive()
    {
        var temp = Directory.CreateTempSubdirectory();
        try
        {
            var invocation = ConduitSilentInvocation.Resolve(Array.Empty<string>(), temp.FullName);

            Assert.False(invocation.IsSilent);
            Assert.Null(invocation.ConfigPath);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void Resolve_ExplicitConfig_WinsOverSidecar()
    {
        var temp = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(temp.FullName, "conduit.provision.json"), "{}");

            var invocation = ConduitSilentInvocation.Resolve(
                new[] { "--silent", "--config", @"D:\other.json" }, temp.FullName);

            Assert.Equal(@"D:\other.json", invocation.ConfigPath);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }
}
