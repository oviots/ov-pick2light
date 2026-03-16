# P2L Installer Package

This directory contains installation tools and packages for Pick2Light (P2L) on Windows systems.

## Contents

```
deploy/
├── publish.sh              # Build script (macOS/Linux)
├── publish.ps1             # Build script (Windows)
├── build-msi.ps1           # MSI builder (requires WiX 4.0+)
├── p2l-install.bat         # Simple batch installer
├── P2L.Installer/          # WiX project directory
│   ├── P2L.Installer.wixproj
│   ├── Product.wxs
│   ├── Features.wxs
│   └── UI.wxs
├── install-linux.sh        # Linux systemd installer
└── check-gateway.sh        # Gateway connectivity checker
```

## Quick Start

### Option 1: Simple Installer (No dependencies)

```cmd
REM On Windows, run as Administrator:
p2l-install.bat install

REM Verify:
p2l --help
```

### Option 2: Professional MSI (Requires WiX)

```powershell
# First, build P2L binary:
.\publish.ps1 -RID win-x64

# Then build MSI:
.\build-msi.ps1 -Platform x64

# Install:
msiexec /i P2L-1.0.0-x64.msi
```

### Option 3: Docker / Portable

See [`WINDOWS_INSTALL.md`](../WINDOWS_INSTALL.md) for Docker and portable installation methods.

---

## Building Installers

### Build Native Binary First

```powershell
# Windows binary
.\publish.ps1 -RID win-x64

# For arm64:
.\publish.ps1 -RID win-arm64
```

Binary will be at: `artifacts/win-x64/p2l.exe`

### Create Batch Installer Package

```cmd
REM Copy binary alongside batch script
copy artifacts\win-x64\p2l.exe deploy\
REM Then distribute: deploy\p2l.exe + deploy\p2l-install.bat
```

### Create Professional MSI

```powershell
cd deploy

# Requirements:
# - WiX Toolset 4.0+: https://wixtoolset.org/
# - Visual Studio 2022 or Build Tools

# Build for x64:
.\build-msi.ps1 -Platform x64 -OutputPath .\releases

# Build for arm64:
.\build-msi.ps1 -Platform arm64 -OutputPath .\releases

# Output: releases\P2L-1.0.0-x64.msi, etc.
```

---

## Installer Features

### Batch Installer (p2l-install.bat)

✓ Simple, lightweight  
✓ No external dependencies  
✓ Works on Windows 7+  
✓ Adds to system PATH  
✓ Creates config directory  
✓ Creates registry uninstall entry  

```cmd
p2l-install.bat install     # Install
p2l-install.bat uninstall   # Remove
p2l-install.bat help        # Show help
```

### MSI Installer (WiX-based)

✓ Professional Windows experience  
✓ Standard Add/Remove Programs entry  
✓ Start Menu shortcuts  
✓ System PATH integration  
✓ Optional service registration  
✓ Automatic upgrades/repairs  
✓ Silent/automated deployment support  

```cmd
msiexec /i P2L-1.0.0-x64.msi              # Interactive
msiexec /i P2L-1.0.0-x64.msi /qn          # Silent
msiexec /i P2L-1.0.0-x64.msi /l*v log.txt # With logging
```

---

## Installation Directories

```
Windows:
  Binary:       C:\Program Files\Pick2Light\bin\p2l.exe
  Config:       %ProgramData%\Pick2Light\config\
  Registry:     HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\

Linux:
  Binary:       /usr/local/bin/p2l
  Config:       /etc/pick2light/config/
  Systemd:      /etc/systemd/system/p2l.service
  Service user: p2l

macOS:
  Binary:       /usr/local/bin/p2l
  Config:       ~/.config/p2l/
  LaunchAgent:  ~/.local/share/launchd/
```

---

## Deployment Scenarios

### Single Workstation

```cmd
p2l-install.bat install
```

### Multiple Workstations (SCCM, Group Policy)

