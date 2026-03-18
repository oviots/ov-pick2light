# Windows Installation Guide — Pick2Light (P2L)

## What Gets Installed

| Item | Location |
|---|---|
| `p2l.exe` — Bridge service | `C:\Program Files\P2L\` |
| `p2l-client.exe` — Web dashboard | `C:\Program Files\P2L\` |
| `e_sqlite3.dll` — SQLite runtime | `C:\Program Files\P2L\` |
| `appsettings.json` — Config | `C:\ProgramData\P2L\` |
| Logs | `C:\ProgramData\P2L\logs\` |
| **P2L** Windows Service | Auto-start, restarts on failure |
| **P2LClient** Windows Service | Auto-start, dashboard on http://localhost:8765 |

---

## Installation Methods

### Option 1: MSI Installer (Recommended)

**Easiest method** — includes binary directly, works on any Windows 10/11 system.

#### Steps:

1. **Prepare the binary:**
   ```powershell
   # On your build machine, publish for Windows
   .\deploy\publish.ps1 -RID win-x64
   ```

2. **Create installer package:**
   - Copy `artifacts/win-x64/p2l.exe` to `deploy/` folder
   - Copy `deploy/p2l-install.bat` alongside the binary
   - Bundle both files for distribution

3. **Install on target machine:**
   ```cmd
   # Run as Administrator
   p2l-install.bat install
   ```

4. **Verify installation:**
   ```cmd
   # Open new Command Prompt and run:
   p2l --help
   p2l devices
   ```

5. **Uninstall (if needed):**
   ```cmd
   # Run as Administrator
   p2l-install.bat uninstall
   ```

### Option 2: MSI Installer (Professional - Requires WiX Toolset)

**For enterprise deployments** — creates professional `.msi` package with:
- Windows standard installation experience
- Automatic uninstall registry entries
- Start Menu shortcuts
- System PATH integration
- Optional service registration

#### Prerequisites:

1. **Install WiX Toolset 4.0:**
   - Download: https://wixtoolset.org/releases/v4.0.x/wix4-latest.exe
   - Or via PowerShell:
     ```powershell
     winget install WiXToolset
     ```

2. **.NET 8+ SDK** (if building from source)

#### Build MSI:

```powershell
cd deploy

# Build for x64
.\build-msi.ps1 -Platform x64

# Build for arm64
.\build-msi.ps1 -Platform arm64

# With release output directory
.\build-msi.ps1 -Platform x64 -OutputPath C:\Releases -Configuration Release
```

#### Install MSI:

```cmd
# Interactive installation
msiexec /i P2L-1.0.0-x64.msi

# Silent installation (admin required)
msiexec /i P2L-1.0.0-x64.msi /qn

# With logging
msiexec /i P2L-1.0.0-x64.msi /l*v install.log
```

#### Uninstall MSI:

```cmd
# Interactive uninstall
msiexec /x P2L-1.0.0-x64.msi

# Silent uninstall
msiexec /x P2L-1.0.0-x64.msi /qn
```

### Option 3: Manual Installation (DIY)

**Most control** — for experienced users or custom setups.

```cmd
# 1. Create installation directory
mkdir "C:\Program Files\Pick2Light\bin"

# 2. Copy binary
copy "artifacts\win-x64\p2l.exe" "C:\Program Files\Pick2Light\bin\p2l.exe"

# 3. Add to PATH (as Administrator)
setx /M PATH "%PATH%;C:\Program Files\Pick2Light\bin"

# 4. Create config directory
mkdir "%ProgramData%\Pick2Light\config"

# 5. Close and reopen command prompt, then test:
p2l --help
```

---

## Deployment Scenarios

### Single Workstation

```powershell
# Option 1: Batch installer (simplest)
.\p2l-install.bat install

# Option 2: Manual copy
Copy-Item "p2l.exe" -Destination "$env:ProgramFiles\Pick2Light\bin\"
```

### Enterprise Rollout (SCCM / GPO)

```powershell
# Option 1: MSI deployment
# - Add P2L-1.0.0-x64.msi to SCCM package
# - Create deployment rule: msiexec /i P2L-1.0.0-x64.msi /qn

# Option 2: Batch installer via Group Policy
# - Copy p2l-install.bat to network share
# - Create logon script: net use Z: \\server\share && Z:\p2l-install.bat install
```

### Docker Container

```dockerfile
FROM mcr.microsoft.com/windows/servercore:ltsc2022

# Copy binary
COPY artifacts/win-x64/p2l.exe /p2l/
ENV PATH="${PATH};C:\p2l"

# Set working directory
WORKDIR /p2l
ENTRYPOINT ["p2l"]
```

### Portable USB Drive

```powershell
# Create portable version
mkdir D:\P2L\bin
Copy-Item "p2l.exe" -Destination "D:\P2L\bin\"

# On target machine (no installation needed):
D:\P2L\bin\p2l.exe --help
```

---

## Troubleshooting

### "p2l is not recognized as an internal or external command"

**Cause:** P2L not in PATH or PATH not refreshed

**Solution:**
```cmd
# Check PATH
echo %PATH%

# Manually run from installation directory
"C:\Program Files\Pick2Light\bin\p2l.exe" --help

