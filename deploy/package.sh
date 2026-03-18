#!/usr/bin/env bash
# package.sh — Build and package P2L service + Python web client into a distributable archive.
#
# Usage:
#   ./deploy/package.sh                    # native platform (auto-detect)
#   ./deploy/package.sh linux-x64
#   ./deploy/package.sh linux-arm64
#   ./deploy/package.sh osx-arm64
#   ./deploy/package.sh osx-x64
#   ./deploy/package.sh win-x64
#
# Output (per RID):
#   dist/p2l-<version>-<rid>.tar.gz        (Linux / macOS)
#   dist/p2l-<version>-<rid>.zip           (Windows)
#
# Package layout:
#   p2l-<version>-<rid>/
#   ├── bin/
#   │   └── p2l            (p2l.exe on Windows)
#   ├── client/
#   │   ├── app/
#   │   ├── static/
#   │   ├── templates/
#   │   ├── requirements.txt
#   │   ├── run.py
#   │   └── p2l_config.json
#   ├── config/
#   │   └── appsettings.json
#   ├── deploy/
#   │   ├── install.sh             (Linux/macOS unified installer)
#   │   ├── install.ps1            (Windows installer)
#   │   ├── p2l.service            (systemd unit — P2L bridge)
#   │   └── p2l-client.service     (systemd unit — web dashboard)
#   └── README.txt

set -euo pipefail

# ── Colour helpers ────────────────────────────────────────────────────────────
# All log helpers write to stderr so they don't pollute $(...) captures
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
info()  { echo -e "${CYAN}[INFO]${NC}  $*" >&2; }
ok()    { echo -e "${GREEN}[OK]${NC}    $*" >&2; }
warn()  { echo -e "${YELLOW}[WARN]${NC}  $*" >&2; }
error() { echo -e "${RED}[ERROR]${NC} $*" >&2; exit 1; }

# ── Paths ─────────────────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$ROOT/src/P2L.CLI/P2L.CLI.csproj"
CLIENT_DIR="$ROOT/client"
DEPLOY_DIR="$ROOT/deploy"
DIST_DIR="$ROOT/dist"
ARTIFACTS_DIR="$ROOT/artifacts"

# Read version from global.json / csproj or default
VERSION="${P2L_VERSION:-$(grep -oP '(?<="version": ")[^"]+' "$ROOT/global.json" 2>/dev/null || echo "1.0.0")}"

# ── Platform detection ────────────────────────────────────────────────────────
detect_native_rid() {
    local os arch
    case "$(uname -s)" in
        Darwin)  os="osx" ;;
        Linux)   os="linux" ;;
        MINGW*|MSYS*|CYGWIN*) os="win" ;;
        *) error "Unsupported OS: $(uname -s)" ;;
    esac
    case "$(uname -m)" in
        x86_64|amd64) arch="x64" ;;
        aarch64|arm64) arch="arm64" ;;
        *) error "Unsupported arch: $(uname -m)" ;;
    esac
    echo "$os-$arch"
}

# ── Build .NET binary ─────────────────────────────────────────────────────────
build_dotnet() {
    local RID="$1"
    local OUT="$ARTIFACTS_DIR/$RID"
    local EXTRA=""
    [[ "$RID" == linux-* ]] && EXTRA="-p:UsePortableLinuxSharedLibraries=true"

    info "Building P2L (.NET AOT) for $RID ..."
    mkdir -p "$OUT"

    # On macOS, AOT linking needs OpenSSL + Brotli from Homebrew (not in system path)
    if [[ "$(uname -s)" == Darwin && "$RID" == osx-* ]]; then
        local _extra_libs=""
        for _pkg in openssl@3 openssl brotli; do
            local _prefix
            _prefix="$(brew --prefix "$_pkg" 2>/dev/null || true)"
            if [[ -n "$_prefix" && -d "$_prefix/lib" ]]; then
                _extra_libs="${_extra_libs}${_extra_libs:+:}${_prefix}/lib"
            fi
        done
        if [[ -n "$_extra_libs" ]]; then
            export LIBRARY_PATH="${_extra_libs}${LIBRARY_PATH:+:${LIBRARY_PATH}}"
            info "LIBRARY_PATH set: $LIBRARY_PATH"
        else
            warn "OpenSSL/Brotli not found — install with: brew install openssl@3 brotli"
        fi
        # osx-x64 AOT on Apple Silicon (arm64 host) is not supported
        if [[ "$RID" == osx-x64 && "$(uname -m)" == arm64 ]]; then
            warn "osx-x64 AOT is not supported on Apple Silicon. Falling back to non-AOT self-contained."
            EXTRA="-p:PublishAot=false"
        fi
    fi

    # Capture exit code explicitly — >&2 alone can mask failures in some shells
    local RC=0
    dotnet publish "$PROJECT" \
        --configuration Release \
        --runtime "$RID" \
        --output "$OUT" \
        --self-contained true \
        -p:PublishAot=true \
        -p:InvariantGlobalization=true \
        -p:StripSymbols=true \
        $EXTRA 2>&1 | tee /tmp/p2l-publish.log >&2 || RC=$?

    if [[ $RC -ne 0 ]]; then
        error "dotnet publish failed (exit $RC) for $RID. Last 20 lines:
$(tail -20 /tmp/p2l-publish.log)"
    fi

    local BIN="$OUT/p2l"
    [[ "$RID" == win-* ]] && BIN="$OUT/p2l.exe"
    [[ -f "$BIN" ]] || error ".NET binary not found after publish: $BIN"
    ok "P2L binary: $BIN ($(du -sh "$BIN" | cut -f1))"
    echo "$BIN"
}

