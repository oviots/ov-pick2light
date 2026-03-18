#Requires -Version 5.1
<#
.SYNOPSIS
    Build and package P2L service + web client for Windows (win-x64).

.DESCRIPTION
    1. dotnet publish P2L.CLI for win-x64 (Native AOT)
    2. Build p2l-client.exe via PyInstaller (requires Python 3.10+ in PATH)
    3. Stage package layout and create a ZIP archive in dist\

.PARAMETER RID
    Runtime identifier. Supported: win-x64, win-arm64. Default: win-x64

.PARAMETER Version
    Package version string. Default: read from global.json, fallback "1.0.0"

.EXAMPLE
    .\deploy\package.ps1
    .\deploy\package.ps1 -RID win-x64 -Version 1.2.0
#>
param(
    [string] $RID     = "win-x64",
    [string] $Version = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step([string]$msg)  { Write-Host "[INFO]  $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)    { Write-Host "[OK]    $msg" -ForegroundColor Green }
function Write-Warn([string]$msg)  { Write-Host "[WARN]  $msg" -ForegroundColor Yellow }
function Fail([string]$msg)        { Write-Host "[ERROR] $msg" -ForegroundColor Red; exit 1 }

$ScriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root       = Split-Path -Parent $ScriptDir
$Project    = Join-Path $Root "src\P2L.CLI\P2L.CLI.csproj"
$ClientDir  = Join-Path $Root "client"
$DeployDir  = $ScriptDir
$ArtifactsDir = Join-Path $Root "artifacts"
$DistDir    = Join-Path $Root "dist"

# Version detection
if (-not $Version) {
    $globalJson = Join-Path $Root "global.json"
    if (Test-Path $globalJson) {
        $gj = Get-Content $globalJson | ConvertFrom-Json
        $Version = $gj.sdk.version -replace '^(\d+\.\d+\.\d+).*','$1'
    }
    if (-not $Version) { $Version = "1.0.0" }
}

Write-Host ""
Write-Host "╔══════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  P2L Package Builder  v$Version" -ForegroundColor Cyan
Write-Host "║  Target: $RID" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# ── 1. Build .NET AOT binary ──────────────────────────────────────────────────
Write-Step "Building P2L (.NET AOT) for $RID ..."
$ArtifactOut = Join-Path $ArtifactsDir $RID
dotnet publish $Project `
    --configuration Release `
    --runtime $RID `
    --output $ArtifactOut `
    --self-contained true `
    -p:PublishAot=true `
    -p:InvariantGlobalization=true `
    -p:StripSymbols=true
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed" }

$P2LBin = Join-Path $ArtifactOut "p2l.exe"
if (-not (Test-Path $P2LBin)) { Fail "p2l.exe not found after publish: $P2LBin" }
$sz = (Get-Item $P2LBin).Length / 1MB
Write-Ok "P2L binary: $P2LBin ($([math]::Round($sz,1)) MB)"

# ── 2. Build PyInstaller client binary ───────────────────────────────────────
Write-Step "Building P2L client (PyInstaller) for $RID ..."

$PythonExe = (Get-Command python -ErrorAction SilentlyContinue)?.Source
if (-not $PythonExe) { $PythonExe = (Get-Command python3 -ErrorAction SilentlyContinue)?.Source }
if (-not $PythonExe) { Fail "Python 3.10+ not found in PATH" }
Write-Step "Using Python: $PythonExe"

$ClientArtifactDir = Join-Path $ArtifactsDir "client\$RID"
New-Item -ItemType Directory -Path $ClientArtifactDir -Force | Out-Null

$BuildVenv = Join-Path $Root ".build-venv-client-win"
Write-Step "Creating build venv: $BuildVenv"
& $PythonExe -m venv $BuildVenv
if ($LASTEXITCODE -ne 0) { Fail "venv creation failed" }

$VenvPip = Join-Path $BuildVenv "Scripts\pip.exe"
$VenvPyInstaller = Join-Path $BuildVenv "Scripts\pyinstaller.exe"

& $VenvPip install --quiet --upgrade pip
& $VenvPip install --quiet -r (Join-Path $ClientDir "requirements.txt")
& $VenvPip install --quiet pyinstaller

$WorkPath = Join-Path $Root ".pyinstaller-work-win"
Push-Location $ClientDir
try {
    & $VenvPyInstaller `
        --noconfirm --clean --onefile `
        --name p2l-client `
        --distpath $ClientArtifactDir `
        --workpath $WorkPath `
        --specpath $WorkPath `
        --add-data "$ClientDir\templates;templates" `
        --add-data "$ClientDir\static;static" `
        --hidden-import uvicorn.logging `
        --hidden-import uvicorn.loops `
        --hidden-import uvicorn.loops.auto `
        --hidden-import uvicorn.loops.asyncio `
        --hidden-import uvicorn.protocols.http.auto `
        --hidden-import uvicorn.protocols.http.h11_impl `
        --hidden-import uvicorn.protocols.websockets.auto `
        --hidden-import uvicorn.lifespan.on `
        --hidden-import uvicorn.lifespan.off `
        --hidden-import fastapi.staticfiles `
        --hidden-import fastapi.templating `
        --hidden-import pydantic.deprecated.decorator `
        --hidden-import aiomqtt `
        --hidden-import aiosqlite `
        --hidden-import aiofiles.os `
        --hidden-import aiofiles.threadpool `
        --hidden-import h11 `
        --hidden-import jinja2.ext `
        --hidden-import anyio._backends._asyncio `
        --hidden-import starlette.routing `
        --hidden-import starlette.staticfiles `
        --hidden-import starlette.templating `
        --exclude-module tkinter `
        --exclude-module unittest `
        run.py
    if ($LASTEXITCODE -ne 0) { Fail "PyInstaller failed" }
} finally {
    Pop-Location
}

$ClientBin = Join-Path $ClientArtifactDir "p2l-client.exe"
if (-not (Test-Path $ClientBin)) { Fail "p2l-client.exe not found: $ClientBin" }
$csz = (Get-Item $ClientBin).Length / 1MB
Write-Ok "Client binary: $ClientBin ($([math]::Round($csz,1)) MB)"

# Cleanup build artifacts
Remove-Item -Recurse -Force $BuildVenv  -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $WorkPath   -ErrorAction SilentlyContinue

# ── 3. Stage package ──────────────────────────────────────────────────────────
$PkgName  = "p2l-$Version-$RID"
$Stage    = Join-Path $DistDir "staging\$PkgName"
Write-Step "Staging package: $PkgName ..."
Remove-Item -Recurse -Force $Stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$Stage\bin"    -Force | Out-Null
New-Item -ItemType Directory -Path "$Stage\config" -Force | Out-Null
New-Item -ItemType Directory -Path "$Stage\deploy" -Force | Out-Null

# Binaries
Copy-Item $P2LBin    (Join-Path $Stage "bin\p2l.exe")        -Force
Copy-Item $ClientBin (Join-Path $Stage "bin\p2l-client.exe") -Force

# SQLite native lib
$SqliteDll = Join-Path $ArtifactOut "e_sqlite3.dll"
if (Test-Path $SqliteDll) {
    Copy-Item $SqliteDll (Join-Path $Stage "bin\e_sqlite3.dll") -Force
    Write-Ok "Bundled e_sqlite3.dll"
} else {
    Write-Warn "e_sqlite3.dll not found — p2l may fail at runtime"
}

# appsettings.json template
$SettingsSrc = Join-Path $DeployDir "appsettings.template.json"
if (-not (Test-Path $SettingsSrc)) { $SettingsSrc = Join-Path $Root "appsettings.json" }
if (Test-Path $SettingsSrc) {
    Copy-Item $SettingsSrc (Join-Path $Stage "config\appsettings.json") -Force
    Write-Ok "Copied appsettings.json"
}

# Deploy scripts
Copy-Item (Join-Path $DeployDir "install-windows.ps1") (Join-Path $Stage "deploy\install.ps1") -Force

# README
@"
Pick2Light (P2L) — $PkgName
=====================================

CONTENTS
  bin\p2l.exe             P2L .NET bridge (Aioi gateway <-> MQTT)
  bin\p2l-client.exe      P2L web dashboard — standalone (no Python required)
  bin\e_sqlite3.dll       SQLite native library (required by p2l.exe)
  config\appsettings.json P2L bridge configuration — edit before first run
  deploy\install.ps1      Windows installer script

INSTALL (run PowerShell as Administrator):
  .\deploy\install.ps1
  .\deploy\install.ps1 -StartNow

CONFIGURATION
  Edit config\appsettings.json to set:
    Gateway.Host / Port    — Aioi hardware IP
    Mqtt.Host / Username / Password — MQTT broker
  Or set environment variables:
    P2L_GATEWAY__HOST, P2L_MQTT__HOST, P2L_MQTT__USERNAME, P2L_MQTT__PASSWORD

DASHBOARD
  http://localhost:8765  (after services are running)

LOGS
  C:\ProgramData\P2L\logs\
  Get-EventLog -LogName Application -Source P2L -Newest 50
"@ | Set-Content (Join-Path $Stage "README.txt") -Encoding UTF8

# ── 4. Create ZIP archive ─────────────────────────────────────────────────────
New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
$ZipPath = Join-Path $DistDir "$PkgName.zip"
Write-Step "Creating archive: $ZipPath ..."

$StagingDir = Split-Path -Parent $Stage
Compress-Archive -Path $Stage -DestinationPath $ZipPath -Force
$zsz = (Get-Item $ZipPath).Length / 1MB
Write-Ok "Archive: $ZipPath ($([math]::Round($zsz,1)) MB)"

# Cleanup staging
Remove-Item -Recurse -Force (Split-Path -Parent $Stage) -ErrorAction SilentlyContinue

Write-Host ""
Write-Ok "Package ready: $ZipPath"
Get-ChildItem $DistDir -Filter "*.zip" | Format-Table Name, @{N='Size';E={[math]::Round($_.Length/1MB,1).ToString()+" MB"}}
