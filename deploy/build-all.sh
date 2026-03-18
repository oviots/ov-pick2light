#!/usr/bin/env bash
# deploy/build-all.sh — Build all P2L packages from macOS.
#
# Produces dist/ packages for all supported platforms:
#   linux-x64    .tar.gz  via Docker (--platform linux/amd64)
#   linux-arm64  .tar.gz  via Docker (--platform linux/arm64)
#   win-x64      .zip     via Docker (binaries only) + GitHub Actions for MSI
#   osx-arm64    .tar.gz  natively (must run on macOS)
#   osx-x64      .tar.gz  natively (must run on macOS)
#
# Usage:
#   ./deploy/build-all.sh              # all platforms
#   ./deploy/build-all.sh linux        # linux-x64 + linux-arm64 only
#   ./deploy/build-all.sh windows      # win-x64 binaries only
#   ./deploy/build-all.sh macos        # osx-arm64 + osx-x64 only
#   ./deploy/build-all.sh linux-x64    # single platform
#
# Requirements:
#   - Docker Desktop running (for linux-* and win-* targets)
#   - .NET SDK 10 installed (for osx-* targets)
#   - pyinstaller installed (for osx-* client binaries)

set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'
BOLD='\033[1m'; NC='\033[0m'
info()    { echo -e "${CYAN}[INFO]${NC}  $*"; }
ok()      { echo -e "${GREEN}[OK]${NC}    $*"; }
warn()    { echo -e "${YELLOW}[WARN]${NC}  $*"; }
error()   { echo -e "${RED}[ERROR]${NC} $*" >&2; exit 1; }
section() { echo -e "\n${BOLD}${CYAN}══ $* ══${NC}"; }

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
IMAGE="p2l-builder"
FILTER="${1:-all}"

# ── Prerequisite checks ───────────────────────────────────────────────────────
check_docker() {
    if ! docker info &>/dev/null; then
        error "Docker is not running. Start Docker Desktop and retry."
    fi
    ok "Docker is running"
}

check_dotnet() {
    if ! command -v dotnet &>/dev/null; then
        error ".NET SDK not found. Install from https://dot.net"
    fi
    ok "dotnet $(dotnet --version)"
}

# ── Build or verify Docker image ──────────────────────────────────────────────
build_image() {
    section "Docker image: $IMAGE"
    if docker inspect "$IMAGE" &>/dev/null; then
        info "Image '$IMAGE' already exists. Rebuilding to pick up source changes..."
    fi
    docker build --platform linux/amd64 -f "$SCRIPT_DIR/Dockerfile.build" -t "$IMAGE" "$ROOT"
    ok "Image built: $IMAGE (linux/amd64)"
}

# ── Run package.sh inside Docker ─────────────────────────────────────────────
# $1 = RID, $2 = docker --platform (linux/amd64 or linux/arm64)
docker_build() {
    local RID="$1"
    local PLATFORM="$2"
    local EXTRA_ARGS="${3:-}"   # e.g. --binaries-only

    section "Building $RID (Docker $PLATFORM)"

    # linux-arm64 needs a separate arm64 image (rebuild with correct platform)
    local img="$IMAGE"
    if [[ "$PLATFORM" == "linux/arm64" ]]; then
        img="${IMAGE}-arm64"
        if ! docker inspect "$img" &>/dev/null; then
            info "Building arm64 variant of image..."
            docker build --platform linux/arm64 \
                -f "$SCRIPT_DIR/Dockerfile.build" -t "$img" "$ROOT"
        fi
    fi

    docker run --rm \
        --platform "$PLATFORM" \
        -v "$ROOT/dist:/workspace/dist" \
        -v "$ROOT/artifacts:/workspace/artifacts" \
        "$img" \
        $RID $EXTRA_ARGS

    ok "$RID done → dist/"
}

# ── macOS native build ────────────────────────────────────────────────────────
native_build() {
    local RID="$1"
    section "Building $RID (native macOS)"
    check_dotnet
    bash "$SCRIPT_DIR/package.sh" "$RID"
    ok "$RID done → dist/"
}

# ── Platform selection ────────────────────────────────────────────────────────
run_linux_x64()  { check_docker; docker_build linux-x64  linux/amd64; }
run_linux_arm64() { check_docker; docker_build linux-arm64 linux/arm64; }
run_win_x64()    { check_docker; docker_build win-x64    linux/amd64; }
run_osx_arm64()  { native_build osx-arm64; }
run_osx_x64()    { native_build osx-x64; }

# ── Main ──────────────────────────────────────────────────────────────────────
echo ""
echo "╔════════════════════════════════════════════╗"
echo "║  P2L Cross-Platform Build — macOS          ║"
echo "╚════════════════════════════════════════════╝"
echo ""

mkdir -p "$ROOT/dist" "$ROOT/artifacts"

case "$FILTER" in
    all)
        check_docker
        build_image
        run_linux_x64
        run_linux_arm64
        run_win_x64
        run_osx_arm64
        run_osx_x64
        ;;
    linux)
        check_docker
        build_image
        run_linux_x64
        run_linux_arm64
        ;;
    linux-x64)
        check_docker
        build_image
        run_linux_x64
        ;;
    linux-arm64)
        check_docker
        build_image
        run_linux_arm64
        ;;
    windows|win|win-x64)
        check_docker
        build_image
        run_win_x64
        warn "Windows binaries only (p2l.exe + e_sqlite3.dll) — no p2l-client.exe, no MSI."
        warn "For a full MSI, run: ./deploy/build-msi.sh 1.0.0"
        ;;
    macos|osx)
        run_osx_arm64
        run_osx_x64
        ;;
    osx-arm64)
        run_osx_arm64
        ;;
    osx-x64)
        run_osx_x64
        ;;
    *)
        echo "Usage: $0 [all|linux|linux-x64|linux-arm64|windows|macos|osx-arm64|osx-x64]"
        exit 1
        ;;
esac

echo ""
echo "╔════════════════════════════════════════════╗"
echo "║  Build complete                            ║"
echo "╚════════════════════════════════════════════╝"
echo ""
echo "Output:"
ls -lh "$ROOT/dist/" 2>/dev/null | grep -v '^total' | sed 's/^/  /' || echo "  (no packages in dist/)"
echo ""

# Windows MSI reminder
if [[ "$FILTER" == "all" || "$FILTER" == "windows" || "$FILTER" == "win"* ]]; then
    echo "Windows MSI (optional):"
    echo "  From macOS:  ./deploy/build-msi.sh 1.0.0"
    echo "  On Windows:  .\\deploy\\build-msi.ps1 -Version 1.0.0"
    echo ""
fi
