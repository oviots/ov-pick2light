#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Install P2L bridge service + Python web client as Windows Services.

.DESCRIPTION
    Copies the p2l.exe binary to C:\Program Files\P2L and registers it as a
    Windows Service. Also installs the Python web dashboard to
    C:\Program Files\P2L\client, creates a venv, and registers it as a
    second Windows Service via NSSM (if available) or a Python wrapper.

    P2L detects it is running under the Service Control Manager via
    Environment.UserInteractive == false and hands off to ServiceBase.Run().

.PARAMETER BinaryPath
    Path to the compiled p2l.exe (win-x64 AOT artifact).
    Default: .\artifacts\win-x64\p2l.exe

.PARAMETER CsvTaskFile
    Absolute path to the picking task CSV that the daemon should process.
    Default: C:\Program Files\P2L\tasks.csv

.PARAMETER ClientSourcePath
    Path to the Python web client source directory.
    Default: .\client

.PARAMETER ServiceName
    Windows Service name for P2L bridge. Default: P2L

.PARAMETER ServiceDisplayName
    Display name shown in services.msc. Default: Pick2Light Warehouse Daemon

.PARAMETER StartNow
    If specified, start both services immediately after installation.

.EXAMPLE
    # Basic install with defaults
    .\deploy\install-windows.ps1

.EXAMPLE
    # Custom paths, start immediately
    .\deploy\install-windows.ps1 `
        -BinaryPath .\artifacts\win-x64\p2l.exe `
        -CsvTaskFile "D:\Warehouse\tasks.csv" `
        -StartNow
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $BinaryPath         = ".\artifacts\win-x64\p2l.exe",
    [string] $CsvTaskFile        = "C:\Program Files\P2L\tasks.csv",
    [string] $ClientSourcePath   = ".\client",
    [string] $ServiceName        = "P2L",
    [string] $ServiceDisplayName = "Pick2Light Warehouse Daemon",
    [switch] $StartNow
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Helpers ──────────────────────────────────────────────────────────────────
function Write-Step([string]$msg)  { Write-Host "[INFO]  $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)    { Write-Host "[OK]    $msg" -ForegroundColor Green }
function Write-Warn([string]$msg)  { Write-Host "[WARN]  $msg" -ForegroundColor Yellow }
function Fail([string]$msg)        { Write-Host "[ERROR] $msg" -ForegroundColor Red; exit 1 }

# ── Pre-flight ────────────────────────────────────────────────────────────────
$BinaryPath = Resolve-Path $BinaryPath -ErrorAction SilentlyContinue
if (-not $BinaryPath -or -not (Test-Path $BinaryPath)) {
    Fail "Binary not found: $BinaryPath`n  Build first with: .\deploy\publish.ps1 -RID win-x64"
}
if (-not (Test-Path $ClientSourcePath)) {
    Fail "Client source not found: $ClientSourcePath"
}

# Check Python availability
$PythonExe = (Get-Command python -ErrorAction SilentlyContinue)?.Source
if (-not $PythonExe) { $PythonExe = (Get-Command python3 -ErrorAction SilentlyContinue)?.Source }
if (-not $PythonExe) { Fail "Python not found. Install Python 3.10+ from https://python.org" }
Write-Ok "Python found: $PythonExe"

Write-Step "Installing P2L from      : $BinaryPath"
Write-Step "CSV task file            : $CsvTaskFile"
Write-Step "Python client source     : $ClientSourcePath"
Write-Step "Service name             : $ServiceName"

# ── 1. Copy P2L binary + config ──────────────────────────────────────────────
$InstallDir   = "C:\Program Files\P2L"
$ClientDir    = "$InstallDir\client"
$LogDir       = "$InstallDir\logs"
$Dest         = "$InstallDir\p2l.exe"

Write-Step "Creating install directory: $InstallDir"
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
New-Item -ItemType Directory -Path $ClientDir  -Force | Out-Null
New-Item -ItemType Directory -Path $LogDir     -Force | Out-Null

Write-Step "Copying P2L binary..."
Copy-Item -Path $BinaryPath -Destination $Dest -Force

$AppSettingsSrc = Join-Path (Split-Path $BinaryPath) "appsettings.json"
if (-not (Test-Path $AppSettingsSrc)) {
    $AppSettingsSrc = Join-Path (Split-Path $PSScriptRoot) "appsettings.json"
}
if (Test-Path $AppSettingsSrc) {
    Copy-Item -Path $AppSettingsSrc -Destination "$InstallDir\appsettings.json" -Force
    Write-Ok "Copied appsettings.json"
} else {
    Write-Warn "appsettings.json not found alongside binary — using compiled-in defaults."
}

# ── 2. Install Python web client ──────────────────────────────────────────────
Write-Step "Copying Python web client to $ClientDir ..."
# Robocopy: /E = include subdirs, /PURGE = remove extra, /XD = exclude dirs, /NFL /NDL = quiet
robocopy $ClientSourcePath $ClientDir /E /PURGE /XD "__pycache__" "venv" ".git" /XF "*.pyc" "*.db-shm" "*.db-wal" /NFL /NDL /NJH /NJS | Out-Null

