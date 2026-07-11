# Conduit Installer Release Pipeline

The contract between the packaging script (`Build-ConduitInstaller.ps1`, this
repo), the tag-triggered release workflow (`.github/workflows/release-installer.yml`
in the **Conduit** repo), and the runtime verification inside the installer.
This is the document to re-review when anything in the signing or distribution
story changes.

## Stage order is a security invariant

```
1. Publish        dotnet publish Conduit.Web (payload) + InstallerRuntime (installer shell)
2. Package/Embed  zip payload, append trailer to the exe: [MARKER][ZIP][SIZE:4][MARKER]
3. Sign           Authenticode over the exe INCLUDING the trailer
4. Verify         fail the build unless the final artifact is exactly right
```

**Embed-then-sign, never sign-then-embed.** Authenticode covers all file bytes
except the certificate table itself. Because signing happens after the embed,
signtool appends the certificate table AFTER the payload trailer and the
signature covers the payload. Appending or modifying anything after signing
flips verification to `HashMismatch`.

Enforcement is mechanical, not conventional:

- The **Sign stage refuses to run** if no payload trailer is present in the exe
  (`ORDERING VIOLATION`, build fails).
- The **Verify stage fails the build** unless, on the final artifact:
  - signed modes: an Authenticode certificate table exists, the trailer parses
    **inside the signed region** (before the certificate table's file offset,
    read from the PE security data directory), the embedded zip fully inflates,
    and `Get-AuthenticodeSignature` reports `Valid` over the final bytes;
  - `Unsigned` mode: no certificate table, trailer parses at end of file,
    status is `NotSigned`, and the exe is named `ConduitSetup-UNSIGNED.exe`.
- A `.sha256` sidecar is written for the final exe; the release workflow writes
  another for the distribution zip.

### Runtime counterpart

A signed exe's trailer is no longer at end-of-file (the certificate table
follows it, plus up to 7 alignment padding bytes). `ResourceExtractor`
(`LocateTrailer` / `GetTrailerSearchEnd`) parses the PE security data directory
and searches for the trailer ending at the certificate table offset, falling
back to end-of-file for unsigned builds. Unit-tested against synthetic
signed/unsigned layouts in `ResourceExtractorTests`.

## What the signature does and does not cover

| Artifact | Covered by |
|---|---|
| `ConduitSetup.exe` incl. embedded payload zip | Authenticode signature (Trusted Signing cert) |
| `conduit.provision.sample.json` | nothing (template only; customer rewrites it) |
| `redist\SQLEXPR_x64_ENU.exe` | **NOT the exe signature** — verified at install time, see below |
| Distribution zip | `.sha256` sidecar in blob storage (integrity, not authenticity) |

## Redist verification (side-by-side SQL Express)

The redist ships next to the installer and runs ELEVATED. Before execution:

0. **TOCTOU staging** (`RedistStager`) — the redist is copied into an
   admin-only staging directory (created ACL-first under the locked-down
   `%PROGRAMDATA%\Conduit`), and BOTH the verification and the execution run
   against that protected copy: the bytes checked are the bytes run, with no
   swap window in the world-writable source location. The staged copy is
   deleted after setup runs.

Then `RedistAuthenticityVerifier` (Installer.Core) requires ONE of:

1. **Pinned SHA-256** — sidecar `sql.expressSetupSha256`. Exact file match;
   works offline; the sole gate when configured (a mismatch fails hard with no
   signature fallback).
2. **Authenticode** (default) — WinVerifyTrust must report a valid embedded
   signature (digest intact, trusted chain, timestamp honored), AND the signer
   subject's **O RDN must equal `Microsoft Corporation`** (parsed RDN, not a
   substring — CN varies by product and can be crafted), AND `X509Chain.Build`
   must succeed with the chain terminating at a **thumbprint-pinned Microsoft
   root CA**: Microsoft Root Certificate Authority 2010
   (`3B1EFD3A66EA28B16697394703A72CA340A05BD5`), 2011
   (`8F43288AD272F3103B6FB1428485EA3014C0BCFE`), or Microsoft Identity
   Verification Root Certificate Authority 2020
   (`F40042E2E5F7E8EF8189FED15519AECE42C3BFA2`). The MD5-era 1997 root is
   deliberately excluded; a legitimate redist chaining elsewhere is handled by
   pinning its hash.

Failure = the redist is **not executed**; exit code **33**
(`SqlRedistVerificationFailed`). Revocation is deliberately not checked
(offline installs; the gate is integrity + publisher identity — pin the hash
for stricter guarantees).

The release workflow additionally pins the redist at **download** time:
`vars.SQLEXPR_URL` + `vars.SQLEXPR_SHA256`, verified before packaging, cached
by hash.

## Signing modes (`Build-ConduitInstaller.ps1 -SigningMode …`)

| Mode | Use | Parameters |
|---|---|---|
| `Unsigned` (default) | dev builds only | none — loud banner, `-UNSIGNED` exe name |
| `SignTool` | local certificate | `-CertificateThumbprint`, `-TimestampUrl` |
| `TrustedSigning` | release | `-TrustedSigningEndpoint`, `-TrustedSigningAccount`, `-TrustedSigningCertProfile`, `-TrustedSigningDlib` |

`TrustedSigning` drives signtool with Azure's `/dlib` (package
`Microsoft.Trusted.Signing.Client`); credentials come from the environment via
`DefaultAzureCredential` (CI: `azure/login` federated credentials). **Real
signing is currently BLOCKED on Jacob's Azure Trusted Signing identity
validation** — the pipeline runs `Unsigned` until the three secrets land, at
which point signing is a config drop-in with no code change.

## Release trigger

Tags matching **`installer/v*`** on the Conduit repo (e.g. `installer/v1.0.0`),
mirroring IdentityCenter's `deploy/azure/*` tag-only convention: a plain trunk
push never produces a distributable. The version suffix flows into
`ConduitInstaller-v<version>[-UNSIGNED].zip`.

The workflow: checks out Conduit + this repo (packager builds from source at
release time — no committed binaries to go stale), runs the Installer unit
tests as a gate, acquires/verifies the redist, runs the packaging script, zips
the package, uploads zip + `.sha256` to blob container `installer-releases`
(path `conduit/<version>/`), and attaches the same files as a workflow
artifact.

## Needs Jacob (one-time)

- `INSTALLER_REPO_TOKEN` — read token for the cross-repo checkout of this repo.
- `INSTALLER_BLOB_CONNECTION_STRING` — storage account for releases, and
  **create the `installer-releases` container** (the workflow never creates
  Azure resources).
- `AZURE_TRUSTED_SIGNING_ENDPOINT` / `_ACCOUNT` / `_PROFILE` — after identity
  validation completes; also uncomment the `azure/login` step and add its
  federated-credential secrets.
- `vars.SQLEXPR_URL` + `vars.SQLEXPR_SHA256` — official SQL Server 2022 Express
  `SQLEXPR_x64_ENU.exe` direct URL and its `Get-FileHash` SHA-256 (capture once
  from microsoft.com).

## Not provable without live pieces

- A real signed build end-to-end (needs the Trusted Signing account): the
  signed-layout parsing is unit-tested against synthetic PE files on both the
  C# and PowerShell sides, and the unsigned pipeline is exercised end-to-end,
  but `signtool /dlib` + `Verify` on a genuinely signed `ConduitSetup.exe` has
  not run.
- The redist Authenticode gate against the actual `SQLEXPR_x64_ENU.exe` (no
  local copy): validated against other Microsoft-signed binaries (dotnet.exe)
  in unit tests.
- The blob upload and cross-repo checkout (need the secrets).
