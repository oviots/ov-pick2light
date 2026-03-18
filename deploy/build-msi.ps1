<#
.SYNOPSIS
  Build the P2L Windows MSI installer using WiX 4/5.

.DESCRIPTION
  Orchestrates:
    1. dotnet publish (Native AOT) for win-x64 / win-arm64
    2. PyInstaller build of p2l-client.exe (must run on Windows)
    3. dotnet build on P2L.Installer.wixproj → .msi

  Prerequisites (one-time setup):
    dotnet tool install --global wix
    wix extension add WixToolset.UI.wixext
    wix extension add WixToolset.Util.wixext
    pip install pyinstaller  (for client binary)

.PARAMETER Platform
  Target platform: x64 (default) or arm64

.PARAMETER Version
  Version string embedded in the MSI. Default: 1.0.0

.PARAMETER SkipDotnetPublish
  Skip dotnet publish (use pre-built binaries in artifacts/).

.PARAMETER SkipClientBuild
  Skip PyInstaller build (use pre-built p2l-client.exe in artifacts/).

.EXAMPLE
  .\deploy\build-msi.ps1
  .\deploy\build-msi.ps1 -Platform arm64 -Version 1.2.3
  .\deploy\build-msi.ps1 -SkipDotnetPublish -SkipClientBuild
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Platform = 'x64',

    [string]$Version = '1.0.0',

    [switch]$SkipDotnetPublish,
    [switch]$SkipClientBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot    = Split-Path $PSScriptRoot -Parent
$ArtifactDir = "$RepoRoot\artifacts\win-$Platform"
$InstallerDir = "$RepoRoot\deploy\P2L.Installer"
$DistDir      = "$RepoRoot\dist"

Write-Host "=== P2L MSI Build ===" -ForegroundColor Cyan
Write-Host "Platform : $Platform"
Write-Host "Version  : $Version"
Write-Host "Artifacts: $ArtifactDir"
Write-Host ""

# ── Prerequisites check ──────────────────────────────────────────────────
Write-Host "[0/4] Checking prerequisites..." -ForegroundColor Yellow

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK not found. Install from https://dot.net"
}
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Write-Host "  wix CLI not found. Installing..." -ForegroundColor Yellow
    dotnet tool install --global wix
    wix extension add WixToolset.UI.wixext
    wix extension add WixToolset.Util.wixext
}

# ── Step 1: dotnet publish (Native AOT) ─────────────────────────────────
if (-not $SkipDotnetPublish) {
    Write-Host "[1/4] Publishing .NET Native AOT for win-$Platform..." -ForegroundColor Yellow
    $rid = "win-$Platform"
    & dotnet publish "$RepoRoot\src\P2L.CLI" `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:PublishAot=true `
        -p:PublishSingleFile=false `
        -p:AssemblyVersion=$Version `
        -o "$ArtifactDir"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    Write-Host "  → p2l.exe published" -ForegroundColor Green
} else {
    Write-Host "[1/4] Skipping dotnet publish (--SkipDotnetPublish)" -ForegroundColor DarkGray
}

# Verify e_sqlite3.dll was produced
if (-not (Test-Path "$ArtifactDir\e_sqlite3.dll")) {
    throw "e_sqlite3.dll not found in $ArtifactDir`nCheck that Microsoft.Data.Sqlite is referenced in P2L.Mqtt.csproj"
}
Write-Host "  e_sqlite3.dll found ✓" -ForegroundColor Green

# ── Step 2: PyInstaller build for p2l-client.exe ────────────────────────
if (-not $SkipClientBuild) {
    Write-Host "[2/4] Building p2l-client.exe with PyInstaller..." -ForegroundColor Yellow
    if (-not (Get-Command pyinstaller -ErrorAction SilentlyContinue)) {
        throw "pyinstaller not found. Run: pip install pyinstaller"
    }
    Push-Location "$RepoRoot\client"
    & pyinstaller --onefile --name p2l-client --distpath "$ArtifactDir" main.py
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw "PyInstaller build failed" }
    Pop-Location
    Write-Host "  → p2l-client.exe built" -ForegroundColor Green
} else {
    Write-Host "[2/4] Skipping client build (--SkipClientBuild)" -ForegroundColor DarkGray
}

# ── Step 3: Verify all required files ────────────────────────────────────
Write-Host "[3/4] Verifying artifacts..." -ForegroundColor Yellow
$required = @('p2l.exe', 'p2l-client.exe', 'e_sqlite3.dll')
foreach ($f in $required) {
    if (-not (Test-Path "$ArtifactDir\$f")) {
        throw "Required file missing: $ArtifactDir\$f"
    }
    $size = (Get-Item "$ArtifactDir\$f").Length / 1MB
    Write-Host ("  {0,-24} {1:F1} MB  ✓" -f $f, $size) -ForegroundColor Green
}

# ── Step 4: Build MSI ────────────────────────────────────────────────────
Write-Host "[4/4] Building MSI..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path $DistDir | Out-Null

& dotnet build "$InstallerDir\P2L.Installer.wixproj" `
    -c Release `
    -p:Platform=$Platform `
    -p:ProductVersion=$Version `
    -p:BinaryPath=$ArtifactDir `
    -o "$DistDir"
if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }

$msi = Get-ChildItem "$DistDir\*.msi" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msi) { throw "No .msi file found in $DistDir" }

Write-Host ""
Write-Host "═══════════════════════════════════════" -ForegroundColor Cyan
Write-Host " MSI ready: $($msi.FullName)" -ForegroundColor Green
Write-Host " Size     : $([math]::Round($msi.Length/1MB, 1)) MB" -ForegroundColor Green
Write-Host "═══════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
Write-Host "Install (interactive):  msiexec /i `"$($msi.Name)`""
Write-Host "Install (silent):       msiexec /i `"$($msi.Name)`" /qn"
Write-Host "Uninstall:              msiexec /x `"$($msi.Name)`" /qn"
