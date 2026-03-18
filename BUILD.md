# Pick2Light (P2L) — Build & Deployment Guide

## Overview

P2L consists of two services:

| Service | Binary | Description |
|---|---|---|
| **P2L Bridge** | `p2l` / `p2l.exe` | .NET 10 Native AOT — controls Aioi light modules, exposes MQTT bridge |
| **P2L Client** | `p2l-client` / `p2l-client.exe` | Python web dashboard (PyInstaller binary) — no Python needed on target |

Both are **fully self-contained** — no .NET runtime, no Python installation required on deployment machines.

---

## Quick Start

```bash
# Linux/macOS — full package (binary + client + installer scripts)
./deploy/package.sh linux-x64
./deploy/package.sh osx-arm64

# Windows — prepare binaries via Docker, build MSI on Windows
docker build -f deploy/Dockerfile.build -t p2l-builder .
docker run --rm -v "$(pwd)/artifacts:/workspace/artifacts" p2l-builder win-x64
# Then on Windows: .\deploy\build-msi.ps1 -SkipDotnetPublish
```

---

## Prerequisites

### P2L Bridge (.NET 10)

```bash
# macOS / Linux
curl -L https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
export PATH="$HOME/.dotnet:$PATH"
dotnet --version   # 10.0.x

# Windows
# Download from https://dotnet.microsoft.com/download/dotnet/10.0
```

Native AOT also requires a C compiler toolchain:

| Platform | Requirement |
|---|---|
| macOS | Xcode CLI tools: `xcode-select --install` |
| Linux | clang + zlib: `apt install clang zlib1g-dev` |
| Windows | Visual Studio Build Tools (C++ workload) |

### P2L Client (Python 3.11+)

```bash
pip install pyinstaller
pip install -r client/requirements.txt
```

---

## Package Build (Recommended)

`package.sh` / `package.ps1` orchestrates the full build:
1. `dotnet publish` (Native AOT) for the bridge binary
2. `pyinstaller` for the standalone client binary
3. Bundles `e_sqlite3.dll/.so/.dylib` (SQLite native lib required at runtime)
4. Writes installer scripts into the package
5. Archives everything as `.tar.gz` (Linux/macOS) or `.zip` (Windows)

### Linux / macOS

```bash
# Native platform
./deploy/package.sh

# Explicit RID
./deploy/package.sh linux-x64
./deploy/package.sh linux-arm64   # Raspberry Pi, ARM servers
./deploy/package.sh osx-arm64     # Apple Silicon
./deploy/package.sh osx-x64       # Intel Mac
```

Output: `dist/p2l-<version>-<rid>.tar.gz`

### Windows

```powershell
.\deploy\package.ps1                   # x64
.\deploy\package.ps1 -Platform arm64   # ARM64
.\deploy\package.ps1 -Version 1.2.3
```

Output: `dist\p2l-<version>-win-x64.zip`

### Package Layout

```
p2l-<version>-<rid>/
├── bin/
│   ├── p2l                      P2L bridge binary (p2l.exe on Windows)
│   ├── p2l-client               Web dashboard binary (p2l-client.exe on Windows)
│   └── libe_sqlite3.dylib       SQLite native lib (name varies by platform)
├── config/
│   └── appsettings.json         Bridge configuration
└── deploy/
    ├── install.sh               Linux/macOS installer
    ├── install.ps1              Windows installer
    ├── p2l.service              systemd unit (bridge)
    └── p2l-client.service       systemd unit (dashboard)
```

> ⚠️ The SQLite native lib (`e_sqlite3.*`) **must** remain alongside the `p2l` binary.

---

## Building with Docker

Docker builds run on any host (macOS, Linux, Windows) and produce packages for other platforms.

```bash
# Build the image (one-time)
docker build -f deploy/Dockerfile.build -t p2l-builder .

# Linux packages (full: AOT binary + PyInstaller client + tar.gz)
docker run --rm -v "$(pwd)/dist:/workspace/dist" p2l-builder linux-x64
docker run --rm -v "$(pwd)/dist:/workspace/dist" p2l-builder linux-arm64

# Windows binaries only (p2l.exe + e_sqlite3.dll → artifacts/win-x64/)
# Note: p2l-client.exe and MSI require Windows — see MSI section below
docker run --rm -v "$(pwd)/artifacts:/workspace/artifacts" p2l-builder win-x64
```

### What Docker can/cannot build

| Target | Docker (Linux) | Notes |
|---|---|---|
| `linux-x64` full package | ✅ | AOT + PyInstaller + tar.gz |
| `linux-arm64` full package | ✅ | Cross-compiled |
| `win-x64` binaries | ✅ | `p2l.exe` + `e_sqlite3.dll` (non-AOT, self-contained) |
| `win-x64` `p2l-client.exe` | ❌ | PyInstaller requires Windows |
| `win-x64` MSI | ❌ | WiX 5 is Windows-only |
| `osx-*` any | ❌ | macOS SDK required |

---

