# Builds the unattended Conduit installer package.
#
# Output layout (side-by-side payload - SQL Express is NOT embedded in the exe;
# it must land on disk to run anyway and would quadruple the exe size):
#
#   <OutputPath>\ConduitInstaller\
#       ConduitSetup.exe                  InstallerRuntime with the Conduit app zip embedded
#       conduit.provision.sample.json     sidecar template (rename to conduit.provision.json)
#       redist\                           drop SQLEXPR_x64_ENU.exe here (see redist\README.txt)
#
# The embed uses the existing InstallerPackager format:
#   [EXE][MARKER "APPDATA\0"][ZIP][SIZE:4][MARKER]

param(
    [string]$ConduitRepo = (Join-Path (Split-Path -Parent $PSScriptRoot) "Conduit"),
    [string]$Configuration = "Release",
    [string]$OutputPath = ".\publish",
    [string]$SqlExpressSetup  # optional: path to SQLEXPR_x64_ENU.exe to copy into redist\
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Write-Step { param($m) Write-Host "`n=== $m ===" -ForegroundColor Cyan }

$ConduitWebProject = Join-Path $ConduitRepo "src\Conduit.Web\Conduit.Web.csproj"
if (-not (Test-Path $ConduitWebProject)) {
    Write-Host "ERROR: Conduit.Web project not found at $ConduitWebProject (pass -ConduitRepo)." -ForegroundColor Red
    exit 1
}

$OutputPath = [System.IO.Path]::GetFullPath((Join-Path $ScriptDir $OutputPath))
$PackageDir = Join-Path $OutputPath "ConduitInstaller"
$WorkDir    = Join-Path $OutputPath "conduit-work"

foreach ($dir in @($PackageDir, $WorkDir)) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

Write-Step "Publishing Conduit.Web (framework-dependent folder publish)"
$ConduitPublishDir = Join-Path $WorkDir "conduit-app"
dotnet publish $ConduitWebProject -c $Configuration -o $ConduitPublishDir --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: Conduit publish failed." -ForegroundColor Red; exit 1 }

# The payload must never ship an environment settings file: Conduit's SetupService
# owns appsettings.Production.json and the installer stamps the BASE file only.
$prodSettings = Join-Path $ConduitPublishDir "appsettings.Production.json"
if (Test-Path $prodSettings) { Remove-Item $prodSettings -Force }
$devSettings = Join-Path $ConduitPublishDir "appsettings.Development.json"
if (Test-Path $devSettings) { Remove-Item $devSettings -Force }

Write-Step "Publishing InstallerRuntime (self-contained single file)"
$RuntimePublishDir = Join-Path $WorkDir "runtime"
dotnet publish (Join-Path $ScriptDir "InstallerRuntime\InstallerRuntime.csproj") `
    -c $Configuration -o $RuntimePublishDir `
    /p:PublishSingleFile=true /p:SelfContained=true /p:RuntimeIdentifier=win-x64 `
    /p:IncludeNativeLibrariesForSelfExtract=true --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: InstallerRuntime publish failed." -ForegroundColor Red; exit 1 }

Write-Step "Zipping Conduit payload"
$PayloadZip = Join-Path $WorkDir "AppPayload.zip"
Compress-Archive -Path (Join-Path $ConduitPublishDir "*") -DestinationPath $PayloadZip -CompressionLevel Optimal

Write-Step "Embedding payload into installer exe"
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

Write-Step "Laying out package"
Copy-Item (Join-Path $ScriptDir "docs\conduit.provision.sample.json") (Join-Path $PackageDir "conduit.provision.sample.json")

$RedistDir = New-Item -ItemType Directory -Path (Join-Path $PackageDir "redist") -Force
@"
Place the SQL Server Express setup executable here as SQLEXPR_x64_ENU.exe.
Download: https://www.microsoft.com/en-us/download/details.aspx?id=104781 (SQL Server 2022 Express)
Use the full extractable package (SQLEXPR_x64_ENU.exe, ~260 MB), not the small web-installer stub -
target machines may not be able to download during install.

The installer only runs this when NO usable local SQL Server instance exists;
it never installs over an existing instance.
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
$exeSize = '{0:N1} MB' -f ((Get-Item $InstallerExe).Length / 1MB)
Write-Host "Package: $PackageDir"
Write-Host "Installer: ConduitSetup.exe ($exeSize)"
Write-Host ""
Write-Host "Unattended usage on the target machine:"
Write-Host "  1. Copy the ConduitInstaller folder to the target."
Write-Host "  2. Rename conduit.provision.sample.json -> conduit.provision.json and fill it in."
Write-Host "  3. Run elevated: .\ConduitSetup.exe --silent   (exit code = result; log in %TEMP%\ConduitInstall-*.log)"
