# Pick2Light MSI Installer Builder
# Requires: WiX Toolset 4.0+
# Usage: .\build-msi.ps1 -Platform x64 -OutputPath C:\Releases

param(
    [ValidateSet('x64', 'arm64')]
    [string]$Platform = 'x64',
    
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    
    [string]$OutputPath = '.\releases',
    
    [switch]$SkipBuild,
    
    [switch]$SignMSI
)

$ErrorActionPreference = 'Stop'

# Configuration
$ProjectRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$InstallerRoot = $PSScriptRoot
$ArtifactDir = Join-Path $ProjectRoot "artifacts" "win-$Platform"
$BinaryPath = Join-Path $ArtifactDir "p2l.exe"

# WiX variables
$WiXProject = Join-Path $InstallerRoot "P2L.Installer.wixproj"
$OutputDir = New-Item -ItemType Directory -Path $OutputPath -Force

Write-Host ""
Write-Host "╔════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  Pick2Light MSI Installer Builder                         ║" -ForegroundColor Cyan
Write-Host "╚════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# Validation
Write-Host "Validating prerequisites..." -ForegroundColor Yellow

# Check for WiX Toolset
$heatExe = Get-Command heat.exe -ErrorAction SilentlyContinue
if (-not $heatExe) {
    Write-Host "⚠️  WiX Toolset 4.0+ not found in PATH" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Install options:" -ForegroundColor Cyan
    Write-Host "  1. Download: https://wixtoolset.org/releases/v4.0.x/wix4-latest.exe" -ForegroundColor Gray
    Write-Host "  2. Or via winget: winget install WiXToolset" -ForegroundColor Gray
    Write-Host ""
    Write-Host "After installation, run this script again." -ForegroundColor Cyan
    exit 1
}

Write-Host "✓ WiX Toolset found" -ForegroundColor Green

# Check for binaries
if (-not (Test-Path $BinaryPath)) {
    Write-Host "⚠️  Binary not found: $BinaryPath" -ForegroundColor Yellow
    Write-Host ""
    if (-not $SkipBuild) {
        Write-Host "Building P2L for $Platform..." -ForegroundColor Cyan
        & "$ProjectRoot\deploy\publish.ps1" -RID "win-$Platform"
        if ($LASTEXITCODE -ne 0) {
            Write-Host "✗ Build failed" -ForegroundColor Red
            exit 1
        }
    } else {
        Write-Host "Skipping build. Use -SkipBuild:$false to build first." -ForegroundColor Red
        exit 1
    }
}

Write-Host "✓ Binary found: $BinaryPath" -ForegroundColor Green
Write-Host ""

# Generate WiX source files
Write-Host "Generating WiX source files..." -ForegroundColor Yellow

$heatArgs = @(
    "dir"
    $ArtifactDir
    "-o", (Join-Path $InstallerRoot "GeneratedFileList.wxs")
    "-var", "var.SourceDir"
    "-gg"
    "-srd"
    "-sfrag"
)

& heat.exe @heatArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "✗ Failed to harvest binary directory" -ForegroundColor Red
    exit 1
}

Write-Host "✓ Generated file list" -ForegroundColor Green

# Build MSI
Write-Host ""
Write-Host "Building MSI package..." -ForegroundColor Yellow

$msBuildArgs = @(
    $WiXProject
    "/p:Configuration=$Configuration"
    "/p:Platform=$Platform"
    "/p:SourceDir=$ArtifactDir"
    "/p:OutputPath=$OutputDir"
)

if ($Configuration -eq 'Release') {
    $msBuildArgs += "/p:TreatWarningsAsErrors=true"
}

& msbuild.exe @msBuildArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "✗ MSI build failed" -ForegroundColor Red
    exit 1
}

Write-Host "✓ MSI package created" -ForegroundColor Green

# Locate MSI output
$msiFile = Get-ChildItem $OutputDir -Filter "*.msi" -ErrorAction SilentlyContinue | Select-Object -First 1

if ($msiFile) {
    Write-Host ""
    Write-Host "╔════════════════════════════════════════════════════════════╗" -ForegroundColor Green
    Write-Host "║  ✓ Build Successful                                       ║" -ForegroundColor Green
    Write-Host "╚════════════════════════════════════════════════════════════╝" -ForegroundColor Green
    Write-Host ""
    Write-Host "MSI Package:" -ForegroundColor Cyan
    Write-Host "  Path: $($msiFile.FullName)" -ForegroundColor Gray
    Write-Host "  Size: $($msiFile.Length / 1MB | FormatNumber -Digits 1) MB" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Installation:" -ForegroundColor Cyan
    Write-Host "  msiexec /i ""$($msiFile.FullName)"" /qn" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Uninstallation:" -ForegroundColor Cyan
    Write-Host "  msiexec /x ""$($msiFile.FullName)"" /qn" -ForegroundColor Gray
    Write-Host ""

    if ($SignMSI) {
        Write-Host "Signing MSI..." -ForegroundColor Yellow
        # Note: This requires a valid code signing certificate
        # Uncomment and configure as needed:
        # signtool sign /f "path/to/cert.pfx" /p "password" /fd sha256 /tr http://timestamp.server "$($msiFile.FullName)"
        Write-Host "⚠️  MSI signing not configured. Add your certificate path to the script." -ForegroundColor Yellow
    }
} else {
    Write-Host "✗ MSI file not found in output directory" -ForegroundColor Red
    exit 1
}

Write-Host ""
