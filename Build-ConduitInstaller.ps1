# Builds the unattended Conduit installer package.
#
# STAGE ORDER IS A SECURITY INVARIANT (see docs/ReleasePipeline.md):
#
#   Publish -> Package/Embed -> Sign -> Verify
#
# The payload trailer MUST be embedded before signing so the Authenticode
# signature covers it (signtool then appends the certificate table AFTER the
# trailer). The Sign stage refuses to run if no trailer is present, and the
# Verify stage fails the build if the FINAL file's trailer is missing/corrupt
# or its signature state is not exactly what the signing mode promises.
#
# Output layout (side-by-side payload - SQL Express is NOT embedded in the exe;
# it must land on disk to run anyway and would quadruple the exe size):
#
#   <OutputPath>\ConduitInstaller\
#       ConduitSetup.exe                  signed release build
#       ConduitSetup-UNSIGNED.exe         unsigned dev build (never distribute)
#       ConduitSetup*.exe.sha256          SHA-256 sidecar
#       conduit.provision.sample.json     sidecar template (rename to conduit.provision.json)
#       redist\                           drop SQLEXPR_x64_ENU.exe here (see redist\README.txt)
#
# The embed uses the existing InstallerPackager format:
#   [EXE][MARKER "APPDATA\0"][ZIP][SIZE:4][MARKER]
#
# Signing modes:
#   Unsigned       (default) dev builds; loud warning; exe named ConduitSetup-UNSIGNED.exe
#   SignTool       local certificate: -CertificateThumbprint (+ -TimestampUrl)
#   TrustedSigning Azure Trusted Signing via signtool /dlib:
#                  -TrustedSigningEndpoint -TrustedSigningAccount -TrustedSigningCertProfile
#                  -TrustedSigningDlib (path to Azure.CodeSigning.Dlib.dll)
#                  Auth comes from the environment (DefaultAzureCredential: az login,
#                  federated CI credentials, or AZURE_* env vars).

