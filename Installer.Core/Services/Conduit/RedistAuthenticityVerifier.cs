using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Installer.Core.Services.Conduit;

/// <summary>
/// Authenticity gate for the side-by-side SQL Express redist BEFORE it is
/// executed elevated. The redist ships next to the installer exe and is never
/// covered by the installer's own Authenticode signature, so it must prove
/// itself independently:
///
///  - Pinned SHA-256 (sql.expressSetupSha256): exact expected file. Works
///    offline and for air-gapped/repackaged distributions. When a pin is
///    configured it is the sole gate — a mismatch fails hard with no
///    signature fallback (a wrong hash means the wrong file, full stop).
///  - Otherwise Authenticode: WinVerifyTrust must report a valid signature
///    over the file (hash intact, chain to a trusted root, timestamp
///    semantics honored), AND the signer subject's Organization RDN must be
///    exactly "Microsoft Corporation" with the chain terminating at a
///    Microsoft root CA.
///
/// Revocation is deliberately not checked (installs run on offline/proxied
/// boxes; the gate is integrity + publisher identity — a customer needing
/// stricter guarantees pins the hash).
///
/// Failing verification means the redist is NOT executed; the install exits
/// with SilentExitCode.SqlRedistVerificationFailed (33).
/// </summary>
public class RedistAuthenticityVerifier
{
    /// <summary>
    /// Expected Organization RDN of the signer. Microsoft varies the CN per
    /// product (SQL redists: "Microsoft Corporation"; .NET: ".NET") but the O
    /// is stable, and it is matched as a parsed RDN — not a substring — so a
    /// crafted CN cannot impersonate it.
    /// </summary>
    private const string ExpectedSignerOrganization = "Microsoft Corporation";

    /// <summary>
    /// Pinned Microsoft root CAs, by SHA-1 certificate thumbprint (not subject
    /// text, which an attacker-controlled trusted root could imitate). Values
    /// verified against the Windows trusted root store. Deliberately excludes
    /// the MD5-era 1997 "Microsoft Root Authority" root. A legitimate redist
    /// chaining elsewhere is handled by pinning its hash (sql.expressSetupSha256).
    /// </summary>
    private static readonly string[] AcceptedRootThumbprints =
    {
        "3B1EFD3A66EA28B16697394703A72CA340A05BD5", // CN=Microsoft Root Certificate Authority 2010
        "8F43288AD272F3103B6FB1428485EA3014C0BCFE", // CN=Microsoft Root Certificate Authority 2011
        "F40042E2E5F7E8EF8189FED15519AECE42C3BFA2"  // CN=Microsoft Identity Verification Root Certificate Authority 2020 (Azure Trusted Signing)
    };

    /// <summary>Returns null when the file is safe to execute, else the reason to refuse.</summary>
    public virtual string? Verify(string filePath, string? pinnedSha256, SilentInstallLog log)
    {
        var fileName = Path.GetFileName(filePath);

        if (!string.IsNullOrWhiteSpace(pinnedSha256))
        {
            if (!TryNormalizeSha256Pin(pinnedSha256, out var expected))
                return $"sql.expressSetupSha256 is not a valid SHA-256 (expected 64 hex characters): '{pinnedSha256}'.";

            var actual = ComputeSha256(filePath);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                return $"SQL Express setup '{fileName}' does not match the pinned SHA-256 (sql.expressSetupSha256). " +
                       $"Expected {expected}, got {actual}. Refusing to execute it.";

            log.Info($"SQL Express setup '{fileName}' matches the pinned SHA-256 — authenticity gate passed.");
            return null;
        }

        var signatureError = VerifyAuthenticodeSignature(filePath);
        if (signatureError != null)
            return $"SQL Express setup '{fileName}' failed Authenticode verification: {signatureError} " +
                   "Refusing to execute it elevated. Re-download the redist from microsoft.com, " +
                   "or pin the expected hash via sql.expressSetupSha256.";

        var identityError = VerifySignerIsMicrosoft(filePath);
        if (identityError != null)
            return $"SQL Express setup '{fileName}' carries a valid signature that is NOT Microsoft's: {identityError} " +
                   "Refusing to execute it elevated.";

        log.Info($"SQL Express setup '{fileName}' has a valid Authenticode signature from Microsoft Corporation " +
                 "chaining to a Microsoft root CA — authenticity gate passed.");
        return null;
    }

    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Accepts common hash formats (Get-FileHash output, dashed, mixed case).
    /// Normalized form is 64 uppercase hex characters.
    /// </summary>
    public static bool TryNormalizeSha256Pin(string? pin, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(pin))
            return false;

        var stripped = new string(pin.Where(c => c != '-' && c != ':' && !char.IsWhiteSpace(c)).ToArray());
        if (stripped.Length != 64 || !stripped.All(Uri.IsHexDigit))
            return false;

