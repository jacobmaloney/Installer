# Identity Center Installer Build Script
# This script builds, packages, and prepares the installer for distribution

param(
    [string]$Configuration = "Release",
    [string]$OutputPath = ".\publish",
    [switch]$CleanBuild,
    [switch]$SkipTests,
    [string]$VersionOverride
)

$ErrorActionPreference = "Stop"

# Colors for output
function Write-Step { param($message) Write-Host "`n=== $message ===" -ForegroundColor Cyan }
function Write-Success { param($message) Write-Host $message -ForegroundColor Green }
function Write-Warning { param($message) Write-Host "WARNING: $message" -ForegroundColor Yellow }
function Write-Error { param($message) Write-Host "ERROR: $message" -ForegroundColor Red }

# Get script directory
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SolutionDir = $ScriptDir
$InstallerCoreProject = Join-Path $SolutionDir "Installer.Core\Installer.Core.csproj"
$InstallerBuilderProject = Join-Path $SolutionDir "InstallerBuilder\InstallerBuilder.csproj"
$InstallerRuntimeProject = Join-Path $SolutionDir "InstallerRuntime\InstallerRuntime.csproj"

Write-Host @"

 _____ _____    _____           _        _ _
|_   _|  ___|  |_   _|         | |      | | |
  | | | |        | | _ __  ___| |_ __ _| | | ___ _ __
  | | | |        | || '_ \/ __| __/ _` | | |/ _ \ '__|
 _| |_| |____   _| || | | \__ \ || (_| | | |  __/ |
|_____|\_____/  |___/_| |_|___/\__\__,_|_|_|\___|_|

Identity Center Installer Build System
"@ -ForegroundColor Magenta

Write-Step "Validating environment"

# Check for .NET SDK
$dotnetVersion = dotnet --version
if (-not $dotnetVersion) {
    Write-Error "dotnet SDK not found. Please install .NET 8.0 SDK."
    exit 1
}
Write-Success ".NET SDK version: $dotnetVersion"

# Get version from Directory.Build.props or use override
if ($VersionOverride) {
    $Version = $VersionOverride
} else {
    $BuildPropsPath = Join-Path $SolutionDir "Directory.Build.props"
    if (Test-Path $BuildPropsPath) {
        [xml]$buildProps = Get-Content $BuildPropsPath
        $major = $buildProps.Project.PropertyGroup.VersionMajor
        $minor = $buildProps.Project.PropertyGroup.VersionMinor
        $patch = $buildProps.Project.PropertyGroup.VersionPatch
        $build = [DateTime]::Now.ToString("yy") + [DateTime]::Now.DayOfYear.ToString("000")
        $Version = "$major.$minor.$patch.$build"
    } else {
        $build = [DateTime]::Now.ToString("yy") + [DateTime]::Now.DayOfYear.ToString("000")
        $Version = "1.0.0.$build"
    }
}
Write-Success "Building version: $Version"

# Clean if requested
if ($CleanBuild) {
    Write-Step "Cleaning previous builds"

    if (Test-Path $OutputPath) {
        Remove-Item -Path $OutputPath -Recurse -Force
        Write-Success "Cleaned output directory"
    }

    # Clean all bin/obj folders
    Get-ChildItem -Path $SolutionDir -Include bin,obj -Recurse -Directory | Remove-Item -Recurse -Force
    Write-Success "Cleaned build artifacts"
}

# Create output directory
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null

Write-Step "Restoring NuGet packages"
dotnet restore $SolutionDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "Package restore failed"
    exit 1
}
Write-Success "Packages restored"

Write-Step "Building Installer.Core"
dotnet build $InstallerCoreProject -c $Configuration /p:Version=$Version
if ($LASTEXITCODE -ne 0) {
    Write-Error "Installer.Core build failed"
    exit 1
}
Write-Success "Installer.Core built successfully"

# Check if InstallerBuilder exists
if (Test-Path $InstallerBuilderProject) {
    Write-Step "Building InstallerBuilder"
    dotnet build $InstallerBuilderProject -c $Configuration /p:Version=$Version
    if ($LASTEXITCODE -ne 0) {
        Write-Error "InstallerBuilder build failed"
        exit 1
    }
    Write-Success "InstallerBuilder built successfully"
}

# Check if InstallerRuntime exists
if (Test-Path $InstallerRuntimeProject) {
    Write-Step "Publishing InstallerRuntime"

    $RuntimePublishPath = Join-Path $OutputPath "InstallerRuntime"

    dotnet publish $InstallerRuntimeProject `
        -c $Configuration `
        -o $RuntimePublishPath `
        /p:Version=$Version `
        /p:PublishSingleFile=true `
        /p:SelfContained=true `
        /p:RuntimeIdentifier=win-x64 `
        /p:IncludeNativeLibrariesForSelfExtract=true

    if ($LASTEXITCODE -ne 0) {
        Write-Error "InstallerRuntime publish failed"
        exit 1
    }
    Write-Success "InstallerRuntime published to: $RuntimePublishPath"
}

# Run tests if not skipped
if (-not $SkipTests) {
    $TestProjects = Get-ChildItem -Path $SolutionDir -Filter "*.Tests.csproj" -Recurse
    if ($TestProjects) {
        Write-Step "Running tests"
        foreach ($testProject in $TestProjects) {
            Write-Host "Running: $($testProject.Name)"
            dotnet test $testProject.FullName -c $Configuration --no-build
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "Some tests failed in $($testProject.Name)"
            }
        }
    }
}

# Create version manifest
Write-Step "Creating version manifest"
$ManifestPath = Join-Path $OutputPath "build-manifest.json"
$Manifest = @{
    version = $Version
    buildDate = [DateTime]::UtcNow.ToString("o")
    configuration = $Configuration
    dotnetVersion = $dotnetVersion
    machine = $env:COMPUTERNAME
    user = $env:USERNAME
} | ConvertTo-Json -Depth 3

$Manifest | Out-File -FilePath $ManifestPath -Encoding UTF8
Write-Success "Build manifest created: $ManifestPath"

# Summary
Write-Step "Build Complete!"
Write-Host @"

Build Summary
-------------
Version:        $Version
Configuration:  $Configuration
Output:         $OutputPath

Contents:
"@ -ForegroundColor White

Get-ChildItem -Path $OutputPath -Recurse -File |
    Select-Object @{N='File';E={$_.FullName.Replace($OutputPath + '\', '')}}, @{N='Size';E={'{0:N2} MB' -f ($_.Length / 1MB)}} |
    Format-Table -AutoSize

Write-Success "`nInstaller build completed successfully!"