1. Create MSI package
2. Deploy via SCCM: `msiexec /i P2L-1.0.0-x64.msi /qn`
3. Or via Group Policy logon script

### Docker Container

```dockerfile
FROM mcr.microsoft.com/windows/servercore:ltsc2022
COPY artifacts/win-x64/p2l.exe /p2l/
ENV PATH="${PATH};C:\p2l"
ENTRYPOINT ["p2l"]
```

### Warehouse Handhelds

1. Build for arm64: `.\publish.ps1 -RID win-arm64`
2. Create MSI: `.\build-msi.ps1 -Platform arm64`
3. Deploy to fleet via MDM or manual installation

---

## Troubleshooting

### "Administrator privileges required"

```cmd
REM Right-click Command Prompt and select "Run as administrator"
REM Or use:
runas /user:Administrator "cmd.exe"
```

### "p2l is not recognized"

```cmd
REM Close and reopen Command Prompt to reload PATH
REM Or run from full path:
"C:\Program Files\Pick2Light\bin\p2l.exe" --help
```

### MSI build fails with "WiX Toolset not found"

```powershell
# Install WiX 4.0:
winget install WiXToolset

# Or download:
# https://wixtoolset.org/releases/v4.0.x/wix4-latest.exe
```

### Service fails to start

```cmd
sc query P2LWarehouseService
Get-EventLog -LogName System -Source P2LWarehouseService -Newest 10
```

---

## Files Reference

| File | Purpose | Requires |
|------|---------|----------|
| `publish.ps1` | Build native binary | .NET 10 SDK |
| `publish.sh` | Build native binary | .NET 10 SDK, bash |
| `p2l-install.bat` | Simple installer | p2l.exe in same dir |
| `build-msi.ps1` | Build MSI package | WiX 4.0+, binary built |
| `P2L.Installer/` | WiX project files | WiX 4.0+, Visual Studio |
| `install-linux.sh` | Linux systemd setup | bash, sudo |
| `check-gateway.sh` | Test gateway | bash, nc/netcat |

---

## Advanced Configuration

### Custom Installation Path (Batch)

Edit `p2l-install.bat` and change:
```batch
set "INSTALL_DIR=%ProgramFiles%\Pick2Light\bin"
REM To:
set "INSTALL_DIR=C:\Custom\P2L\Path"
```

### Custom WiX Properties (MSI)

Modify `Product.wxs`:
```xml
<Directory Id="INSTALLFOLDER" Name="Pick2Light">
  <!-- Change to desired location -->
</Directory>
```

### Service Configuration

Edit `Features.wxs` to customize service:
```xml
<ServiceInstall
  DisplayName="My Custom Name"
  Description="My custom description"
/>
```

---

## For Maintainers

### Release Checklist

- [ ] Update version in `P2L.Installer.wixproj`
- [ ] Build binaries: `.\publish.ps1 -RID win-x64`
- [ ] Create MSI: `.\build-msi.ps1 -Platform x64`
- [ ] Test batch installer: `p2l-install.bat install`
- [ ] Test MSI: `msiexec /i P2L-1.0.0-x64.msi`
- [ ] Create release notes
- [ ] Sign binaries (optional): `signtool sign /f cert.pfx p2l.exe`
- [ ] Create GitHub release with both installers

### Batch Installer Update

When updating `p2l-install.bat`:
1. Increment `VERSION` in script
2. Test install/uninstall
3. Verify PATH integration
4. Verify config directory creation

### MSI Update

When updating MSI:
1. Increment version in `P2L.Installer.wixproj`
2. Update `UpgradeCode` if major version change
3. Run `build-msi.ps1`
4. Test upgrade: `msiexec /i old.msi` → `msiexec /i new.msi`

---

## See Also

- [`WINDOWS_INSTALL.md`](../WINDOWS_INSTALL.md) — Detailed Windows installation guide
- [`BUILD.md`](../BUILD.md) — Cross-platform build documentation
- [`README.md`](../README.md) — Project overview
- [`CLAUDE.md`](../CLAUDE.md) — Architecture & standards
