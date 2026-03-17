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
    dotnet publish "$PROJECT" \
        --configuration Release \
        --runtime "$RID" \
        --output "$OUT" \
        --self-contained true \
        -p:PublishAot=true \
        -p:InvariantGlobalization=true \
        -p:StripSymbols=true \
        $EXTRA >&2

    local BIN="$OUT/p2l"
    [[ "$RID" == win-* ]] && BIN="$OUT/p2l.exe"
    [[ -f "$BIN" ]] || error ".NET binary not found after publish: $BIN"
    ok "P2L binary: $BIN ($(du -sh "$BIN" | cut -f1))"
    echo "$BIN"
}

# ── Build Python client wheel ─────────────────────────────────────────────────
check_python_client() {
    [[ -d "$CLIENT_DIR/app" ]]     || error "client/app/ not found — is client/ present?"
    [[ -f "$CLIENT_DIR/run.py" ]]  || error "client/run.py not found"
    [[ -f "$CLIENT_DIR/requirements.txt" ]] || error "client/requirements.txt not found"
    ok "Python client source verified"
}

# ── Stage package ─────────────────────────────────────────────────────────────
stage_package() {
    local RID="$1"
    local BIN_PATH="$2"
    local PKG_NAME="p2l-${VERSION}-${RID}"
    local STAGE="$DIST_DIR/staging/$PKG_NAME"

    info "Staging package: $PKG_NAME ..."
    rm -rf "$STAGE"
    mkdir -p "$STAGE/bin" "$STAGE/client" "$STAGE/config" "$STAGE/deploy"

    # ── P2L binary ──────────────────────────────────────────────────────────
    local BIN_NAME="p2l"; [[ "$RID" == win-* ]] && BIN_NAME="p2l.exe"
    cp "$BIN_PATH" "$STAGE/bin/$BIN_NAME"
    chmod +x "$STAGE/bin/$BIN_NAME"

    # ── appsettings.json ─────────────────────────────────────────────────────
    local SETTINGS_SRC="$(dirname "$BIN_PATH")/appsettings.json"
    [[ ! -f "$SETTINGS_SRC" ]] && SETTINGS_SRC="$ROOT/appsettings.json"
    if [[ -f "$SETTINGS_SRC" ]]; then
        cp "$SETTINGS_SRC" "$STAGE/config/appsettings.json"
        ok "Copied appsettings.json"
    else
        warn "appsettings.json not found — using compiled-in defaults"
    fi

    # ── Python client ────────────────────────────────────────────────────────
    rsync -a --exclude='__pycache__' --exclude='*.pyc' --exclude='venv' \
        --exclude='*.db-shm' --exclude='*.db-wal' --exclude='logs' \
        "$CLIENT_DIR/app"           "$STAGE/client/"
    rsync -a --exclude='__pycache__' --exclude='*.pyc' \
        "$CLIENT_DIR/static"        "$STAGE/client/"
    rsync -a "$CLIENT_DIR/templates" "$STAGE/client/"
    cp "$CLIENT_DIR/requirements.txt" "$STAGE/client/"
    cp "$CLIENT_DIR/run.py"           "$STAGE/client/"
    [[ -f "$CLIENT_DIR/p2l_config.json" ]] && \
        cp "$CLIENT_DIR/p2l_config.json" "$STAGE/client/"
    # Seed empty DB if present
    [[ -f "$CLIENT_DIR/p2l_devices.db" ]] && \
        cp "$CLIENT_DIR/p2l_devices.db" "$STAGE/client/"

    # ── Deploy scripts ────────────────────────────────────────────────────────
    cp "$DEPLOY_DIR/p2l.service"         "$STAGE/deploy/"
    cp "$DEPLOY_DIR/p2l-client.service"  "$STAGE/deploy/"

    # Write unified install.sh (Linux/macOS) into the package
    _write_install_sh "$STAGE/deploy/install.sh" "$RID"
    chmod +x "$STAGE/deploy/install.sh"

    # Windows install script
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
# install.sh — Install P2L service + Web Client on Linux or macOS.
# Run with: sudo ./deploy/install.sh  (Linux)
#           ./deploy/install.sh        (macOS — uses launchd, no sudo needed for user agents)

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
CLIENT_DIR="$INSTALL_DIR/client"
P2L_USER="p2l"

# ── Pre-flight ────────────────────────────────────────────────────────────────
BIN_SRC="$PKG_ROOT/bin/p2l"
[[ -f "$BIN_SRC" ]] || error "Binary not found: $BIN_SRC"

python3 --version &>/dev/null || error "python3 not found. Install Python 3.10+ first."
pip3 --version   &>/dev/null || error "pip3 not found. Install pip first."

info "Installing to: $INSTALL_DIR"
info "Client at:     $CLIENT_DIR"

# ── Create system user (Linux only) ──────────────────────────────────────────
if [[ "$OS" == "Linux" ]]; then
    [[ $EUID -eq 0 ]] || error "Run as root: sudo ./deploy/install.sh"
    if ! id -u "$P2L_USER" &>/dev/null; then
        useradd --system --no-create-home --shell /usr/sbin/nologin "$P2L_USER"
        info "Created system user: $P2L_USER"
    fi
fi

# ── Install P2L binary ────────────────────────────────────────────────────────
info "Installing P2L binary ..."
mkdir -p "$INSTALL_DIR/logs" "$INSTALL_DIR/config"
install -m 0755 "$BIN_SRC" "$INSTALL_DIR/p2l"
[[ -f "$PKG_ROOT/config/appsettings.json" ]] && \
    install -m 0640 "$PKG_ROOT/config/appsettings.json" "$INSTALL_DIR/appsettings.json"
ok "P2L binary installed"

# ── Install Python client ─────────────────────────────────────────────────────
info "Installing Python web client ..."
mkdir -p "$CLIENT_DIR"
rsync -a --delete \
    --exclude='__pycache__' --exclude='*.pyc' --exclude='venv' \
    "$PKG_ROOT/client/" "$CLIENT_DIR/"

info "Creating Python virtual environment ..."
python3 -m venv "$CLIENT_DIR/venv"
"$CLIENT_DIR/venv/bin/pip" install --quiet --upgrade pip
"$CLIENT_DIR/venv/bin/pip" install --quiet -r "$CLIENT_DIR/requirements.txt"
ok "Python client installed (venv: $CLIENT_DIR/venv)"

# ── Fix permissions ───────────────────────────────────────────────────────────
if [[ "$OS" == "Linux" ]]; then
    chown -R "$P2L_USER:$P2L_USER" "$INSTALL_DIR"
    chmod 755 "$INSTALL_DIR"
fi

# ── Install services ──────────────────────────────────────────────────────────
if [[ "$OS" == "Linux" ]]; then
    _install_systemd
elif [[ "$OS" == "Darwin" ]]; then
    _install_launchd
fi

echo ""
ok "=== Installation complete ==="
echo ""
if [[ "$OS" == "Linux" ]]; then
    echo "  P2L bridge  : sudo systemctl start p2l"
    echo "  Web client  : sudo systemctl start p2l-client"
    echo "  Dashboard   : http://localhost:8765"
    echo "  Logs bridge : journalctl -u p2l -f"
    echo "  Logs client : journalctl -u p2l-client -f"
elif [[ "$OS" == "Darwin" ]]; then
    echo "  P2L bridge  : launchctl start com.p2l.service"
    echo "  Web client  : launchctl start com.p2l.client"
    echo "  Dashboard   : http://localhost:8765"
    echo "  Logs bridge : tail -f /var/log/p2l.log"
    echo "  Logs client : tail -f /var/log/p2l-client.log"
fi

# ── systemd installer ─────────────────────────────────────────────────────────
_install_systemd() {
    info "Installing systemd units ..."
    local SVCDIR="$SCRIPT_DIR"

    # Patch WorkingDirectory into service files
    sed "s|WorkingDirectory=.*|WorkingDirectory=$INSTALL_DIR|" \
        "$SVCDIR/p2l.service" | \
    sed "s|ExecStart=.*p2l run.*|ExecStart=$INSTALL_DIR/p2l run $INSTALL_DIR/tasks.csv|" \
        > /etc/systemd/system/p2l.service

    sed "s|WorkingDirectory=.*|WorkingDirectory=$CLIENT_DIR|" \
        "$SVCDIR/p2l-client.service" | \
    sed "s|ExecStart=.*python.*|ExecStart=$CLIENT_DIR/venv/bin/python $CLIENT_DIR/run.py|" \
        > /etc/systemd/system/p2l-client.service

    systemctl daemon-reload
    systemctl enable p2l p2l-client
    ok "systemd units installed and enabled"

    read -r -p "Start services now? [y/N] " START
    if [[ "${START,,}" == "y" ]]; then
        systemctl start p2l
        sleep 1
        systemctl start p2l-client
        systemctl status p2l --no-pager
        systemctl status p2l-client --no-pager
    fi
}

# ── launchd installer (macOS) ─────────────────────────────────────────────────
_install_launchd() {
    info "Installing launchd plists (macOS) ..."
    local LAUNCH_DIR="$HOME/Library/LaunchAgents"
    mkdir -p "$LAUNCH_DIR"

    cat > "$LAUNCH_DIR/com.p2l.service.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>             <string>com.p2l.service</string>
    <key>ProgramArguments</key>
    <array>
        <string>$INSTALL_DIR/p2l</string>
        <string>run</string>
        <string>$INSTALL_DIR/tasks.csv</string>
    </array>
    <key>WorkingDirectory</key>  <string>$INSTALL_DIR</string>
    <key>RunAtLoad</key>         <true/>
    <key>KeepAlive</key>         <true/>
    <key>StandardOutPath</key>   <string>/var/log/p2l.log</string>
    <key>StandardErrorPath</key> <string>/var/log/p2l.log</string>
</dict>
</plist>
PLIST

    cat > "$LAUNCH_DIR/com.p2l.client.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>             <string>com.p2l.client</string>
    <key>ProgramArguments</key>
    <array>
        <string>$CLIENT_DIR/venv/bin/python</string>
        <string>$CLIENT_DIR/run.py</string>
    </array>
    <key>WorkingDirectory</key>  <string>$CLIENT_DIR</string>
    <key>EnvironmentVariables</key>
    <dict>
        <key>P2L_CLIENT_HOST</key> <string>0.0.0.0</string>
        <key>P2L_CLIENT_PORT</key> <string>8765</string>
    </dict>
    <key>RunAtLoad</key>         <true/>
    <key>KeepAlive</key>         <true/>
    <key>StandardOutPath</key>   <string>/var/log/p2l-client.log</string>
    <key>StandardErrorPath</key> <string>/var/log/p2l-client.log</string>
</dict>
</plist>
PLIST

    launchctl load "$LAUNCH_DIR/com.p2l.service.plist" 2>/dev/null || true
    launchctl load "$LAUNCH_DIR/com.p2l.client.plist"  2>/dev/null || true
    ok "launchd agents installed"
}
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
  client/                 Python web dashboard (FastAPI)
  config/appsettings.json P2L bridge configuration
  deploy/                 Installation scripts & systemd units

REQUIREMENTS
  Python 3.10+  (for web client)
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

  Edit client/p2l_config.json for web dashboard settings.

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

# ── Main ──────────────────────────────────────────────────────────────────────
main() {
    local RID="${1:-native}"
    [[ "$RID" == "native" ]] && RID="$(detect_native_rid)"

    echo ""
    echo "╔══════════════════════════════════════════╗"
    echo "║  P2L Package Builder  v${VERSION}          "
    echo "║  Target: $RID"
    echo "╚══════════════════════════════════════════╝"
    echo ""

    # Verify client source
    check_python_client

    # Build .NET binary
    local BIN_PATH
    BIN_PATH="$(build_dotnet "$RID")"

    # Stage
    local STAGE
    STAGE="$(stage_package "$RID" "$BIN_PATH")"
    ok "Staged: $STAGE"

    # Archive
    create_archive "$STAGE" "$RID"

    # Clean staging
    rm -rf "$(dirname "$STAGE")"

    echo ""
    ok "Package ready in: $DIST_DIR/"
    ls -lh "$DIST_DIR/"
}

main "${1:-native}"
