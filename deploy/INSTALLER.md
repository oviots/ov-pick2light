# P2L Windows MSI Installer — WiX 4/5 Guide

## What You Get

A professional Windows MSI that:
- Installs `p2l.exe` (bridge), `p2l-client.exe` (dashboard), `e_sqlite3.dll`
- Registers both as **Windows Services** (auto-start, restart-on-failure)
- Writes `appsettings.json` to `C:\ProgramData\P2L\` (never overwritten on upgrade)
- Adds install dir to `PATH`
- Creates Start Menu shortcuts
- Handles clean **upgrades** (remove old, install new) and clean **uninstall**

---

## 1. Prerequisites (One-Time Setup)

### 1a. Install WiX toolset

```powershell
dotnet tool install --global wix
wix extension add WixToolset.UI.wixext
wix extension add WixToolset.Util.wixext
```

Verify:
```powershell
wix --version   # should print 5.x.x
```

### 1b. Install PyInstaller (for client binary)

```powershell
pip install pyinstaller
```

### 1c. .NET SDK 8+ (for dotnet publish)

Download: https://dot.net

---

## 2. Build Platform Notes

> ⚠️ **WiX 5 only compiles MSIs on Windows** (limitation of the toolset).
> On macOS/Linux: use the GitHub Actions workflow (Option A) or a Windows VM.

| Where you build | Supported? | Method |
|---|---|---|
| Windows (native) | ✅ | `build-msi.ps1` (Option B) |
| macOS / Linux | ✅ via CI | `build-msi.sh` → GitHub Actions |
| macOS / Linux (local) | ❌ | WiX 5 is Windows-only |

---

## 3. Build Steps

### Option A: From macOS/Linux via GitHub Actions (recommended)

**Prerequisites:**
```bash
brew install gh   # GitHub CLI
gh auth login
```

**Trigger build:**
```bash
./deploy/build-msi.sh              # x64, version 1.0.0
./deploy/build-msi.sh 1.2.3        # custom version, x64
./deploy/build-msi.sh 1.2.3 arm64  # ARM64
```

**Monitor and download:**
```bash
gh run watch                        # live progress
./deploy/build-msi.sh --download    # download MSI to dist/
```

Or trigger from GitHub UI: **Actions → Build P2L Windows MSI → Run workflow**

---

### Option B: Automated on Windows (recommended on Windows)

```powershell
# From repo root:
.\deploy\build-msi.ps1                          # x64
.\deploy\build-msi.ps1 -Platform arm64          # ARM64
.\deploy\build-msi.ps1 -Version 1.2.3 -Platform x64

# Skip rebuild if binaries already in artifacts\win-x64\
.\deploy\build-msi.ps1 -SkipDotnetPublish -SkipClientBuild
```

Output: `dist\P2L-<version>-<platform>.msi`

### Option C: Manual Step-by-Step (Windows)

**Step 1 — Build .NET binary (cross-platform, runs on Windows/macOS/Linux):**
```powershell
dotnet publish src\P2L.CLI -c Release -r win-x64 --self-contained true `
    -p:PublishAot=true -p:PublishSingleFile=false -o artifacts\win-x64
```

**Step 2 — Build client binary:**
```powershell
cd client
pyinstaller --onefile --name p2l-client --distpath ..\artifacts\win-x64 main.py
```

**Step 3 — Build MSI:**
```powershell
dotnet build deploy\P2L.Installer\P2L.Installer.wixproj `
    -c Release `
    -p:Platform=x64 `
    -p:ProductVersion=1.0.0 `
    -o dist
```

---

## 3. Install / Uninstall

### Interactive (with UI):
```cmd
msiexec /i dist\P2L-1.0.0-x64.msi
```

### Silent install:
```cmd
msiexec /i dist\P2L-1.0.0-x64.msi /qn

# Custom install directory:
msiexec /i dist\P2L-1.0.0-x64.msi /qn INSTALLFOLDER="D:\Warehouse\P2L\"
```

### Silent uninstall:
```cmd
msiexec /x dist\P2L-1.0.0-x64.msi /qn
# or via Product Code:
msiexec /x {87654321-4321-4321-4321-210987654321} /qn
```