# Restart PowerShell or Command Prompt to reload PATH
```

### "Access denied" or "Permission denied"

**Cause:** Not running as Administrator

**Solution:**
- Right-click Command Prompt → "Run as Administrator"
- Or right-click `p2l-install.bat` → "Run as Administrator"

### "p2l.exe cannot be opened because it is from an unknown publisher"

**Cause:** Windows Defender SmartScreen blocking unsigned binary

**Solution:**
```powershell
# Option 1: Unblock file properties
Unblock-File -Path "C:\Program Files\Pick2Light\bin\p2l.exe"

# Option 2: Via GUI
# Right-click p2l.exe → Properties → Unblock → Apply
```

### Port conflicts (gateway communication fails)

**Cause:** Port already in use

**Solution:**
```cmd
# Find process using port (e.g., 10000)
netstat -ano | findstr :10000

# Kill process (replace PID)
taskkill /PID <PID> /F

# Or configure alternate port in config file
```

### Service fails to start

**Cause:** Missing dependencies or incorrect executable path

**Solution:**
```cmd
# Check service status
sc query P2LWarehouseService

# View service details
Get-Service P2LWarehouseService -DisplayName | Format-List *

# Restart service
net stop P2LWarehouseService
net start P2LWarehouseService

# View event logs
Get-EventLog -LogName System -Source P2LWarehouseService -Newest 10
```

---

## Configuration

### Post-Installation Setup

After installation, configure your gateway connection:

1. **Create config file:**
   ```cmd
   mkdir "%ProgramData%\Pick2Light\config"
   echo. > "%ProgramData%\Pick2Light\config\appsettings.json"
   ```

2. **Edit with your gateway details:**
   ```json
   {
     "Gateway": {
       "Host": "192.168.1.100",
       "Port": 10000,
       "Timeout": 5000
     },
     "Picking": {
       "AutoConfirm": false,
       "MaxBatch": 100
     }
   }
   ```

3. **Test connection:**
   ```cmd
   p2l devices
   ```

---

## Upgrade & Maintenance

### Updating to New Version

**Using batch installer:**
```cmd
# Uninstall old version
p2l-install.bat uninstall

# Install new version
p2l-install.bat install
```

**Using MSI:**
```powershell
# MSI automatically handles upgrades
msiexec /i P2L-2.0.0-x64.msi

# Or uninstall first, then install new version
msiexec /x P2L-1.0.0-x64.msi /qn
msiexec /i P2L-2.0.0-x64.msi /qn
```

### Backup Configuration

```cmd
# Before upgrade, backup config
copy "%ProgramData%\Pick2Light\config\*" "C:\Backup\P2L\"
```

---

## Performance Tuning

### Memory & CPU

P2L (Native AOT) uses minimal resources (~50 MB RAM, <5% CPU at idle).

Monitor with Task Manager or PowerShell:
```powershell
Get-Process p2l | Select-Object ProcessName, Memory, CPU, Threads
```

### Registry Settings (Advanced)

For very low-latency scenarios:
```cmd
# Increase thread priority (caution - system-wide impact)
# See documentation for Service-specific tuning
```

---

## Uninstallation

### Remove P2L

```cmd
# Using batch installer
p2l-install.bat uninstall

# Or manually
del "C:\Program Files\Pick2Light\bin\p2l.exe"
rmdir "C:\Program Files\Pick2Light"
```

### Clean Configuration

```cmd
# Keep config (for reinstall)
# Skip this if you want clean slate

# Or delete config
rmdir /s %ProgramData%\Pick2Light\config
```

---

## Support & Debugging

### Enable Debug Logging

Set environment variable:
```powershell
$env:P2L_LOG_LEVEL = "Debug"
p2l --help
```

### Export Logs

```cmd
# Find log file location
set | findstr P2L

# Copy logs to USB/network for analysis
copy "logs\p2l-*.log" "\\server\share\diagnostics\"
```

### Collect Diagnostics

```powershell
# System info
systeminfo | Out-File p2l-sysinfo.txt

# Network config
ipconfig /all | Out-File p2l-network.txt

# Running processes
Get-Process | Out-File p2l-processes.txt

# Zip for support
Compress-Archive -Path p2l-*.txt -DestinationPath p2l-diagnostics.zip
```

---

## Security

### Antivirus Exceptions

Add to antivirus whitelist:
- `C:\Program Files\Pick2Light\bin\p2l.exe`
- `%ProgramData%\Pick2Light\`

### Firewall Rules

```cmd
# Allow inbound (for service)
netsh advfirewall firewall add rule name="P2L In" dir=in action=allow program="C:\Program Files\Pick2Light\bin\p2l.exe"

# Allow outbound to gateway
netsh advfirewall firewall add rule name="P2L Out" dir=out action=allow program="C:\Program Files\Pick2Light\bin\p2l.exe"
```

### Code Signing (Future)

When production-ready:
```cmd
# Sign binary during build
signtool sign /f "cert.pfx" /p "password" /fd sha256 "p2l.exe"
```

---

## Contact & Support

- **Issues:** https://github.com/pick2light/p2l/issues
- **Documentation:** See `BUILD.md` and `README.md`
- **Configuration:** See `CLAUDE.md` for architecture details
