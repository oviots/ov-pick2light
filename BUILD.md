# Pick2Light (P2L) Build & Deployment Guide

## Overview

Pick2Light is built as a **Native AOT** single-file binary for maximum portability and reduced footprint. This guide covers building for Windows, Linux, and macOS.

### Key Points
- **No .NET Runtime required** — fully self-contained executable
- **Single binary** — no dependencies to ship
- **Cross-platform** — supports Windows (x64/arm64), Linux (x64/arm64), macOS (x64/arm64)
- **Native AOT limitation** — must build on (or for) each target platform (cross-OS compilation not supported)

---

## Prerequisites

### macOS & Linux
```bash
# Install .NET 10 SDK
curl https://dot.net/v1/dotnet-install.sh | bash
export PATH="$HOME/.dotnet:$PATH"
```

### Windows
Download [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

---

## Building on macOS

### For macOS (arm64) — Apple Silicon
```bash
./deploy/publish.sh osx-arm64
# Output: artifacts/osx-arm64/p2l (15 MB)
```

### For macOS (x64) — Intel Macs
```bash
./deploy/publish.sh osx-x64
# Output: artifacts/osx-x64/p2l (15 MB)
```

### Build Both macOS Architectures
```bash
./deploy/publish.sh native  # Auto-detects and builds for current platform
```

### Signing & Notarization (Optional - for distribution)
```bash
# Sign the binary
codesign -s - artifacts/osx-arm64/p2l

# For App Store / developer ID, replace `-s -` with your certificate
```

---

## Building on Linux

### For Linux (x64) — Standard servers
```bash
./deploy/publish.sh linux-x64
# Output: artifacts/linux-x64/p2l (15 MB)
```

### For Linux (arm64) — ARM servers / Raspberry Pi
```bash
./deploy/publish.sh linux-arm64
# Output: artifacts/linux-arm64/p2l (15 MB)
```

### Or use Docker (for cross-platform builds from macOS/Windows)
```bash
# Build linux-x64 binary on any platform
docker run --rm \
  -v "$(pwd):/src" \
  -w /src/src/P2L.CLI \
  mcr.microsoft.com/dotnet/sdk:10 \
  dotnet publish \
    -c Release \
    -r linux-x64 \
    -p:PublishAot=true \
    -p:UsePortableLinuxSharedLibraries=true \
    -p:StripSymbols=true \
    --output /src/artifacts/linux-x64
```

---

## Building on Windows

### For Windows (x64) — Standard PCs
```powershell
.\deploy\publish.ps1 win-x64
# Or use dotnet CLI directly:
dotnet publish src/P2L.CLI/P2L.CLI.csproj `
  -c Release `
  -r win-x64 `
  -p:PublishAot=true `
  --output artifacts/win-x64
```

### For Windows (arm64) — ARM-based devices
```powershell
.\deploy\publish.ps1 win-arm64
```

### Output
- `artifacts/win-x64/p2l.exe` (15 MB)
- `artifacts/win-arm64/p2l.exe` (15 MB)

---

## Deployment

### macOS
```bash
# Copy to Applications
cp artifacts/osx-arm64/p2l /usr/local/bin/p2l
chmod +x /usr/local/bin/p2l

# Or copy to a specific directory
cp artifacts/osx-arm64/p2l /opt/p2l/bin/

# Run
p2l --help
```

### Linux (Standard install)
```bash
sudo cp artifacts/linux-x64/p2l /usr/local/bin/p2l
sudo chmod +x /usr/local/bin/p2l

# Verify
p2l --help
```

### Linux (systemd Service)
```bash
# Copy binary
sudo cp artifacts/linux-x64/p2l /usr/local/bin/p2l
sudo chmod +x /usr/local/bin/p2l

# Create systemd service file
sudo tee /etc/systemd/system/p2l.service > /dev/null <<EOF
[Unit]
Description=Pick2Light Warehouse Control System
After=network.target

[Service]
Type=simple
ExecStart=/usr/local/bin/p2l run /opt/p2l/config/picking-batch.csv
Restart=on-failure
RestartSec=10
StandardOutput=journal
StandardError=journal
User=p2l
WorkingDirectory=/opt/p2l

[Install]
WantedBy=multi-user.target
EOF

# Enable and start
sudo systemctl daemon-reload
sudo systemctl enable p2l
sudo systemctl start p2l

# Monitor
sudo journalctl -u p2l -f
```

### Windows (CLI)
```cmd
artifacts\win-x64\p2l.exe --help

# Or add to PATH for system-wide access
set PATH=%PATH%;C:\opt\p2l\bin
```

### Windows (Service - future)
To run as a Windows Service, a separate installer or SC command would be needed. Contact DevOps for setup.

---

## Testing & Verification

### Quick Test (all platforms)
```bash
# Display help
./artifacts/*/p2l --help

# Check version
./artifacts/*/p2l --version

# List devices (requires Aioi gateway running)
./artifacts/*/p2l devices

# Test light control
./artifacts/*/p2l light <module-id> on
```

### Performance Check
```bash
# Binary size
ls -lh artifacts/*/p2l

# Runtime check
time ./artifacts/osx-arm64/p2l --help

# Memory usage (on macOS)
/usr/bin/time -v ./artifacts/osx-arm64/p2l --help
```

---

## Troubleshooting

### macOS: "p2l" cannot be opened because the developer cannot be verified
```bash
# Bypass Gatekeeper (one-time)
xattr -d com.apple.quarantine /usr/local/bin/p2l

# Or allow in Security & Privacy settings
```

### Linux: Permission denied
```bash
chmod +x /usr/local/bin/p2l
```

### Windows: Cannot find p2l
Ensure `artifacts/win-x64/p2l.exe` exists and the path is in `%PATH%`

### Build fails with "clang: invalid linker name"
This happens when cross-compiling from macOS to Linux. Use Docker instead (see above).

---

## Build Artifacts

After running `./deploy/publish.sh all` on each platform, you'll have:

```
artifacts/
├── osx-x64/
│   ├── p2l                    # macOS Intel binary
│   └── p2l.dSYM/             # Debug symbols
├── osx-arm64/
│   ├── p2l                    # macOS Apple Silicon binary
│   └── p2l.dSYM/
├── linux-x64/
│   └── p2l                    # Linux x64 binary
├── linux-arm64/
│   └── p2l                    # Linux ARM binary
├── win-x64/
│   └── p2l.exe                # Windows x64 binary
└── win-arm64/
    └── p2l.exe                # Windows ARM binary
```

Each binary is ~15 MB and fully self-contained.

---

## Native AOT Limitations

- **No dynamic code generation** — all types must be known at compile time
- **Reflection is limited** — use Source Generators for JSON serialization (already configured)
- **No cross-OS compilation** — must compile on target platform or use Docker
- **Platform-specific code** — Windows service host is compiled only for win-x64/win-arm64

---

## CI/CD Integration

### GitHub Actions Example
```yaml
name: Build P2L Binaries

on:
  push:
    branches: [main]

jobs:
  build-macos:
    runs-on: macos-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10'
      - run: chmod +x deploy/publish.sh && ./deploy/publish.sh native
      - uses: actions/upload-artifact@v4
        with:
          name: p2l-osx
          path: artifacts/osx-*/p2l

  build-linux:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10'
      - run: chmod +x deploy/publish.sh && ./deploy/publish.sh native
      - uses: actions/upload-artifact@v4
        with:
          name: p2l-linux
          path: artifacts/linux-*/p2l

  build-windows:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10'
      - run: .\deploy\publish.ps1 -native
      - uses: actions/upload-artifact@v4
        with:
          name: p2l-windows
          path: artifacts/win-*/p2l.exe
```

---

## For Developers

### Build locally during development
```bash
# Debug build (JIT, slower but with debugging)
dotnet run --project src/P2L.CLI/P2L.CLI.csproj -- --help

# Release build (AOT, optimized)
./deploy/publish.sh native
```

### Clean build
```bash
rm -rf artifacts/*/
dotnet clean
./deploy/publish.sh native
```

### Publish to GitHub Releases
```bash
git tag v1.0.0
git push --tags

# Build binaries and attach to release via GitHub API or gh CLI
gh release create v1.0.0 artifacts/*/p2l* --draft
```

---

## Support

For issues or questions:
- Check the main [README.md](README.md)
- Review [CLAUDE.md](CLAUDE.md) for architecture
- Open an issue on GitHub
