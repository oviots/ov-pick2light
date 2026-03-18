#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Install P2L bridge service + web client as Windows Services.
    Uses pre-built binaries — no Python runtime required.

.DESCRIPTION
    Copies p2l.exe + p2l-client.exe to C:\Program Files\P2L and registers
    both as Windows Services via sc.exe.

.PARAMETER PackageRoot
    Path to the extracted package root (contains bin\, config\, deploy\).
    Default: directory above this script.

.PARAMETER CsvTaskFile
    Path to the picking task CSV.
    Default: C:\Program Files\P2L\tasks.csv

.PARAMETER ServiceName
    Windows Service name for P2L bridge. Default: P2L

.PARAMETER ClientPort
    Port for the web dashboard. Default: 8765

.PARAMETER StartNow
    If specified, start both services immediately after installation.

.EXAMPLE
    .\deploy\install-windows.ps1
    .\deploy\install-windows.ps1 -StartNow
    .\deploy\install-windows.ps1 -PackageRoot "D:\p2l-1.0.0-win-x64"
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $PackageRoot    = "",
    [string] $CsvTaskFile    = "C:\Program Files\P2L\tasks.csv",
    [string] $ServiceName    = "P2L",
    [string] $ClientPort     = "8765",
    [switch] $StartNow
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step([string]$msg)  { Write-Host "[INFO]  $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)    { Write-Host "[OK]    $msg" -ForegroundColor Green }
function Write-Warn([string]$msg)  { Write-Host "[WARN]  $msg" -ForegroundColor Yellow }
function Fail([string]$msg)        { Write-Host "[ERROR] $msg" -ForegroundColor Red; exit 1 }

# Resolve package root
if (-not $PackageRoot) {
    $PackageRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
}
$PackageRoot = (Resolve-Path $PackageRoot).Path

# ── Pre-flight ────────────────────────────────────────────────────────────────
$P2LBin    = Join-Path $PackageRoot "bin\p2l.exe"
$ClientBin = Join-Path $PackageRoot "bin\p2l-client.exe"

if (-not (Test-Path $P2LBin))    { Fail "Not found: $P2LBin" }
if (-not (Test-Path $ClientBin)) { Fail "Not found: $ClientBin" }

$InstallDir = "C:\Program Files\P2L"
$LogDir     = "C:\ProgramData\P2L\logs"
$ClientServiceName = "${ServiceName}Client"

Write-Step "Package root : $PackageRoot"
Write-Step "Install dir  : $InstallDir"
Write-Host ""

# ── 1. Create install directories ─────────────────────────────────────────────
Write-Step "Creating directories ..."
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
New-Item -ItemType Directory -Path $LogDir     -Force | Out-Null
Write-Ok "Directories ready"

# ── 2. Install binaries ───────────────────────────────────────────────────────
Write-Step "Installing binaries ..."
Copy-Item $P2LBin    (Join-Path $InstallDir "p2l.exe")        -Force
Copy-Item $ClientBin (Join-Path $InstallDir "p2l-client.exe") -Force
Write-Ok "Installed p2l.exe and p2l-client.exe"

# SQLite native library (e_sqlite3.dll) — required by p2l at runtime
$SqliteDll = Join-Path $PackageRoot "bin\e_sqlite3.dll"
if (Test-Path $SqliteDll) {
    Copy-Item $SqliteDll (Join-Path $InstallDir "e_sqlite3.dll") -Force
    Write-Ok "Installed SQLite native lib: e_sqlite3.dll"
} else {
    Write-Warn "e_sqlite3.dll not found in package — p2l may fail at runtime"
}

# appsettings.json
$AppSettings = Join-Path $PackageRoot "config\appsettings.json"
$AppSettingsDest = Join-Path $InstallDir "appsettings.json"
if (Test-Path $AppSettings) {
    if (Test-Path $AppSettingsDest) {
        Write-Warn "appsettings.json already exists — keeping existing config"
    } else {
        Copy-Item $AppSettings $AppSettingsDest -Force
        Write-Ok "Installed appsettings.json  <- edit before starting the service"
    }
}

# ── 3. Register P2L bridge Windows Service ────────────────────────────────────
Write-Step "Registering P2L bridge service ..."
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Warn "Service '$ServiceName' already exists — removing for clean install"
    if ($existing.Status -eq "Running") { Stop-Service -Name $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

$p2lExe = Join-Path $InstallDir "p2l.exe"
$binPath = "`"$p2lExe`" run `"$CsvTaskFile`""
$result = sc.exe create $ServiceName `
    binPath= $binPath `
    DisplayName= "Pick2Light Warehouse Daemon" `
    start= auto
if ($LASTEXITCODE -ne 0) { Fail "sc.exe create failed: $result" }
sc.exe description $ServiceName "Pick2Light warehouse light-module controller (Aioi Systems)" | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
Write-Ok "P2L bridge service registered: $ServiceName"

# ── 4. Register P2L client Windows Service ────────────────────────────────────
Write-Step "Registering P2L web client service ..."
$existingClient = Get-Service -Name $ClientServiceName -ErrorAction SilentlyContinue
if ($existingClient) {
    Write-Warn "Service '$ClientServiceName' already exists — removing for clean install"
    if ($existingClient.Status -eq "Running") { Stop-Service -Name $ClientServiceName -Force }
    sc.exe delete $ClientServiceName | Out-Null
    Start-Sleep -Seconds 2
}

$clientExe = Join-Path $InstallDir "p2l-client.exe"
$clientBinPath = "`"$clientExe`""
$result = sc.exe create $ClientServiceName `
    binPath= $clientBinPath `
    DisplayName= "Pick2Light Web Dashboard" `
    start= auto
if ($LASTEXITCODE -ne 0) { Fail "sc.exe create (client) failed: $result" }
sc.exe description $ClientServiceName "Pick2Light web dashboard on port $ClientPort" | Out-Null
sc.exe failure $ClientServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

# Set environment variables for the service
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ClientServiceName\Parameters"
New-Item -Path $regPath -Force | Out-Null
Set-ItemProperty -Path $regPath -Name "AppParameters" -Value "" | Out-Null

# Use sc.exe to set environment
$envMultiSz = [string[]]@("P2L_CLIENT_HOST=0.0.0.0", "P2L_CLIENT_PORT=$ClientPort")
$regEnvPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ClientServiceName"
New-ItemProperty -Path $regEnvPath -Name "Environment" -Value $envMultiSz -PropertyType MultiString -Force | Out-Null

Write-Ok "P2L client service registered: $ClientServiceName"

# ── 5. Configure ACLs ─────────────────────────────────────────────────────────
# Allow LocalService to read the install dir
$acl = Get-Acl $InstallDir
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "NT AUTHORITY\LocalService", "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.AddAccessRule($rule)
$dataRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "NT AUTHORITY\LocalService", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.AddAccessRule($dataRule)
Set-Acl $InstallDir $acl
Write-Ok "ACLs configured"

# ── 6. Optionally start now ────────────────────────────────────────────────────
if ($StartNow) {
    Write-Step "Starting P2L bridge ..."
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 2
    Write-Ok "P2L bridge: $((Get-Service -Name $ServiceName).Status)"

    Write-Step "Starting P2L web client ..."
    Start-Service -Name $ClientServiceName
    Start-Sleep -Seconds 2
    Write-Ok "P2L client: $((Get-Service -Name $ClientServiceName).Status)"
} else {
    Write-Warn "Services not started. Run when ready:"
    Write-Host "    Start-Service -Name $ServiceName" -ForegroundColor White
    Write-Host "    Start-Service -Name $ClientServiceName" -ForegroundColor White
}

Write-Host ""
Write-Ok "=== Installation complete ==="
Write-Host "  Dashboard     : http://localhost:$ClientPort"
Write-Host "  Config        : $InstallDir\appsettings.json"
Write-Host "  Logs          : $LogDir\"
Write-Host "  Stop bridge   : Stop-Service -Name $ServiceName"
Write-Host "  Stop client   : Stop-Service -Name $ClientServiceName"
Write-Host "  Uninstall     : sc.exe delete $ServiceName; sc.exe delete $ClientServiceName"
