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

/// <summary>
/// Trailer location under both layouts: unsigned (trailer at EOF) and signed
/// (embed-then-sign puts the Authenticode certificate table AFTER the trailer,
/// with up to 7 bytes of alignment padding between them).
/// </summary>
public class ResourceExtractorTrailerTests
{
    private static readonly byte[] Marker = System.Text.Encoding.ASCII.GetBytes("APPDATA\0");
    private static readonly byte[] Payload = System.Text.Encoding.ASCII.GetBytes("PK-fake-zip-payload-bytes");

    /// <summary>
    /// Minimal PE32+ shape: MZ, e_lfanew=0x80, "PE\0\0", optional-header magic
    /// 0x20B, security data-directory entry (index 4) at 0x128. Only the fields
    /// GetTrailerSearchEnd reads are populated.
    /// </summary>
    private static byte[] BuildExe(byte[] payload, bool signed, int padding)
    {
        var header = new byte[0x200];
        header[0] = (byte)'M';
        header[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(header, 0x3C);
        header[0x80] = (byte)'P';
        header[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)0x20B).CopyTo(header, 0x98);

        using var ms = new MemoryStream();
        ms.Write(header);
        ms.Write(Marker);
        ms.Write(payload);
        ms.Write(BitConverter.GetBytes(payload.Length));
        ms.Write(Marker);

        if (!signed)
            return ms.ToArray();

        for (var i = 0; i < padding; i++)
            ms.WriteByte(0);
        var certTableOffset = (uint)ms.Length;
        var certTable = new byte[64];
        Array.Fill(certTable, (byte)0xCC);
        ms.Write(certTable);

        var bytes = ms.ToArray();
        BitConverter.GetBytes(certTableOffset).CopyTo(bytes, 0x128); // security dir file offset
        BitConverter.GetBytes((uint)certTable.Length).CopyTo(bytes, 0x12C); // security dir size
        return bytes;
    }

    [Fact]
    public void Unsigned_TrailerAtEndOfFile_IsLocated()
    {
        using var stream = new MemoryStream(BuildExe(Payload, signed: false, padding: 0));

        var trailer = ResourceExtractor.LocateTrailer(stream);

        Assert.NotNull(trailer);
        Assert.Equal(0x200 + Marker.Length, trailer.Value.ZipStart);
        Assert.Equal(Payload.Length, trailer.Value.ZipSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(7)]
    public void Signed_TrailerBeforeCertificateTable_IsLocated(int padding)
    {
        using var stream = new MemoryStream(BuildExe(Payload, signed: true, padding));

        var trailer = ResourceExtractor.LocateTrailer(stream);

        Assert.NotNull(trailer);
        Assert.Equal(0x200 + Marker.Length, trailer.Value.ZipStart);
        Assert.Equal(Payload.Length, trailer.Value.ZipSize);
    }

    [Fact]
    public void Signed_SearchEndIsCertificateTableOffset()
    {
        var bytes = BuildExe(Payload, signed: true, padding: 4);
        using var stream = new MemoryStream(bytes);

        var searchEnd = ResourceExtractor.GetTrailerSearchEnd(stream);

        Assert.Equal(bytes.Length - 64, searchEnd); // cert table is the final 64 bytes
    }

    [Fact]
    public void Unsigned_SearchEndIsFileLength()
    {
        var bytes = BuildExe(Payload, signed: false, padding: 0);
        using var stream = new MemoryStream(bytes);

        Assert.Equal(bytes.Length, ResourceExtractor.GetTrailerSearchEnd(stream));
    }

    [Fact]
    public void NoTrailer_ReturnsNull()
    {
        using var stream = new MemoryStream(BuildExe(Array.Empty<byte>(), signed: false, padding: 0)[..0x200]);

        Assert.Null(ResourceExtractor.LocateTrailer(stream));
    }

    [Fact]
    public void Signed_SecondTrailerSmuggledAfterCertTable_InnerSignedTrailerWins()
    {
        // Adversary appends a full, well-formed trailer AFTER the certificate
        // table (outside Authenticode coverage). The extractor must resolve the
        // INNER trailer — the one the signature covers — not the smuggled one.
        var smuggled = System.Text.Encoding.ASCII.GetBytes("EVIL-payload-not-covered-by-signature");
        using var ms = new MemoryStream();
        ms.Write(BuildExe(Payload, signed: true, padding: 4));
        ms.Write(Marker);
        ms.Write(smuggled);
        ms.Write(BitConverter.GetBytes(smuggled.Length));
        ms.Write(Marker);

        var trailer = ResourceExtractor.LocateTrailer(ms);

        Assert.NotNull(trailer);
        Assert.Equal(0x200 + Marker.Length, trailer.Value.ZipStart);
        Assert.Equal(Payload.Length, trailer.Value.ZipSize);
    }

    [Fact]
    public void Signed_OnlyTrailerIsAfterCertTable_ReturnsNull()
    {
        // Signed exe with NO embedded trailer; the only trailer sits after the
        // certificate table (pure post-sign append). Nothing inside the signed
        // region parses, so extraction must refuse.
        var header = new byte[0x200];
        header[0] = (byte)'M';
        header[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(header, 0x3C);
        header[0x80] = (byte)'P';
        header[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)0x20B).CopyTo(header, 0x98);

        using var ms = new MemoryStream();
        ms.Write(header);
        ms.Write(new byte[0x40]); // signed image body, no trailer
        var certTableOffset = (uint)ms.Length;
        var certTable = new byte[64];
        Array.Fill(certTable, (byte)0xCC);
        ms.Write(certTable);
        ms.Write(Marker); // smuggled trailer, entirely outside signature coverage
        ms.Write(Payload);
        ms.Write(BitConverter.GetBytes(Payload.Length));
        ms.Write(Marker);

        var bytes = ms.ToArray();
        BitConverter.GetBytes(certTableOffset).CopyTo(bytes, 0x128);
        BitConverter.GetBytes((uint)certTable.Length).CopyTo(bytes, 0x12C);
        using var stream = new MemoryStream(bytes);

        Assert.Null(ResourceExtractor.LocateTrailer(stream));
    }

    [Fact]
    public void NonPeGarbage_FallsBackToEofSearch_AndStillFindsTrailer()
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[100]); // no MZ header
        ms.Write(Marker);
        ms.Write(Payload);
        ms.Write(BitConverter.GetBytes(Payload.Length));
        ms.Write(Marker);

        var trailer = ResourceExtractor.LocateTrailer(ms);

        Assert.NotNull(trailer);
        Assert.Equal(100 + Marker.Length, trailer.Value.ZipStart);
    }
}
