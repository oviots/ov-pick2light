#!/usr/bin/env bash
# publish.sh — Build Pick2Light as a Native AOT single-file binary.
#
# Usage:
#   ./deploy/publish.sh                   # native platform (default)
#   ./deploy/publish.sh linux-x64
#   ./deploy/publish.sh win-x64
#   ./deploy/publish.sh osx-arm64
#   ./deploy/publish.sh osx-x64
#   ./deploy/publish.sh all               # build all RIDs (native platform only - cross-compilation not supported)
#
# NOTE: Native AOT does not support cross-OS compilation.
# Build each platform on its target OS or use Docker for Linux builds.
# For Windows: Build on Windows (PowerShell: dotnet publish -c Release -r win-x64 -p:PublishAot=true)
# For Linux:   Build on Linux or use: docker run --rm -v $(pwd):/src -w /src/src/P2L.CLI mcr.microsoft.com/dotnet/sdk:10 dotnet publish -c Release -r linux-x64 -p:PublishAot=true --output /src/artifacts/linux-x64

set -euo pipefail

DOTNET="${DOTNET_ROOT:-$HOME/.dotnet}/dotnet"
PROJECT="src/P2L.CLI/P2L.CLI.csproj"
SOLUTION_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# Detect the current OS
detect_os() {
    case "$(uname -s)" in
        Darwin)  echo "osx" ;;
        Linux)   echo "linux" ;;
        MINGW*|MSYS*) echo "win" ;;
        *)       echo "unknown" ;;
    esac
}

# Detect the current architecture
detect_arch() {
    case "$(uname -m)" in
        x86_64|amd64) echo "x64" ;;
        aarch64|arm64) echo "arm64" ;;
        *)            echo "unknown" ;;
    esac
}

build_rid() {
    local RID="$1"
    local OUT="$SOLUTION_ROOT/artifacts/$RID"

    echo ""
    echo "==> Publishing P2L for $RID..."
    
    local EXTRA_FLAGS=""
    if [[ "$RID" == linux-* ]]; then
        EXTRA_FLAGS="-p:UsePortableLinuxSharedLibraries=true"
    fi
    
    "$DOTNET" publish "$SOLUTION_ROOT/$PROJECT" \
        --configuration Release \
        --runtime "$RID" \
        --output "$OUT" \
        --self-contained true \
        -p:PublishAot=true \
        -p:InvariantGlobalization=true \
        -p:StripSymbols=true \
        $EXTRA_FLAGS

    local BIN="$OUT/p2l"
    [[ "$RID" == win-* ]] && BIN="$OUT/p2l.exe"

    echo "==> Done: $BIN"
    ls -lh "$BIN" 2>/dev/null || ls -lh "$OUT/"
}

CURRENT_OS=$(detect_os)
CURRENT_ARCH=$(detect_arch)

ARG="${1:-native}"

case "$ARG" in
    native)
        # Build for the current platform
        if [[ "$CURRENT_OS" == "osx" ]]; then
            build_rid "osx-$CURRENT_ARCH"
        elif [[ "$CURRENT_OS" == "linux" ]]; then
            build_rid "linux-$CURRENT_ARCH"
        elif [[ "$CURRENT_OS" == "win" ]]; then
            build_rid "win-$CURRENT_ARCH"
        else
            echo "Error: Unknown OS. Cannot auto-detect RID."
            exit 1
        fi
        ;;
    all)
        # Note: Cross-compilation is not supported; can only build for current OS
        echo "Warning: Cross-OS compilation is not supported by Native AOT."
        echo "Building for current OS ($CURRENT_OS) only."
        if [[ "$CURRENT_OS" == "osx" ]]; then
            for arch in x64 arm64; do
                build_rid "osx-$arch"
            done
        elif [[ "$CURRENT_OS" == "linux" ]]; then
            for arch in x64 arm64; do
                build_rid "linux-$arch"
            done
        elif [[ "$CURRENT_OS" == "win" ]]; then
            for arch in x64 arm64; do
                build_rid "win-$arch"
            done
        fi
        ;;
    *)
        # Build for specified RID
        build_rid "$ARG"
        ;;
esac
