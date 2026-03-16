# publish.ps1 — Build Pick2Light as a Native AOT single-file binary (Windows)
#
# Usage:
#   .\deploy\publish.ps1                  # native platform (default)
#   .\deploy\publish.ps1 -RID win-x64
#   .\deploy\publish.ps1 -RID win-arm64
#   .\deploy\publish.ps1 -All             # build all architectures for current OS
#
# NOTE: Native AOT does not support cross-OS compilation.
# Build on target platform: use Windows for win-*, Linux for linux-*, macOS for osx-*

param(
    [string]$RID = "native",
    [switch]$All
)

$ErrorActionPreference = "Stop"

$dotnet = if ($env:DOTNET_ROOT) { "$env:DOTNET_ROOT\dotnet.exe" } else { "dotnet" }
$project = "src\P2L.CLI\P2L.CLI.csproj"
$solutionRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Build-RID {
    param([string]$rid)

    $out = Join-Path $solutionRoot "artifacts" $rid
    New-Item -ItemType Directory -Force -Path $out | Out-Null

    Write-Host ""
    Write-Host "==> Publishing P2L for $rid..."
    
    $extraFlags = @()
    if ($rid -like "linux-*") {
        $extraFlags += "-p:UsePortableLinuxSharedLibraries=true"
    }

    & $dotnet publish "$solutionRoot\$project" `
        --configuration Release `
        --runtime $rid `
        --output "$out" `
        --self-contained true `
        -p:PublishAot=true `
        -p:InvariantGlobalization=true `
        -p:StripSymbols=true `
        @extraFlags

    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed for $rid" -ForegroundColor Red
        exit 1
    }

    $bin = Join-Path $out "p2l.exe"
    if (-not (Test-Path $bin)) {
        $bin = Join-Path $out "p2l"
    }

    Write-Host "==> Done: $bin"
    if (Test-Path $bin) {
        $size = (Get-Item $bin).Length / 1MB
        Write-Host "Size: $($size.ToString('F1')) MB"
    } else {
        Get-Item $out | Format-Table Name, Length
    }
}

function Detect-OS {
    if ($PSVersionTable.Platform -eq "Unix") {
        $uname = uname -s
        if ($uname -eq "Darwin") { return "osx" }
        elseif ($uname -eq "Linux") { return "linux" }
    }
    return "win"
}

function Detect-Arch {
    if ($PSVersionTable.Platform -eq "Unix") {
        $uname = uname -m
        if ($uname -eq "aarch64" -or $uname -eq "arm64") { return "arm64" }
    } else {
        # Windows: check processor architecture
        if ([Environment]::Is64BitOperatingSystem) {
            if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq "Arm64") {
                return "arm64"
            }
        }
    }
    return "x64"
}

$currentOS = Detect-OS
$currentArch = Detect-Arch

if ($All) {
    Write-Host "Building for current OS ($currentOS) only."
    Write-Host "Warning: Cross-OS compilation is not supported by Native AOT."
    
    if ($currentOS -eq "osx") {
        foreach ($arch in @("x64", "arm64")) {
            Build-RID "osx-$arch"
        }
    } elseif ($currentOS -eq "linux") {
        foreach ($arch in @("x64", "arm64")) {
            Build-RID "linux-$arch"
        }
    } else {
        foreach ($arch in @("x64", "arm64")) {
            Build-RID "win-$arch"
        }
    }
} elseif ($RID -eq "native") {
    if ($currentOS -eq "osx") {
        Build-RID "osx-$currentArch"
    } elseif ($currentOS -eq "linux") {
        Build-RID "linux-$currentArch"
    } else {
        Build-RID "win-$currentArch"
    }
} else {
    Build-RID $RID
}

Write-Host ""
Write-Host "Build completed successfully!" -ForegroundColor Green