# ── Build Python client binary (PyInstaller) ─────────────────────────────────
build_client() {
    local RID="$1"
    local CLIENT_BIN_DIR="$ARTIFACTS_DIR/client/$RID"
    info "Building P2L client (PyInstaller) for $RID ..."
    # build-client.sh echoes the binary path to stdout; all logs go to stderr
    local BIN
    BIN="$(bash "$DEPLOY_DIR/build-client.sh" "$CLIENT_BIN_DIR")"
    [[ -f "$BIN" ]] || error "Client binary not found: $BIN"
    ok "Client binary: $BIN ($(du -sh "$BIN" | cut -f1))"
    echo "$BIN"
}

# ── Stage package ─────────────────────────────────────────────────────────────
stage_package() {
    local RID="$1"
    local P2L_BIN="$2"
    local CLIENT_BIN="$3"
    local PKG_NAME="p2l-${VERSION}-${RID}"
    local STAGE="$DIST_DIR/staging/$PKG_NAME"

    info "Staging package: $PKG_NAME ..."
    rm -rf "$STAGE"
    mkdir -p "$STAGE/bin" "$STAGE/config" "$STAGE/deploy"

    # ── P2L bridge binary ────────────────────────────────────────────────────
    local BIN_NAME="p2l"; [[ "$RID" == win-* ]] && BIN_NAME="p2l.exe"
    cp "$P2L_BIN" "$STAGE/bin/$BIN_NAME"
    chmod +x "$STAGE/bin/$BIN_NAME"

    # ── SQLite native library (required at runtime alongside p2l) ────────────
    local ARTIFACT_DIR; ARTIFACT_DIR="$(dirname "$P2L_BIN")"
    local SQLITE_LIB=""
    for pattern in "libe_sqlite3.dylib" "libe_sqlite3.so" "e_sqlite3.dll"; do
        if [[ -f "$ARTIFACT_DIR/$pattern" ]]; then
            SQLITE_LIB="$ARTIFACT_DIR/$pattern"
            break
        fi
    done
    if [[ -n "$SQLITE_LIB" ]]; then
        cp "$SQLITE_LIB" "$STAGE/bin/"
        ok "Bundled SQLite native lib: $(basename "$SQLITE_LIB")"
    else
        warn "SQLite native lib not found in $ARTIFACT_DIR — p2l may fail at runtime!"
    fi

    # ── P2L client binary ────────────────────────────────────────────────────
    local CLIENT_NAME="p2l-client"; [[ "$RID" == win-* ]] && CLIENT_NAME="p2l-client.exe"
    cp "$CLIENT_BIN" "$STAGE/bin/$CLIENT_NAME"
    chmod +x "$STAGE/bin/$CLIENT_NAME"

    # ── appsettings.json — use template (no credentials) ─────────────────────
    local SETTINGS_SRC="$DEPLOY_DIR/appsettings.template.json"
    [[ ! -f "$SETTINGS_SRC" ]] && SETTINGS_SRC="$ROOT/appsettings.json"
    if [[ -f "$SETTINGS_SRC" ]]; then
        cp "$SETTINGS_SRC" "$STAGE/config/appsettings.json"
        ok "Copied appsettings.json"
    else
        warn "appsettings.json not found — using compiled-in defaults"
    fi

    # ── Deploy scripts ────────────────────────────────────────────────────────
    cp "$DEPLOY_DIR/p2l.service"         "$STAGE/deploy/"
    cp "$DEPLOY_DIR/p2l-client.service"  "$STAGE/deploy/"
    _write_install_sh "$STAGE/deploy/install.sh" "$RID"
    chmod +x "$STAGE/deploy/install.sh"
    if [[ "$RID" == win-* ]]; then
        cp "$DEPLOY_DIR/install-windows.ps1" "$STAGE/deploy/install.ps1" 2>/dev/null || true
    fi

    # ── README ───────────────────────────────────────────────────────────────
    _write_readme "$STAGE/README.txt" "$RID" "$PKG_NAME"

    echo "$STAGE"
}

