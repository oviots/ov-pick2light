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
# Recommended — build all platforms on GitHub Actions (no local toolchain needed)
gh workflow run build-all.yml -f version=1.0.0 -f targets=all \
  --repo <owner>/ov-pick2light

# Linux/macOS — local native build
./deploy/package.sh linux-x64
./deploy/package.sh osx-arm64

# All platforms via Docker (Linux packages) + GitHub Actions (Windows/macOS)
./deploy/build-all.sh all
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

## Building with Docker (Linux packages, local)

Docker builds run on any host and produce Linux packages. Use GitHub Actions for Windows/macOS.

```bash
# Build the image (one-time, must use --platform linux/amd64 on Apple Silicon)
docker build --platform linux/amd64 -f deploy/Dockerfile.build -t p2l-builder .

# Linux packages (full: AOT binary + PyInstaller client + tar.gz)
docker run --rm --platform linux/amd64 \
  -v "$(pwd)/dist:/workspace/dist" \
  p2l-builder linux-x64

docker run --rm --platform linux/arm64 \
  -v "$(pwd)/dist:/workspace/dist" \
  p2l-builder linux-arm64

# Or use the master script (handles --platform flags automatically)
./deploy/build-all.sh linux-x64
./deploy/build-all.sh linux        # x64 + arm64
```

### What Docker can/cannot build

| Target | Docker (Linux) | Use instead |
|---|---|---|
| `linux-x64` full package | ✅ | — |
| `linux-arm64` full package | ✅ | — |
| `win-x64` binaries only | ✅ | `p2l.exe` + `e_sqlite3.dll` (non-AOT) |
| `win-x64` `p2l-client.exe` | ❌ | GitHub Actions `windows-x64` job |
| `win-x64` MSI | ❌ | GitHub Actions `windows-x64` job |
| `osx-*` | ❌ | GitHub Actions `osx-*` jobs or local `package.sh` |

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

### From macOS/Linux — via GitHub Actions (recommended)

```bash
# Trigger full cross-platform build
gh workflow run build-all.yml -f version=1.0.0 -f targets=windows \
  --repo <owner>/ov-pick2light

# Download MSI when done
gh run download --repo <owner>/ov-pick2light --dir dist/
```

### MSI-only via GitHub Actions

```bash
# Trigger MSI-only build
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

The repo ships two workflows:

| Workflow | File | Trigger | Description |
|---|---|---|---|
| **Build All Platforms** | `.github/workflows/build-all.yml` | Manual / `v*` tag | All platforms in parallel — **recommended** |
| Build MSI only | `.github/workflows/build-msi.yml` | Manual / `v*` tag | Windows MSI only |

### Build all platforms (recommended)

Each platform runs on its own native GitHub-hosted runner — no Docker, no local toolchain required:

| Job | Runner | Output |
|---|---|---|
| `linux-x64` | `ubuntu-24.04` | `p2l-<v>-linux-x64.tar.gz` |
| `linux-arm64` | `ubuntu-24.04-arm` | `p2l-<v>-linux-arm64.tar.gz` |
| `osx-arm64` | `macos-latest` | `p2l-<v>-osx-arm64.tar.gz` |
| `osx-x64` | `macos-13` (Intel) | `p2l-<v>-osx-x64.tar.gz` |
| `windows-x64` | `windows-latest` | `P2L-<v>-x64.msi` + `.zip` |

```bash
# Trigger manually
gh workflow run build-all.yml \
  -f version=1.0.0 \
  -f targets=all \
  --repo <owner>/ov-pick2light

# Build only Linux
gh workflow run build-all.yml -f version=1.0.0 -f targets=linux

# Build only Windows
gh workflow run build-all.yml -f version=1.0.0 -f targets=windows

# Monitor progress
gh run watch --repo <owner>/ov-pick2light

# Download all artifacts when done
gh run download --repo <owner>/ov-pick2light --dir dist/
```

### Release (auto on tag)

```bash
git tag v1.0.0
git push github v1.0.0
# → builds all platforms → creates GitHub Release with all 5 packages
```

### Trigger MSI-only build

```bash
gh workflow run build-msi.yml -f version=1.0.0 -f platform=x64
./deploy/build-msi.sh --download   # download MSI to dist/
```

---

## Troubleshooting

| Problem | Fix |
|---|---|
| `ld: library 'ssl' not found` (macOS) | `brew install openssl@3 brotli` — already handled by `package.sh` |
| `p2l` exits silently (no output) | Check `crash_history.jsonl`; likely missing type in `rd.xml` |
| `e_sqlite3.dll/so/dylib not found` | Keep SQLite lib in same dir as `p2l` binary |
| macOS: "developer cannot be verified" | `xattr -d com.apple.quarantine /path/to/p2l` |
| AOT + `config.Get<T>()` returns null | Use string-key indexer instead |
| PyInstaller: no module named X | Add to `client/requirements.txt` and rebuild |