param(
    [string]$ConduitRepo = (Join-Path (Split-Path -Parent $PSScriptRoot) "Conduit"),
    [string]$Configuration = "Release",
    [string]$OutputPath = ".\publish",
    [string]$SqlExpressSetup,  # optional: path to SQLEXPR_x64_ENU.exe to copy into redist\

    [ValidateSet('Unsigned', 'SignTool', 'TrustedSigning')]
    [string]$SigningMode = 'Unsigned',

    # SignTool mode (local certificate store)
    [string]$CertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.acs.microsoft.com',

    # TrustedSigning mode (Azure Trusted Signing through signtool /dlib)
    [string]$TrustedSigningEndpoint,     # e.g. https://eus.codesigning.azure.net
    [string]$TrustedSigningAccount,
    [string]$TrustedSigningCertProfile,
    [string]$TrustedSigningDlib,         # path to Azure.CodeSigning.Dlib.dll (Microsoft.Trusted.Signing.Client package)

    [string]$SignToolPath                # optional explicit signtool.exe (else PATH, then Windows Kits)
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Add-Type -AssemblyName System.IO.Compression | Out-Null

function Write-Step { param($m) Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Fail { param($m) Write-Host "ERROR: $m" -ForegroundColor Red; exit 1 }

# ── PE / trailer inspection ─────────────────────────────────────────────────

# File offset of the Authenticode certificate table (PE security data
# directory), or 0 when the file is unsigned / not a parseable PE.
function Get-PeSecurityDirectoryOffset {
    param([string]$Path)
    $fs = [System.IO.File]::OpenRead($Path)
    try {
        $br = New-Object System.IO.BinaryReader($fs)
        if ($fs.Length -lt 0x40) { return [long]0 }
        if ($br.ReadUInt16() -ne 0x5A4D) { return [long]0 }          # "MZ"
        $fs.Position = 0x3C
        $peOffset = $br.ReadInt32()
        if ($peOffset -le 0 -or $peOffset -gt ($fs.Length - 0x100)) { return [long]0 }
        $fs.Position = $peOffset
        if ($br.ReadUInt32() -ne 0x00004550) { return [long]0 }      # "PE\0\0"
        $fs.Position = $peOffset + 24
        $magic = $br.ReadUInt16()
        if ($magic -eq 0x20B)     { $ddBase = $peOffset + 24 + 112 } # PE32+
        elseif ($magic -eq 0x10B) { $ddBase = $peOffset + 24 + 96 }  # PE32
        else { return [long]0 }
        $fs.Position = $ddBase + (4 * 8)                             # security dir = index 4
        $secOffset = $br.ReadUInt32()
        $secSize = $br.ReadUInt32()
        if ($secOffset -gt 0 -and $secSize -gt 0 -and $secOffset -lt $fs.Length) { return [long]$secOffset }
        return [long]0
    } finally { $fs.Dispose() }
}

# Parses the payload trailer ([MARKER][ZIP][SIZE:4][MARKER]) ending at
# $SearchEnd (0 = end of file; pass the certificate-table offset for signed
# files — signtool may insert up to 7 alignment padding bytes after the
# trailer). Fully opens the embedded zip to prove it is intact. Returns
# @{ ZipStart; ZipSize; FileCount } or $null.
function Get-EmbeddedTrailer {
    param([string]$Path, [long]$SearchEnd = 0)
    $marker = [System.Text.Encoding]::ASCII.GetBytes("APPDATA`0")
    $fs = [System.IO.File]::OpenRead($Path)
    try {
        $end = if ($SearchEnd -gt 0) { $SearchEnd } else { $fs.Length }
        for ($pad = 0; $pad -le 7; $pad++) {
            $markerEnd = $end - $pad
            if ($markerEnd -lt 20) { break }

            $fs.Position = $markerEnd - 8
            $finalMarker = New-Object byte[] 8
            [void]$fs.Read($finalMarker, 0, 8)
            if (-not [System.Linq.Enumerable]::SequenceEqual([byte[]]$finalMarker, [byte[]]$marker)) { continue }

            $fs.Position = $markerEnd - 12
            $sizeBytes = New-Object byte[] 4
            [void]$fs.Read($sizeBytes, 0, 4)
            $zipSize = [BitConverter]::ToInt32($sizeBytes, 0)
            if ($zipSize -le 0 -or $zipSize -gt ($markerEnd - 20)) { continue }

            $initialMarkerStart = $markerEnd - 12 - $zipSize - 8
            $fs.Position = $initialMarkerStart
            $initialMarker = New-Object byte[] 8
            [void]$fs.Read($initialMarker, 0, 8)
            if (-not [System.Linq.Enumerable]::SequenceEqual([byte[]]$initialMarker, [byte[]]$marker)) { continue }

            # Prove the zip is structurally intact: parse the central directory
            # and fully inflate every entry. (Byte-level integrity of signed
            # builds is guaranteed by the Authenticode check in Verify — this
            # catches truncation/corruption that breaks the archive structure.)
            $fs.Position = $initialMarkerStart + 8
            $zipBytes = New-Object byte[] $zipSize
            $read = 0
            while ($read -lt $zipSize) {
                $n = $fs.Read($zipBytes, $read, $zipSize - $read)
                if ($n -eq 0) { return $null }
                $read += $n
            }
            $ms = New-Object System.IO.MemoryStream(,$zipBytes)
            try {
                $archive = New-Object System.IO.Compression.ZipArchive($ms, [System.IO.Compression.ZipArchiveMode]::Read)
                $fileCount = $archive.Entries.Count
                foreach ($entry in $archive.Entries) {
                    $entryStream = $entry.Open()
                    try { $entryStream.CopyTo([System.IO.Stream]::Null) } finally { $entryStream.Dispose() }
                }
                $archive.Dispose()
            } catch {
                return $null
            }

            return @{ ZipStart = $initialMarkerStart + 8; ZipSize = $zipSize; FileCount = $fileCount }
        }
        return $null
    } finally { $fs.Dispose() }
}

# ── Signing ─────────────────────────────────────────────────────────────────

function Resolve-SignTool {
    if ($SignToolPath) {
        if (-not (Test-Path $SignToolPath)) { Fail "signtool not found at -SignToolPath '$SignToolPath'." }
        return $SignToolPath
    }
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $kits = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($kits) { return $kits.FullName }
    Fail "signtool.exe not found (PATH or Windows Kits). Install the Windows SDK or pass -SignToolPath."
}

function Invoke-SignStage {
    param([string]$ExePath)

    # ORDER ENFORCEMENT: the payload must already be embedded so the signature
    # covers it. Signing first and appending later would leave the payload
    # outside the signed bytes — exactly the tampering channel this closes.
    $trailer = Get-EmbeddedTrailer -Path $ExePath
    if (-not $trailer) {
        Fail "ORDERING VIOLATION: Sign stage invoked but no embedded payload trailer was found in $ExePath. The Package/Embed stage must complete before signing."
    }

    switch ($SigningMode) {
        'Unsigned' {
            Write-Host ""
            Write-Host "############################################################" -ForegroundColor Yellow
            Write-Host "##  UNSIGNED BUILD - development only.                    ##" -ForegroundColor Yellow
            Write-Host "##  The exe will be named ConduitSetup-UNSIGNED.exe.      ##" -ForegroundColor Yellow
            Write-Host "##  Do NOT distribute this artifact to customers.         ##" -ForegroundColor Yellow
            Write-Host "############################################################" -ForegroundColor Yellow
        }
        'SignTool' {
            if (-not $CertificateThumbprint) { Fail "SigningMode SignTool requires -CertificateThumbprint." }
            $signtool = Resolve-SignTool
            Write-Host "Signing with local certificate $CertificateThumbprint (timestamp: $TimestampUrl)"
            & $signtool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /sha1 $CertificateThumbprint $ExePath
            if ($LASTEXITCODE -ne 0) { Fail "signtool sign failed (exit $LASTEXITCODE)." }
        }
        'TrustedSigning' {
            $missing = @()
            if (-not $TrustedSigningEndpoint)    { $missing += '-TrustedSigningEndpoint' }
            if (-not $TrustedSigningAccount)     { $missing += '-TrustedSigningAccount' }
            if (-not $TrustedSigningCertProfile) { $missing += '-TrustedSigningCertProfile' }
            if (-not $TrustedSigningDlib)        { $missing += '-TrustedSigningDlib' }
            if ($missing.Count -gt 0) {
                Fail ("SigningMode TrustedSigning requires: {0}. These come from the Azure Trusted Signing account (blocked on identity validation - see docs/ReleasePipeline.md)." -f ($missing -join ', '))
            }
            if (-not (Test-Path $TrustedSigningDlib)) { Fail "Azure.CodeSigning.Dlib.dll not found at '$TrustedSigningDlib' (ships in the Microsoft.Trusted.Signing.Client NuGet package)." }

            $metadataPath = Join-Path ([System.IO.Path]::GetTempPath()) "trusted-signing-metadata-$PID.json"
            @{
                Endpoint               = $TrustedSigningEndpoint
                CodeSigningAccountName = $TrustedSigningAccount
                CertificateProfileName = $TrustedSigningCertProfile
            } | ConvertTo-Json | Out-File -FilePath $metadataPath -Encoding ascii

            $signtool = Resolve-SignTool
            Write-Host "Signing via Azure Trusted Signing ($TrustedSigningAccount / $TrustedSigningCertProfile)"
            try {
                & $signtool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /dlib $TrustedSigningDlib /dmdf $metadataPath $ExePath
                if ($LASTEXITCODE -ne 0) { Fail "signtool sign (Trusted Signing) failed (exit $LASTEXITCODE)." }
            } finally {
                Remove-Item $metadataPath -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

# Post-sign gate. Fails the build unless the FINAL artifact is exactly right:
#  - signed modes: an Authenticode cert table exists, the trailer parses INSIDE
#    the signed region (before the cert table), and Get-AuthenticodeSignature
#    reports Valid over the final bytes. Any post-sign append or edit flips the
#    status to HashMismatch and the build dies here.
#  - Unsigned mode: no signature present, trailer parses at end of file.
function Invoke-VerifyStage {
    param([string]$ExePath)

    $secOffset = Get-PeSecurityDirectoryOffset -Path $ExePath

    if ($SigningMode -eq 'Unsigned') {
        if ($secOffset -ne 0) { Fail "VERIFY: unsigned build unexpectedly carries an Authenticode certificate table." }
        $trailer = Get-EmbeddedTrailer -Path $ExePath
        if (-not $trailer) { Fail "VERIFY: payload trailer missing or corrupt in the final exe." }
        $sig = Get-AuthenticodeSignature -FilePath $ExePath
        if ($sig.Status -ne 'NotSigned') { Fail "VERIFY: expected NotSigned for an unsigned build, got '$($sig.Status)'." }
        Write-Host "Verify OK (unsigned dev build): trailer intact, $($trailer.FileCount) files in payload."
        return
    }

    if ($secOffset -eq 0) { Fail "VERIFY: signing mode $SigningMode but the final exe has no Authenticode certificate table." }

    $trailer = Get-EmbeddedTrailer -Path $ExePath -SearchEnd $secOffset
    if (-not $trailer) {
        Fail "VERIFY: payload trailer not found INSIDE the signed region (before the certificate table). Embed/sign order was violated or the file was tampered with after signing."
    }

    $sig = Get-AuthenticodeSignature -FilePath $ExePath
    if ($sig.Status -ne 'Valid') {
        Fail "VERIFY: Authenticode signature over the FINAL packaged file is '$($sig.Status)' ($($sig.StatusMessage)). This indicates post-sign tampering or a broken signing step."
    }

    Write-Host "Verify OK: signature Valid (signer: $($sig.SignerCertificate.Subject)); trailer covered by the signature ($($trailer.FileCount) files in payload)."
}

# ── Stage 0: layout ─────────────────────────────────────────────────────────

$ConduitWebProject = Join-Path $ConduitRepo "src\Conduit.Web\Conduit.Web.csproj"
if (-not (Test-Path $ConduitWebProject)) {
    Fail "Conduit.Web project not found at $ConduitWebProject (pass -ConduitRepo)."
}

$OutputPath = [System.IO.Path]::GetFullPath((Join-Path $ScriptDir $OutputPath))
$PackageDir = Join-Path $OutputPath "ConduitInstaller"
$WorkDir    = Join-Path $OutputPath "conduit-work"

foreach ($dir in @($PackageDir, $WorkDir)) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

# ── Stage 1: Publish ────────────────────────────────────────────────────────

Write-Step "Stage 1/4 Publish: Conduit.Web (framework-dependent folder publish)"
$ConduitPublishDir = Join-Path $WorkDir "conduit-app"
dotnet publish $ConduitWebProject -c $Configuration -o $ConduitPublishDir --nologo
if ($LASTEXITCODE -ne 0) { Fail "Conduit publish failed." }

# The payload must never ship an environment settings file: Conduit's SetupService
# owns appsettings.Production.json and the installer stamps the BASE file only.
$prodSettings = Join-Path $ConduitPublishDir "appsettings.Production.json"
if (Test-Path $prodSettings) { Remove-Item $prodSettings -Force }
$devSettings = Join-Path $ConduitPublishDir "appsettings.Development.json"
if (Test-Path $devSettings) { Remove-Item $devSettings -Force }

Write-Step "Stage 1/4 Publish: InstallerRuntime (self-contained single file)"
$RuntimePublishDir = Join-Path $WorkDir "runtime"
dotnet publish (Join-Path $ScriptDir "InstallerRuntime\InstallerRuntime.csproj") `
    -c $Configuration -o $RuntimePublishDir `
    /p:PublishSingleFile=true /p:SelfContained=true /p:RuntimeIdentifier=win-x64 `
    /p:IncludeNativeLibrariesForSelfExtract=true --nologo
if ($LASTEXITCODE -ne 0) { Fail "InstallerRuntime publish failed." }

# ── Stage 2: Package / Embed ────────────────────────────────────────────────

Write-Step "Stage 2/4 Package: zip payload + embed into installer exe"
$PayloadZip = Join-Path $WorkDir "AppPayload.zip"
Compress-Archive -Path (Join-Path $ConduitPublishDir "*") -DestinationPath $PayloadZip -CompressionLevel Optimal

$InstallerExe = Join-Path $PackageDir "ConduitSetup.exe"
Copy-Item (Join-Path $RuntimePublishDir "InstallerRuntime.exe") $InstallerExe

# Append: [MARKER][ZIP][SIZE:4 little-endian][MARKER] - must match ResourceExtractor.
$marker = [System.Text.Encoding]::ASCII.GetBytes("APPDATA`0")
$zipBytes = [System.IO.File]::ReadAllBytes($PayloadZip)
$sizeBytes = [System.BitConverter]::GetBytes([int]$zipBytes.Length)
$stream = [System.IO.File]::Open($InstallerExe, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write)
try {
    $stream.Write($marker, 0, $marker.Length)
    $stream.Write($zipBytes, 0, $zipBytes.Length)
    $stream.Write($sizeBytes, 0, $sizeBytes.Length)
    $stream.Write($marker, 0, $marker.Length)
} finally {
    $stream.Dispose()
}

# ── Stage 3: Sign (AFTER embed — the signature must cover the payload) ─────

Write-Step "Stage 3/4 Sign (mode: $SigningMode)"
Invoke-SignStage -ExePath $InstallerExe

if ($SigningMode -eq 'Unsigned') {
    $FinalExe = Join-Path $PackageDir "ConduitSetup-UNSIGNED.exe"
    Move-Item $InstallerExe $FinalExe -Force
} else {
    $FinalExe = $InstallerExe
}

# ── Stage 4: Verify (fails the build on any ordering/tamper violation) ─────

Write-Step "Stage 4/4 Verify final artifact"
Invoke-VerifyStage -ExePath $FinalExe

$hash = (Get-FileHash -Algorithm SHA256 -Path $FinalExe).Hash
$hashFile = "$FinalExe.sha256"
"$hash  $(Split-Path -Leaf $FinalExe)" | Out-File -FilePath $hashFile -Encoding ascii
Write-Host "SHA-256: $hash"

# ── Package layout ──────────────────────────────────────────────────────────

Write-Step "Laying out package"
Copy-Item (Join-Path $ScriptDir "docs\conduit.provision.sample.json") (Join-Path $PackageDir "conduit.provision.sample.json")

$RedistDir = New-Item -ItemType Directory -Path (Join-Path $PackageDir "redist") -Force
@"
Place the SQL Server Express setup executable here as SQLEXPR_x64_ENU.exe.
Download: https://www.microsoft.com/en-us/download/details.aspx?id=104781 (SQL Server 2022 Express)
Use the full extractable package (SQLEXPR_x64_ENU.exe, ~260 MB), not the small web-installer stub -
target machines may not be able to download during install.

The installer only runs this when NO usable local SQL Server instance exists;
it never installs over an existing instance. Before executing it ELEVATED, the
installer verifies the file's Authenticode signature chains to a Microsoft
root CA (or matches the sidecar's sql.expressSetupSha256 pin) - a redist that
fails verification is never run (exit code 33).
"@ | Out-File -FilePath (Join-Path $RedistDir "README.txt") -Encoding utf8

if ($SqlExpressSetup) {
    if (Test-Path $SqlExpressSetup) {
        Copy-Item $SqlExpressSetup (Join-Path $RedistDir ([System.IO.Path]::GetFileName($SqlExpressSetup)))
        Write-Host "Bundled SQL Express setup from $SqlExpressSetup" -ForegroundColor Green
    } else {
        Write-Host "WARNING: -SqlExpressSetup path not found: $SqlExpressSetup (package built without it)" -ForegroundColor Yellow
    }
} else {
    Write-Host "NOTE: no -SqlExpressSetup supplied - drop SQLEXPR_x64_ENU.exe into redist\ before distribution." -ForegroundColor Yellow
}

Remove-Item $WorkDir -Recurse -Force

Write-Step "Done"
$exeSize = '{0:N1} MB' -f ((Get-Item $FinalExe).Length / 1MB)
Write-Host "Package: $PackageDir"
Write-Host "Installer: $(Split-Path -Leaf $FinalExe) ($exeSize)"
if ($SigningMode -eq 'Unsigned') {
    Write-Host "UNSIGNED DEV BUILD - do not distribute." -ForegroundColor Yellow
}
Write-Host ""
Write-Host "Unattended usage on the target machine:"
Write-Host "  1. Copy the ConduitInstaller folder to the target."
Write-Host "  2. Rename conduit.provision.sample.json -> conduit.provision.json and fill it in."
Write-Host "  3. Run elevated: .\$(Split-Path -Leaf $FinalExe) --silent   (exit code = result; log in %TEMP%\ConduitInstall-*.log)"
