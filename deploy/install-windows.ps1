#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Install P2L as a Windows Service using the native AOT binary.

.DESCRIPTION
    Copies the p2l.exe binary to C:\ProgramFiles\P2L, registers it as a
    Windows Service via sc.exe, and optionally starts it immediately.

    P2L detects it is running under the Service Control Manager via
    Environment.UserInteractive == false and hands off to ServiceBase.Run().

.PARAMETER BinaryPath
    Path to the compiled p2l.exe (win-x64 AOT artifact).
    Default: .\artifacts\win-x64\p2l.exe

.PARAMETER CsvTaskFile
    Absolute path to the picking task CSV that the daemon should process.
    Default: C:\ProgramFiles\P2L\tasks.csv

.PARAMETER ServiceName
    Windows Service name. Default: P2L

.PARAMETER ServiceDisplayName
    Display name shown in services.msc. Default: Pick2Light Warehouse Daemon

.PARAMETER StartNow
    If specified, start the service immediately after installation.

.EXAMPLE
    # Basic install with defaults
    .\deploy\install-windows.ps1

.EXAMPLE
    # Custom binary + task file, start immediately
    .\deploy\install-windows.ps1 `
        -BinaryPath .\artifacts\win-x64\p2l.exe `
        -CsvTaskFile "D:\Warehouse\tasks.csv" `
        -StartNow
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $BinaryPath       = ".\artifacts\win-x64\p2l.exe",
    [string] $CsvTaskFile      = "C:\Program Files\P2L\tasks.csv",
    [string] $ServiceName      = "P2L",
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
    Fail "Binary not found: $BinaryPath`n  Build first with: .\deploy\publish.sh win-x64"
}

Write-Step "Installing P2L from : $BinaryPath"
Write-Step "CSV task file       : $CsvTaskFile"
Write-Step "Service name        : $ServiceName"

# ── 1. Copy files ─────────────────────────────────────────────────────────────
$InstallDir = "C:\Program Files\P2L"
$LogDir     = "$InstallDir\logs"
$Dest       = "$InstallDir\p2l.exe"

Write-Step "Creating install directory: $InstallDir"
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
New-Item -ItemType Directory -Path $LogDir     -Force | Out-Null

Write-Step "Copying binary..."
Copy-Item -Path $BinaryPath -Destination $Dest -Force

# Copy appsettings.json from binary directory if present
$AppSettingsSrc = Join-Path (Split-Path $BinaryPath) "appsettings.json"
if (Test-Path $AppSettingsSrc) {
    Copy-Item -Path $AppSettingsSrc -Destination "$InstallDir\appsettings.json" -Force
    Write-Ok "Copied appsettings.json"
} else {
    Write-Warn "appsettings.json not found alongside binary — using compiled-in defaults."
}

# ── 2. Secure directory ACL ───────────────────────────────────────────────────
Write-Step "Locking down directory permissions..."
$Acl = Get-Acl $InstallDir
$Acl.SetAccessRuleProtection($true, $false)     # disable inheritance
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

# ── 3. Remove existing service if present ────────────────────────────────────
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Warn "Service '$ServiceName' already exists — removing for clean install."
    if ($existing.Status -eq "Running") { Stop-Service -Name $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# ── 4. Register Windows Service ───────────────────────────────────────────────
# p2l detects SCM context via !Environment.UserInteractive and calls ServiceBase.Run()
$BinPathArg = "`"$Dest`" run `"$CsvTaskFile`""

Write-Step "Registering Windows Service..."
$result = sc.exe create $ServiceName `
    binPath= $BinPathArg `
    DisplayName= $ServiceDisplayName `
    start= auto `
    obj= "NT AUTHORITY\LocalService"

if ($LASTEXITCODE -ne 0) { Fail "sc.exe create failed: $result" }
Write-Ok "Service registered."

# Set description
sc.exe description $ServiceName "Pick2Light warehouse light-module controller (Aioi Systems)" | Out-Null

# Configure failure recovery: restart after 5 s, up to 3 times in a 24-hour window
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
Write-Ok "Failure recovery configured."

# ── 5. Set environment overrides (optional) ───────────────────────────────────
# Uncomment and adjust as needed:
# [Environment]::SetEnvironmentVariable("P2L_GATEWAY__HOST", "192.168.1.254", "Machine")
# [Environment]::SetEnvironmentVariable("P2L_GATEWAY__PORT", "5003",          "Machine")

# ── 6. Optionally start now ───────────────────────────────────────────────────
if ($StartNow) {
    Write-Step "Starting service..."
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 2
    $svc = Get-Service -Name $ServiceName
    Write-Ok "Service status: $($svc.Status)"
} else {
    Write-Warn "Service not started. Run when ready:"
    Write-Host "    Start-Service -Name $ServiceName" -ForegroundColor White
}

Write-Host ""
Write-Ok "Installation complete."
Write-Host "  Logs  : Get-EventLog -LogName Application -Source $ServiceName -Newest 50"
Write-Host "  Logs  : $LogDir\*.log"
Write-Host "  Stop  : Stop-Service -Name $ServiceName"
Write-Host "  Status: Get-Service -Name $ServiceName"