        normalized = stripped.ToUpperInvariant();
        return true;
    }

    /// <summary>
    /// WinVerifyTrust boundary: valid embedded Authenticode signature over the
    /// file's current bytes. Returns null on success, reason text on failure.
    /// Virtual so tests can substitute the OS boundary.
    /// </summary>
    protected virtual string? VerifyAuthenticodeSignature(string filePath)
    {
        uint result;
        try
        {
            result = WinVerifyTrustFile(filePath);
        }
        catch (Exception ex)
        {
            return $"WinVerifyTrust could not run: {ex.Message}.";
        }

        return result switch
        {
            0 => null,
            TRUST_E_NOSIGNATURE => "the file is not Authenticode-signed.",
            TRUST_E_BAD_DIGEST => "the file has been modified since it was signed (digest mismatch).",
            CERT_E_UNTRUSTEDROOT => "the signature chain does not terminate at a trusted root.",
            TRUST_E_EXPLICIT_DISTRUST => "the signing certificate is explicitly distrusted.",
            CERT_E_EXPIRED => "the signing certificate is expired and the signature has no valid timestamp.",
            _ => $"WinVerifyTrust returned 0x{result:X8}."
        };
    }

    /// <summary>
    /// Identity check on top of the trust check: the signer's Organization RDN
    /// must be exactly "Microsoft Corporation" and the chain root must be a
    /// Microsoft root CA. WinVerifyTrust already proved validity/trust; this
    /// pass only pins WHO.
    /// </summary>
    protected virtual string? VerifySignerIsMicrosoft(string filePath)
    {
        X509Certificate2 signer;
        try
        {
            signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
        }
        catch (Exception ex)
        {
            return $"could not read the signing certificate: {ex.Message}.";
        }

        if (!HasOrganization(signer.SubjectName, ExpectedSignerOrganization))
            return $"the signer is '{signer.Subject}', expected O={ExpectedSignerOrganization}.";

        using var chain = new X509Chain();
        // Trust was already established by WinVerifyTrust (with timestamp
        // semantics); this build identifies the root, so ignore time validity
        // (redist signing certs age out while the timestamped signature stays valid).
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags =
            X509VerificationFlags.IgnoreNotTimeValid | X509VerificationFlags.IgnoreCtlNotTimeValid;

        if (!chain.Build(signer))
        {
            var statuses = string.Join("; ", chain.ChainStatus
                .Select(s => s.StatusInformation.Trim())
                .Where(s => s.Length > 0));
            return $"the certificate chain could not be validated ({(statuses.Length > 0 ? statuses : "unknown chain error")}).";
        }

        if (chain.ChainElements.Count == 0)
            return "the certificate chain could not be built.";

        var root = chain.ChainElements[^1].Certificate;
        if (!AcceptedRootThumbprints.Contains(root.Thumbprint, StringComparer.OrdinalIgnoreCase))
            return $"the chain terminates at '{root.Subject}' (thumbprint {root.Thumbprint}), which is not a pinned Microsoft root CA.";

        return null;
    }

    /// <summary>Parsed RDN comparison (OID 2.5.4.10 = organizationName).</summary>
    private static bool HasOrganization(X500DistinguishedName subject, string expectedOrganization)
    {
        foreach (var rdn in subject.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.HasMultipleElements)
                continue;
            if (rdn.GetSingleElementType().Value == "2.5.4.10" &&
                string.Equals(rdn.GetSingleElementValue(), expectedOrganization, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // ── WinVerifyTrust P/Invoke ─────────────────────────────────────────

    private const uint TRUST_E_NOSIGNATURE = 0x800B0100;
    private const uint CERT_E_EXPIRED = 0x800B0101;
    private const uint CERT_E_UNTRUSTEDROOT = 0x800B0109;
    private const uint TRUST_E_EXPLICIT_DISTRUST = 0x800B0111;
    private const uint TRUST_E_BAD_DIGEST = 0x80096010;

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public int cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public int cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WintrustData pWVTData);

    private static uint WinVerifyTrustFile(string filePath)
    {
        var pathPtr = Marshal.StringToCoTaskMemUni(filePath);
        var fileInfoPtr = IntPtr.Zero;
        try
        {
            var fileInfo = new WintrustFileInfo
            {
                cbStruct = Marshal.SizeOf<WintrustFileInfo>(),
                pcwszFilePath = pathPtr
            };
            fileInfoPtr = Marshal.AllocCoTaskMem(fileInfo.cbStruct);
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);

            var data = new WintrustData
            {
                cbStruct = Marshal.SizeOf<WintrustData>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = fileInfoPtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
            };

            var action = ActionGenericVerifyV2;
            var result = WinVerifyTrust(InvalidHandleValue, ref action, ref data);

            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(InvalidHandleValue, ref action, ref data);

            return result;
        }
        finally
        {
            if (fileInfoPtr != IntPtr.Zero)
                Marshal.FreeCoTaskMem(fileInfoPtr);
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }
}
