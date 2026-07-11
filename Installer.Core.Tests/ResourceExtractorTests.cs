using Installer.Core.Services;
using Xunit;

namespace Installer.Core.Tests;

public class ResourceExtractorTests
{
    private const string Target = @"C:\Program Files\Conduit";

    [Theory]
    [InlineData("Conduit.Web.exe")]
    [InlineData("wwwroot/css/site.css")]
    [InlineData(@"wwwroot\css\site.css")]
    public void ResolveDestinationPath_ContainedEntries_ResolveUnderTarget(string entryName)
    {
        var resolved = ResourceExtractor.ResolveDestinationPath(Target, entryName);

        Assert.StartsWith(Target + @"\", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"..\..\evil.txt")]
    [InlineData("../../evil.txt")]
    [InlineData(@"..\evil.txt")]
    [InlineData(@"sub\..\..\evil.txt")]
    [InlineData(@"C:\Windows\System32\evil.dll")]
    [InlineData(@"\evil.txt")]
    public void ResolveDestinationPath_EscapingEntries_AreRejected(string entryName)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ResourceExtractor.ResolveDestinationPath(Target, entryName));

        Assert.Contains("outside the target directory", ex.Message);
    }

    [Fact]
    public void ResolveDestinationPath_DotDotThatStaysInside_IsAllowed()
    {
        var resolved = ResourceExtractor.ResolveDestinationPath(Target, @"sub\..\file.txt");

        Assert.Equal(Target + @"\file.txt", resolved, ignoreCase: true);
    }

    [Fact]
    public void ResolveDestinationPath_TrailingSeparatorOnTarget_IsHandled()
    {
        var resolved = ResourceExtractor.ResolveDestinationPath(Target + @"\", "file.txt");
        Assert.Equal(Target + @"\file.txt", resolved, ignoreCase: true);

        Assert.Throws<InvalidOperationException>(
            () => ResourceExtractor.ResolveDestinationPath(Target + @"\", @"..\evil.txt"));
    }
}