# ── Write embedded install.sh ─────────────────────────────────────────────────
_write_install_sh() {
    local OUT="$1"; local RID="$2"
    cat > "$OUT" <<'INSTALL_EOF'
#!/usr/bin/env bash
# install.sh — Install P2L service + Web Client binaries on Linux or macOS.
# No Python runtime required on the target machine.
#
# Linux  : sudo ./deploy/install.sh
# macOS  : ./deploy/install.sh

set -euo pipefail
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
info()  { echo -e "${CYAN}[INFO]${NC}  $*"; }
ok()    { echo -e "${GREEN}[OK]${NC}    $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC}  $*"; }
error() { echo -e "${RED}[ERROR]${NC} $*" >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PKG_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
OS="$(uname -s)"
INSTALL_DIR="/opt/p2l"
P2L_USER="p2l"

# ── Helper: install systemd units ────────────────────────────────────────────
_install_systemd() {
    info "Installing systemd units ..."
    cat > /etc/systemd/system/p2l.service <<UNIT
[Unit]
Description=Pick2Light (P2L) Warehouse Control Daemon
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=${P2L_USER}
Group=${P2L_USER}
WorkingDirectory=${INSTALL_DIR}
ExecStart=${INSTALL_DIR}/p2l run ${INSTALL_DIR}/tasks.csv
Environment=LD_LIBRARY_PATH=${INSTALL_DIR}
KillSignal=SIGTERM
TimeoutStopSec=20s
Restart=always
RestartSec=5s
StandardOutput=journal
StandardError=journal
SyslogIdentifier=p2l
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=${INSTALL_DIR}/logs ${INSTALL_DIR}

[Install]
WantedBy=multi-user.target
UNIT

    cat > /etc/systemd/system/p2l-client.service <<UNIT
[Unit]
Description=Pick2Light Web Dashboard (P2L Client)
After=network-online.target p2l.service
Wants=network-online.target

[Service]
Type=simple
User=${P2L_USER}
Group=${P2L_USER}
WorkingDirectory=${INSTALL_DIR}
ExecStart=${INSTALL_DIR}/p2l-client
Environment=P2L_CLIENT_HOST=0.0.0.0
Environment=P2L_CLIENT_PORT=8765
KillSignal=SIGTERM
TimeoutStopSec=15s
Restart=always
RestartSec=5s
StandardOutput=journal
StandardError=journal
SyslogIdentifier=p2l-client
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=${INSTALL_DIR}

[Install]
WantedBy=multi-user.target
UNIT

    systemctl daemon-reload
    systemctl enable p2l p2l-client
    ok "systemd units enabled"

    read -r -p "Start services now? [y/N] " START
    if [[ "${START,,}" == "y" ]]; then
        systemctl start p2l; sleep 1
        systemctl start p2l-client; sleep 1
        systemctl status p2l --no-pager
        systemctl status p2l-client --no-pager
    fi
}

# ── Helper: install launchd agents ────────────────────────────────────────────
_install_launchd() {
    info "Installing launchd agents (macOS) ..."
    local LOG_DIR="$HOME/Library/Logs/P2L"
    local LAUNCH_DIR="$HOME/Library/LaunchAgents"
    mkdir -p "$LOG_DIR" "$LAUNCH_DIR"

    cat > "$LAUNCH_DIR/com.p2l.service.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
    <key>Label</key>             <string>com.p2l.service</string>
    <key>ProgramArguments</key>  <array><string>${INSTALL_DIR}/p2l</string><string>run</string><string>${INSTALL_DIR}/tasks.csv</string></array>
    <key>WorkingDirectory</key>  <string>${INSTALL_DIR}</string>
    <key>EnvironmentVariables</key><dict><key>DYLD_LIBRARY_PATH</key><string>${INSTALL_DIR}</string></dict>
    <key>RunAtLoad</key>         <true/>
    <key>KeepAlive</key>         <dict><key>SuccessfulExit</key><false/></dict>
    <key>ThrottleInterval</key>  <integer>5</integer>
    <key>StandardOutPath</key>   <string>${LOG_DIR}/p2l.log</string>
    <key>StandardErrorPath</key> <string>${LOG_DIR}/p2l.log</string>
</dict></plist>
PLIST

    cat > "$LAUNCH_DIR/com.p2l.client.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
    <key>Label</key>             <string>com.p2l.client</string>
    <key>ProgramArguments</key>  <array><string>${INSTALL_DIR}/p2l-client</string></array>
    <key>WorkingDirectory</key>  <string>${INSTALL_DIR}</string>
    <key>EnvironmentVariables</key><dict>
        <key>P2L_CLIENT_HOST</key><string>0.0.0.0</string>
        <key>P2L_CLIENT_PORT</key><string>8765</string>
    </dict>
    <key>RunAtLoad</key>         <true/>
    <key>KeepAlive</key>         <dict><key>SuccessfulExit</key><false/></dict>
    <key>ThrottleInterval</key>  <integer>5</integer>
    <key>StandardOutPath</key>   <string>${LOG_DIR}/p2l-client.log</string>
    <key>StandardErrorPath</key> <string>${LOG_DIR}/p2l-client.log</string>
</dict></plist>
PLIST

    launchctl unload "$LAUNCH_DIR/com.p2l.service.plist" 2>/dev/null || true
    launchctl unload "$LAUNCH_DIR/com.p2l.client.plist"  2>/dev/null || true
    launchctl load   "$LAUNCH_DIR/com.p2l.service.plist"
    launchctl load   "$LAUNCH_DIR/com.p2l.client.plist"
    ok "launchd agents installed and loaded"
}

# ── Pre-flight ────────────────────────────────────────────────────────────────
[[ -f "$PKG_ROOT/bin/p2l" ]]        || error "Binary not found: $PKG_ROOT/bin/p2l"
[[ -f "$PKG_ROOT/bin/p2l-client" ]] || error "Client binary not found: $PKG_ROOT/bin/p2l-client"

if [[ "$OS" == "Linux" ]]; then
    [[ $EUID -eq 0 ]] || error "Run as root: sudo ./deploy/install.sh"
fi

info "Installing to: $INSTALL_DIR"

# ── Create system user (Linux only) ──────────────────────────────────────────
if [[ "$OS" == "Linux" ]]; then
    if ! id -u "$P2L_USER" &>/dev/null; then
        useradd --system --no-create-home --shell /usr/sbin/nologin "$P2L_USER"
        info "Created system user: $P2L_USER"
    fi
fi

# ── Install binaries + SQLite native lib ─────────────────────────────────────
info "Installing binaries ..."
mkdir -p "$INSTALL_DIR/logs"
install -m 0755 "$PKG_ROOT/bin/p2l"        "$INSTALL_DIR/p2l"
install -m 0755 "$PKG_ROOT/bin/p2l-client" "$INSTALL_DIR/p2l-client"

for sqlite_lib in "$PKG_ROOT/bin/"libe_sqlite3.* "$PKG_ROOT/bin/"e_sqlite3.*; do
    [[ -f "$sqlite_lib" ]] && install -m 0644 "$sqlite_lib" "$INSTALL_DIR/" && ok "Installed $(basename "$sqlite_lib")"
done

[[ -f "$PKG_ROOT/config/appsettings.json" ]] && \
    install -m 0640 "$PKG_ROOT/config/appsettings.json" "$INSTALL_DIR/appsettings.json"
ok "Binaries installed"

# ── Fix permissions (Linux) ───────────────────────────────────────────────────
if [[ "$OS" == "Linux" ]]; then
    chown -R "$P2L_USER:$P2L_USER" "$INSTALL_DIR"
    chmod 755 "$INSTALL_DIR"
fi

# ── Install services ──────────────────────────────────────────────────────────
if [[ "$OS" == "Linux" ]]; then
    _install_systemd
elif [[ "$OS" == "Darwin" ]]; then
    INSTALL_DIR="$HOME/Library/Application Support/P2L"
    mkdir -p "$INSTALL_DIR/logs"
    install -m 0755 "$PKG_ROOT/bin/p2l"        "$INSTALL_DIR/p2l"
    install -m 0755 "$PKG_ROOT/bin/p2l-client" "$INSTALL_DIR/p2l-client"
    for sqlite_lib in "$PKG_ROOT/bin/"libe_sqlite3.* "$PKG_ROOT/bin/"e_sqlite3.*; do
        [[ -f "$sqlite_lib" ]] && install -m 0644 "$sqlite_lib" "$INSTALL_DIR/"
    done
    [[ -f "$PKG_ROOT/config/appsettings.json" ]] && \
        install -m 0640 "$PKG_ROOT/config/appsettings.json" "$INSTALL_DIR/appsettings.json"
    _install_launchd
fi

echo ""
ok "=== Installation complete ==="
if [[ "$OS" == "Linux" ]]; then
    echo "  Start bridge  : sudo systemctl start p2l"
    echo "  Start client  : sudo systemctl start p2l-client"
    echo "  Dashboard     : http://localhost:8765"
    echo "  Logs bridge   : journalctl -u p2l -f"
    echo "  Logs client   : journalctl -u p2l-client -f"
elif [[ "$OS" == "Darwin" ]]; then
    echo "  Dashboard     : http://localhost:8765"
    echo "  Logs bridge   : tail -f ~/Library/Logs/P2L/p2l.log"
    echo "  Logs client   : tail -f ~/Library/Logs/P2L/p2l-client.log"
    echo "  Stop bridge   : launchctl unload ~/Library/LaunchAgents/com.p2l.service.plist"
    echo "  Stop client   : launchctl unload ~/Library/LaunchAgents/com.p2l.client.plist"
fi
INSTALL_EOF
}

# ── Write README ──────────────────────────────────────────────────────────────
_write_readme() {
    local OUT="$1"; local RID="$2"; local PKG_NAME="$3"
    cat > "$OUT" <<EOF
Pick2Light (P2L) — $PKG_NAME
=====================================

CONTENTS
  bin/p2l                 P2L .NET bridge service (Aioi gateway ↔ MQTT)
  bin/p2l-client          P2L web dashboard — standalone binary (no Python required)
  config/appsettings.json P2L bridge configuration
  deploy/                 Installation scripts & systemd units

REQUIREMENTS
  No Python runtime required — p2l-client is self-contained.
  MQTT broker   (e.g. EMQX)

INSTALL — Linux / macOS
  sudo ./deploy/install.sh        # Linux (systemd)
  ./deploy/install.sh             # macOS (launchd)

INSTALL — Windows
  # Run PowerShell as Administrator:
  .\\deploy\\install.ps1

CONFIGURATION
  Edit config/appsettings.json before installing, or set environment variables:
    P2L_GATEWAY__HOST=192.168.1.254
    P2L_GATEWAY__PORT=5003
    P2L_MQTT__HOST=your-broker
    P2L_MQTT__USERNAME=user
    P2L_MQTT__PASSWORD=pass

  p2l_config.json is created next to p2l-client on first run.

DASHBOARD
  http://localhost:8765  (after services are running)

LOGS
  Linux   : journalctl -u p2l -f  |  journalctl -u p2l-client -f
  macOS   : tail -f /var/log/p2l.log  |  tail -f /var/log/p2l-client.log
  Windows : C:\\ProgramData\\P2L\\logs\\

MORE
  See https://github.com/your-org/p2l for documentation.
EOF
}

# ── Archive ───────────────────────────────────────────────────────────────────
create_archive() {
    local STAGE="$1"; local RID="$2"
    local PKG_NAME="$(basename "$STAGE")"
    mkdir -p "$DIST_DIR"

    if [[ "$RID" == win-* ]]; then
        local ZIP="$DIST_DIR/${PKG_NAME}.zip"
        info "Creating archive: $ZIP"
        (cd "$(dirname "$STAGE")" && zip -qr "$ZIP" "$PKG_NAME")
        ok "Archive: $ZIP ($(du -sh "$ZIP" | cut -f1))"
    else
        local TGZ="$DIST_DIR/${PKG_NAME}.tar.gz"
        info "Creating archive: $TGZ"
        tar -czf "$TGZ" -C "$(dirname "$STAGE")" "$PKG_NAME"
        ok "Archive: $TGZ ($(du -sh "$TGZ" | cut -f1))"
    fi
}

# ── Prepare Windows artifacts (binaries only, no client, no archive) ──────────
# Used when building inside Docker on Linux to cross-compile win-x64 binaries.
# Output: artifacts/win-x64/  (p2l.exe + e_sqlite3.dll — ready for MSI build)
prepare_win_artifacts() {
    local RID="$1"
    local OUT="$ARTIFACTS_DIR/$RID"
    mkdir -p "$OUT"

    info "Cross-compiling P2L for $RID (non-AOT, self-contained) ..."
    dotnet publish "$PROJECT" \
        --configuration Release \
        --runtime "$RID" \
        --output "$OUT" \
        --self-contained true \
        -p:PublishAot=false \
        -p:PublishSingleFile=false \
        -p:InvariantGlobalization=true >&2

    local BIN="$OUT/p2l.exe"
    [[ -f "$BIN" ]] || error ".NET binary not found: $BIN"
    ok "p2l.exe: $(du -sh "$BIN" | cut -f1)"

    for dll in e_sqlite3.dll; do
        [[ -f "$OUT/$dll" ]] && ok "$dll found ✓" || warn "$dll NOT found — SQLite will fail at runtime"
    done

    # Copy appsettings template for MSI packaging
    local TMPL="$DEPLOY_DIR/appsettings.template.json"
    [[ -f "$TMPL" ]] && cp "$TMPL" "$OUT/appsettings.json"

    echo ""
    ok "Windows artifacts ready in: $OUT"
    echo "  Contents:"
    ls -lh "$OUT/" | grep -v '^total' | sed 's/^/    /' >&2
    echo ""
    warn "p2l-client.exe NOT built — PyInstaller requires Windows."
    warn "To build p2l-client.exe, run on Windows: pyinstaller --onefile --name p2l-client --distpath artifacts\\$RID client\\main.py"
    warn "Then build MSI: .\\deploy\\build-msi.ps1 -SkipDotnetPublish -Platform x64"
    echo ""
    echo "Or trigger full build via GitHub Actions from any OS:"
    echo "  ./deploy/build-msi.sh 1.0.0"
}

# ── Main ──────────────────────────────────────────────────────────────────────
main() {
    local RID="${1:-native}"
    local BINARIES_ONLY=false

    # Parse --binaries-only flag (used by Docker for Windows cross-compile)
    for arg in "$@"; do
        [[ "$arg" == "--binaries-only" ]] && BINARIES_ONLY=true
    done
    # Strip flags from RID
    [[ "$RID" == --* ]] && RID="native"
    [[ "$RID" == "native" ]] && RID="$(detect_native_rid)"

    echo ""
    echo "╔══════════════════════════════════════════╗"
    echo "║  P2L Package Builder  v${VERSION}          "
    echo "║  Target: $RID"
    [[ "$BINARIES_ONLY" == true ]] && echo "║  Mode: binaries-only (no client, no MSI)"
    echo "╚══════════════════════════════════════════╝"
    echo ""

    # Windows binaries-only mode (Docker cross-compile from Linux)
    if [[ "$BINARIES_ONLY" == true ]] || [[ "$RID" == win-* && "$(uname -s)" == "Linux" ]]; then
        prepare_win_artifacts "$RID"
        return
    fi

    # Build .NET binary
    local P2L_BIN
    P2L_BIN="$(build_dotnet "$RID")"

    # Build client binary (PyInstaller)
    local CLIENT_BIN
    CLIENT_BIN="$(build_client "$RID")"

    # Stage
    local STAGE
    STAGE="$(stage_package "$RID" "$P2L_BIN" "$CLIENT_BIN")"
    ok "Staged: $STAGE"

    # Archive
    create_archive "$STAGE" "$RID"

    # Clean staging
    rm -rf "$(dirname "$STAGE")"

    echo ""
    ok "Package ready in: $DIST_DIR/"
    ls -lh "$DIST_DIR/"
}

main "$@"