### Upgrade (replace old version):
```cmd
msiexec /i dist\P2L-1.2.0-x64.msi /qn
# MajorUpgrade in Product.wxs automatically removes the old version first.
```

---

## 4. After Installation

| Resource | Location |
|---|---|
| Binaries | `C:\Program Files\P2L\` |
| Config | `C:\ProgramData\P2L\appsettings.json` |
| Logs | `C:\ProgramData\P2L\logs\` |
| Dashboard | http://localhost:8765 |

### Check services:
```powershell
Get-Service P2L, P2LClient
sc.exe query P2L
sc.exe query P2LClient
```

### Start/Stop manually:
```powershell
Start-Service P2L; Start-Service P2LClient
Stop-Service P2LClient; Stop-Service P2L
```

### View live logs:
```powershell
Get-Content "C:\ProgramData\P2L\logs\p2l.log" -Wait -Tail 50
```

---

## 5. Customization

### Change the UpgradeCode (IMPORTANT for production)

Each **product** must have a unique UpgradeCode GUID that **never changes**.
Generate one and replace in `Product.wxs`:

```powershell
[System.Guid]::NewGuid()   # generates a new GUID
```

Replace line in `Product.wxs`:
```xml
UpgradeCode="YOUR-NEW-GUID-HERE"
```

### Change component GUIDs

Each `<Component>` in `Components.wxs` and `Services.wxs` needs a stable GUID.
Generate unique GUIDs and replace the placeholder values.

> ⚠️ Never reuse or change Component GUIDs after shipping a version.
> WiX uses them to track installed components for repair/uninstall.

### Change version

```powershell
.\deploy\build-msi.ps1 -Version 2.0.0
```

or in `P2L.Installer.wixproj`:
```xml
<ProductVersion>2.0.0</ProductVersion>
```

### Change dashboard port

In `Services.wxs`, edit the Registry multiString value:
```xml
Value="P2L_CLIENT_HOST=0.0.0.0[~]P2L_CLIENT_PORT=8765"
```

### Custom branding (banner/dialog images)

1. Create `Assets\banner.bmp`  (493×58 pixels)
2. Create `Assets\dialog.bmp`  (493×312 pixels)
3. Uncomment in `Product.wxs`:
   ```xml
   <WixVariable Id="WixUIBannerBmp"  Value="Assets\banner.bmp"  />
   <WixVariable Id="WixUIDialogBmp"  Value="Assets\dialog.bmp"  />
   ```

---

## 6. Signing the MSI (Optional but Recommended)

```powershell
# Sign with a code-signing certificate
signtool sign `
    /fd SHA256 `
    /tr http://timestamp.digicert.com `
    /td SHA256 `
    /f your-cert.pfx `
    /p your-password `
    dist\P2L-1.0.0-x64.msi

# Verify signature
signtool verify /pa dist\P2L-1.0.0-x64.msi
```

---

## 7. File Structure Reference

```
deploy\P2L.Installer\
├── Product.wxs          Main package: version, upgrade, features, UI
├── Components.wxs       Files, config, shortcuts, registry
├── Services.wxs         Windows Service install/control for both services
├── P2L.Installer.wixproj  SDK-style WiX 5 project file
└── Assets\
    ├── License.rtf      License text shown in installer (edit as needed)
    └── appsettings.json Default config copied to C:\ProgramData\P2L\

deploy\build-msi.ps1     Automated build script (dotnet publish + PyInstaller + wix build)
```

---

## 8. Troubleshooting

| Problem | Cause | Fix |
|---|---|---|
| `e_sqlite3.dll not found` | Not produced by dotnet publish | Ensure `Microsoft.Data.Sqlite` is referenced in `P2L.Mqtt.csproj` |
| Service fails to start | Config not found | Check `C:\ProgramData\P2L\appsettings.json` exists |
| Service fails to start | Port in use | Stop any existing P2L process; check port 8765 |
| Upgrade blocked | Downgrade attempt | Uninstall old version manually first |
| `wix` command not found | Not installed | Run `dotnet tool install --global wix` |
| Build error: WXS namespace | Wrong namespace | Use `http://wixtoolset.org/schemas/v4/wxs` |

### View MSI installer log:
```cmd
msiexec /i dist\P2L-1.0.0-x64.msi /l*v install.log
notepad install.log
```