Write-Step "Creating Python virtual environment ..."
& $PythonExe -m venv "$ClientDir\venv"
& "$ClientDir\venv\Scripts\pip.exe" install --quiet --upgrade pip
& "$ClientDir\venv\Scripts\pip.exe" install --quiet -r "$ClientDir\requirements.txt"
Write-Ok "Python client installed (venv ready)"

# ── 3. Secure directory ACL ───────────────────────────────────────────────────
Write-Step "Locking down directory permissions..."
$Acl = Get-Acl $InstallDir
$Acl.SetAccessRuleProtection($true, $false)
$SystemRule  = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "NT AUTHORITY\SYSTEM",    "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$AdminRule   = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "BUILTIN\Administrators", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$ServiceRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "NT SERVICE\$ServiceName", "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")
$LogRule     = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "NT SERVICE\$ServiceName", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
$Acl.AddAccessRule($SystemRule)
$Acl.AddAccessRule($AdminRule)
$Acl.AddAccessRule($ServiceRule)
Set-Acl $InstallDir $Acl

$LogAcl = Get-Acl $LogDir
$LogAcl.AddAccessRule($LogRule)
Set-Acl $LogDir $LogAcl

# ── 4. Register P2L bridge Windows Service ───────────────────────────────────
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Warn "Service '$ServiceName' already exists — removing for clean install."
    if ($existing.Status -eq "Running") { Stop-Service -Name $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

$BinPathArg = "`"$Dest`" run `"$CsvTaskFile`""
Write-Step "Registering P2L bridge Windows Service..."
$result = sc.exe create $ServiceName `
    binPath= $BinPathArg `
    DisplayName= $ServiceDisplayName `
    start= auto `
    obj= "NT AUTHORITY\LocalService"

if ($LASTEXITCODE -ne 0) { Fail "sc.exe create failed: $result" }
sc.exe description $ServiceName "Pick2Light warehouse light-module controller (Aioi Systems)" | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
Write-Ok "P2L bridge service registered."

# ── 5. Register Python web client Windows Service ────────────────────────────
$ClientServiceName = "${ServiceName}Client"
$ClientSvcExisting = Get-Service -Name $ClientServiceName -ErrorAction SilentlyContinue
if ($ClientSvcExisting) {
    Write-Warn "Service '$ClientServiceName' already exists — removing for clean install."
    if ($ClientSvcExisting.Status -eq "Running") { Stop-Service -Name $ClientServiceName -Force }
    sc.exe delete $ClientServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# Write a wrapper script that Windows SCM can launch (pythonservice.exe alternative)
$WrapperScript = "$ClientDir\start-service.ps1"
@"
`$env:P2L_CLIENT_HOST = '0.0.0.0'
`$env:P2L_CLIENT_PORT = '8765'
Set-Location '$ClientDir'
& '$ClientDir\venv\Scripts\python.exe' '$ClientDir\run.py'
"@ | Set-Content -Path $WrapperScript -Encoding UTF8

$ClientBinPath = "`"$Env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe`" -NonInteractive -ExecutionPolicy Bypass -File `"$WrapperScript`""
Write-Step "Registering P2L web client Windows Service..."
$result = sc.exe create $ClientServiceName `
    binPath= $ClientBinPath `
    DisplayName= "Pick2Light Web Dashboard" `
    start= auto `
    obj= "NT AUTHORITY\LocalService"

if ($LASTEXITCODE -ne 0) { Fail "sc.exe create (client) failed: $result" }
sc.exe description $ClientServiceName "Pick2Light web dashboard (FastAPI) on port 8765" | Out-Null
sc.exe failure $ClientServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
Write-Ok "P2L web client service registered."

# ── 6. Optionally start now ───────────────────────────────────────────────────
if ($StartNow) {
    Write-Step "Starting P2L bridge service..."
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 2
    $svc = Get-Service -Name $ServiceName
    Write-Ok "P2L bridge status: $($svc.Status)"

    Write-Step "Starting P2L web client service..."
    Start-Service -Name $ClientServiceName
    Start-Sleep -Seconds 2
    $csvc = Get-Service -Name $ClientServiceName
    Write-Ok "P2L client status: $($csvc.Status)"
} else {
    Write-Warn "Services not started. Run when ready:"
    Write-Host "    Start-Service -Name $ServiceName" -ForegroundColor White
    Write-Host "    Start-Service -Name $ClientServiceName" -ForegroundColor White
}

Write-Host ""
Write-Ok "=== Installation complete ==="
Write-Host "  Dashboard         : http://localhost:8765"
Write-Host "  Logs (bridge)     : $LogDir\*.log"
Write-Host "  Logs (client)     : Get-EventLog -LogName Application -Source $ClientServiceName -Newest 50"
Write-Host "  Stop bridge       : Stop-Service -Name $ServiceName"
Write-Host "  Stop client       : Stop-Service -Name $ClientServiceName"
Write-Host "  Status bridge     : Get-Service -Name $ServiceName"
Write-Host "  Status client     : Get-Service -Name $ClientServiceName"