## Building the Bridge Only (dotnet publish)

For CI or when you only need the bridge binary:

```bash
# Native AOT (production — smaller, faster startup, no runtime)
dotnet publish src/P2L.CLI -c Release -r linux-x64 \
    --self-contained true \
    -p:PublishAot=true \
    -p:StripSymbols=true \
    -o artifacts/linux-x64

# Non-AOT self-contained (for cross-compile from macOS → Windows)
dotnet publish src/P2L.CLI -c Release -r win-x64 \
    --self-contained true \
    -o artifacts/win-x64
# Produces: artifacts/win-x64/p2l.exe + e_sqlite3.dll
```

### Publish profiles (MSBuild)

```bash
dotnet publish src/P2L.CLI/P2L.CLI.csproj /p:PublishProfile=linux-x64
dotnet publish src/P2L.CLI/P2L.CLI.csproj /p:PublishProfile=win-x64
```

Profiles in `src/P2L.CLI/Properties/PublishProfiles/` set: `PublishAot=true`, `SelfContained=true`, `InvariantGlobalization=true`, `StripSymbols=true`.

---

## Windows MSI Installer

See **[deploy/INSTALLER.md](INSTALLER.md)** for the full WiX 5 MSI guide.

### From macOS/Linux — via GitHub Actions

```bash
# Trigger build on a Windows runner (requires gh CLI + repo push access)
./deploy/build-msi.sh 1.0.0           # x64
./deploy/build-msi.sh 1.0.0 arm64     # ARM64

# Monitor
gh run watch

# Download MSI when done
./deploy/build-msi.sh --download
```

### From Windows — local build

```powershell
.\deploy\build-msi.ps1 -Version 1.0.0           # full build
.\deploy\build-msi.ps1 -SkipDotnetPublish        # use existing artifacts/win-x64/
```

Prerequisites: `dotnet tool install --global wix && wix extension add WixToolset.UI.wixext && wix extension add WixToolset.Util.wixext`

---

## Artifact Structure

After all platform builds:

```
artifacts/
├── osx-arm64/
│   ├── p2l                        macOS Apple Silicon bridge (AOT, ~15 MB)
│   └── libe_sqlite3.dylib
├── osx-x64/
│   ├── p2l
│   └── libe_sqlite3.dylib
├── linux-x64/
│   ├── p2l                        Linux x64 bridge (AOT, ~15 MB)
│   └── libe_sqlite3.so
├── linux-arm64/
│   ├── p2l
│   └── libe_sqlite3.so
├── win-x64/
│   ├── p2l.exe                    Windows x64 bridge
│   ├── p2l-client.exe             Web dashboard (standalone, ~40 MB)
│   └── e_sqlite3.dll
└── client/
    ├── osx-arm64/p2l-client
    ├── linux-x64/p2l-client
    └── ...
```

---

## Native AOT Notes

### Spectre.Console reflection (`rd.xml`)

Spectre.Console.Cli uses runtime reflection to discover `Settings` types. `src/P2L.CLI/rd.xml` preserves these via `Dynamic="Required All"`. **If you add a command, add it to `rd.xml`:**

```xml
<Type Name="P2L.CLI.Commands.MyNewCommand"          Dynamic="Required All" />
<Type Name="P2L.CLI.Commands.MyNewCommand+Settings" Dynamic="Required All" />
```

### `appsettings.json` binding

`config.Get<T>()` is trimmed under AOT. P2L reads every key via string indexer (`config["Mqtt:Enabled"]`). Add new settings the same way in `Program.cs`.

### Cross-OS AOT limitation

Native AOT compilation **cannot cross OS families**:
- macOS → Linux: requires Docker (`deploy/Dockerfile.build`)
- macOS → Windows: Docker produces non-AOT exe (functional, ~80 MB vs ~15 MB AOT)
- Linux → macOS: not supported (requires macOS SDK)

---

## CI/CD — GitHub Actions

The repo ships three workflows:

| Workflow | File | Trigger |
|---|---|---|
| Build MSI | `.github/workflows/build-msi.yml` | Manual / `v*` tag |

### Trigger MSI build from any machine

```bash
# Manual trigger (any version/platform)
gh workflow run build-msi.yml -f version=1.0.0 -f platform=x64

# Tag push (auto-builds and creates GitHub Release)
git tag v1.0.0 && git push origin v1.0.0
```

---

## Troubleshooting

| Problem | Fix |
|---|---|
| `clang: invalid linker name` | Use Docker for cross-platform Linux builds |
| `p2l` exits silently (no output) | Check `crash_history.jsonl`; likely missing type in `rd.xml` |
| `e_sqlite3.dll/so/dylib not found` | Keep SQLite lib in same dir as `p2l` binary |
| macOS: "developer cannot be verified" | `xattr -d com.apple.quarantine /path/to/p2l` |
| AOT + `config.Get<T>()` returns null | Use string-key indexer instead |
| PyInstaller: no module named X | Add to `client/requirements.txt` and rebuild |
