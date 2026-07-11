using Installer.Core.Models;
using Installer.Core.Services.Conduit;
using Xunit;

namespace Installer.Core.Tests;

public class RedistAuthenticityVerifierTests
{
    private static SilentInstallLog NullLog() =>
        new(Path.Combine(Path.GetTempPath(), $"redist-verify-test-{Guid.NewGuid():N}.log"));

    private static string WriteTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"redist-verify-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, content);
        return path;
    }

    // ── Pinned SHA-256 path ─────────────────────────────────────────────

    [Fact]
    public void PinnedHash_Match_Passes_WithoutConsultingAuthenticode()
    {
        // An unsigned non-PE file: only the pin can make this pass, proving the
        // pin path does not fall back to (or require) signature verification.
        var path = WriteTempFile("definitely not a signed PE");
        try
        {
            var pin = RedistAuthenticityVerifier.ComputeSha256(path);
            using var log = NullLog();

            Assert.Null(new RedistAuthenticityVerifier().Verify(path, pin, log));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PinnedHash_Mismatch_Fails_WithNoSignatureFallback()
    {
        // The file has a hash; the pin is a different (valid-format) hash.
        var path = WriteTempFile("payload v1");
        try
        {
            var wrongPin = new string('A', 64);
            using var log = NullLog();

            var error = new RedistAuthenticityVerifier().Verify(path, wrongPin, log);

            Assert.NotNull(error);
            Assert.Contains("pinned SHA-256", error);
            Assert.Contains("Refusing to execute", error);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PinnedHash_AcceptsGetFileHashStyleFormatting()
    {
        var path = WriteTempFile("some payload");
        try
        {
            var raw = RedistAuthenticityVerifier.ComputeSha256(path);
            var dashedLowercase = string.Join("-", Enumerable.Range(0, 32).Select(i => raw.Substring(i * 2, 2))).ToLowerInvariant();
            using var log = NullLog();

            Assert.Null(new RedistAuthenticityVerifier().Verify(path, dashedLowercase, log));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PinnedHash_InvalidFormat_FailsClosed()
    {
        var path = WriteTempFile("some payload");
        try
        {
            using var log = NullLog();
            var error = new RedistAuthenticityVerifier().Verify(path, "not-a-hash", log);

            Assert.NotNull(error);
            Assert.Contains("expressSetupSha256", error);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("xyz", false)]
    [InlineData("GG112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF", false)] // 64 chars, not hex
    [InlineData("00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF", true)]
    [InlineData("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff", true)]
    [InlineData("00-11-22-33-44-55-66-77-88-99-AA-BB-CC-DD-EE-FF-00-11-22-33-44-55-66-77-88-99-AA-BB-CC-DD-EE-FF", true)]
    public void TryNormalizeSha256Pin_ValidatesFormat(string? pin, bool expectedValid)
    {
        Assert.Equal(expectedValid, RedistAuthenticityVerifier.TryNormalizeSha256Pin(pin, out var normalized));
        if (expectedValid)
            Assert.Equal("00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF", normalized);
    }

    [Fact]
    public void SidecarValidation_RejectsMalformedPin()
    {
        var options = ConduitInstallOptions.Parse(
            """{ "enrollUrl": "https://x.example.com", "enrollCode": "c", "mode": "cloud-only", "sql": { "expressSetupSha256": "beef" } }""");

        Assert.Contains(options.Validate(), e => e.Contains("expressSetupSha256"));
    }

    // ── Authenticode path ───────────────────────────────────────────────

    [Fact]
    public void UnsignedRealPe_FailsAuthenticodeGate()
    {
        // Installer.Core.dll is a genuine PE with no Authenticode signature.
        var unsignedPe = typeof(RedistAuthenticityVerifier).Assembly.Location;
        using var log = NullLog();

        var error = new RedistAuthenticityVerifier().Verify(unsignedPe, pinnedSha256: null, log);

        Assert.NotNull(error);
        Assert.Contains("not Authenticode-signed", error);
        Assert.Contains("Refusing to execute", error);
    }

    [Fact]
    public void NonPeGarbage_FailsAuthenticodeGate()
    {
        var path = WriteTempFile("MZ this is not really a PE");
        try
        {
            using var log = NullLog();
            Assert.NotNull(new RedistAuthenticityVerifier().Verify(path, pinnedSha256: null, log));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Live positive check against a Microsoft-signed binary present on the
    /// machine (dev boxes and GitHub runners both carry these). Skips silently
    /// when none exists — the SQLEXPR redist itself can only be proven on a
    /// machine that has it (see NEEDS-LIVE-SMOKE in the docs).
    /// </summary>
    [Fact]
    public void MicrosoftSignedBinary_PassesAuthenticodeGate()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe")
        };

        var target = candidates.FirstOrDefault(File.Exists);
        if (target == null)
            return; // No known Microsoft-signed binary on this machine — nothing to assert.

        using var log = NullLog();
        var error = new RedistAuthenticityVerifier().Verify(target, pinnedSha256: null, log);

        Assert.Null(error);
    }

    /// <summary>
    /// A valid signature from the WRONG publisher must still fail: substitute
    /// the WinTrust boundary to simulate "signature fine", then let the real
    /// identity check reject a non-Microsoft signer (this test assembly has no
    /// certificate at all, which the identity pass reports as unreadable).
    /// </summary>
    [Fact]
    public void ValidSignatureFromNonMicrosoftSigner_Fails()
    {
        var unsignedPe = typeof(RedistAuthenticityVerifier).Assembly.Location;
        using var log = NullLog();

        var error = new SignatureAlwaysValidVerifier().Verify(unsignedPe, pinnedSha256: null, log);

        Assert.NotNull(error);
        Assert.Contains("NOT Microsoft", error);
    }

    private sealed class SignatureAlwaysValidVerifier : RedistAuthenticityVerifier
    {
        protected override string? VerifyAuthenticodeSignature(string filePath) => null;
    }
}
