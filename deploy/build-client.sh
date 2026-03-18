#!/usr/bin/env bash
# build-client.sh — Build P2L web client as a standalone single-file binary using PyInstaller.
#
# Usage:
#   ./deploy/build-client.sh                  # build for current platform
#   ./deploy/build-client.sh <output-dir>     # specify custom output directory
#
# Requirements:
#   Python 3.10+  (must be in PATH as python3)
#
# Output:
#   artifacts/client/<rid>/p2l-client         (Linux/macOS)
#   artifacts/client/<rid>/p2l-client.exe     (Windows)

set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
info()  { echo -e "${CYAN}[INFO]${NC}  $*" >&2; }
ok()    { echo -e "${GREEN}[OK]${NC}    $*" >&2; }
warn()  { echo -e "${YELLOW}[WARN]${NC}  $*" >&2; }
error() { echo -e "${RED}[ERROR]${NC} $*" >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
CLIENT_DIR="$ROOT/client"

# Detect RID for output directory name
detect_rid() {
    local os arch
    case "$(uname -s)" in
        Darwin)  os="osx" ;;
        Linux)   os="linux" ;;
        MINGW*|MSYS*|CYGWIN*) os="win" ;;
        *) os="unknown" ;;
    esac
    case "$(uname -m)" in
        x86_64|amd64) arch="x64" ;;
        aarch64|arm64) arch="arm64" ;;
        *) arch="unknown" ;;
    esac
    echo "$os-$arch"
}

RID="$(detect_rid)"
OUTPUT_DIR="${1:-$ROOT/artifacts/client/$RID}"

# ── Pre-flight ────────────────────────────────────────────────────────────────
[[ -d "$CLIENT_DIR" ]]         || error "client/ directory not found: $CLIENT_DIR"
[[ -f "$CLIENT_DIR/run.py" ]]  || error "client/run.py not found"
python3 --version &>/dev/null  || error "python3 not found — install Python 3.10+"

info "Building P2L client binary for $RID ..."
info "Source : $CLIENT_DIR"
info "Output : $OUTPUT_DIR"

# ── Create build venv ─────────────────────────────────────────────────────────
BUILD_VENV="$ROOT/.build-venv-client"
info "Setting up build venv at: $BUILD_VENV"
python3 -m venv "$BUILD_VENV"
PIP="$BUILD_VENV/bin/pip"
PYINSTALLER="$BUILD_VENV/bin/pyinstaller"

"$PIP" install --quiet --upgrade pip
"$PIP" install --quiet -r "$CLIENT_DIR/requirements.txt"
"$PIP" install --quiet pyinstaller

# ── Run PyInstaller ───────────────────────────────────────────────────────────
info "Running PyInstaller ..."
mkdir -p "$OUTPUT_DIR"

PYINSTALLER_WORK="$ROOT/.pyinstaller-work"
mkdir -p "$PYINSTALLER_WORK"

(
    cd "$CLIENT_DIR"
    "$PYINSTALLER" \
        --noconfirm \
        --clean \
        --onefile \
        --name p2l-client \
        --distpath "$OUTPUT_DIR" \
        --workpath "$PYINSTALLER_WORK" \
        --specpath "$PYINSTALLER_WORK" \
        --add-data "$CLIENT_DIR/templates:templates" \
        --add-data "$CLIENT_DIR/static:static" \
        --hidden-import uvicorn.logging \
        --hidden-import uvicorn.loops \
        --hidden-import uvicorn.loops.auto \
        --hidden-import uvicorn.loops.asyncio \
        --hidden-import uvicorn.protocols \
        --hidden-import uvicorn.protocols.http \
        --hidden-import uvicorn.protocols.http.auto \
        --hidden-import uvicorn.protocols.http.h11_impl \
        --hidden-import uvicorn.protocols.websockets \
        --hidden-import uvicorn.protocols.websockets.auto \
        --hidden-import uvicorn.protocols.websockets.websockets_impl \
        --hidden-import uvicorn.lifespan \
        --hidden-import uvicorn.lifespan.on \
        --hidden-import uvicorn.lifespan.off \
        --hidden-import fastapi.staticfiles \
        --hidden-import fastapi.templating \
        --hidden-import pydantic.deprecated.decorator \
        --hidden-import aiomqtt \
        --hidden-import aiosqlite \
        --hidden-import aiofiles.os \
        --hidden-import aiofiles.threadpool \
        --hidden-import websockets.legacy \
        --hidden-import websockets.legacy.server \
        --hidden-import h11 \
        --hidden-import jinja2.ext \
        --hidden-import anyio \
        --hidden-import anyio._backends._asyncio \
        --hidden-import starlette.routing \
        --hidden-import starlette.staticfiles \
        --hidden-import starlette.templating \
        --exclude-module tkinter \
        --exclude-module unittest \
        --strip \
        run.py >&2
)

# ── Verify output ─────────────────────────────────────────────────────────────
BIN="$OUTPUT_DIR/p2l-client"
[[ "$(uname -s)" == "MINGW"* ]] && BIN="$OUTPUT_DIR/p2l-client.exe"
[[ -f "$BIN" ]] || error "PyInstaller output not found: $BIN"
chmod +x "$BIN"

ok "Client binary: $BIN ($(du -sh "$BIN" | cut -f1))"

# ── Cleanup ───────────────────────────────────────────────────────────────────
rm -rf "$PYINSTALLER_WORK" "$BUILD_VENV"
info "Build complete: $BIN"

# Return path for use by package.sh via subshell
echo "$BIN"
