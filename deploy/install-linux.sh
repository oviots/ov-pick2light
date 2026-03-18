#!/usr/bin/env bash
# install-linux.sh — Install P2L service + web client as systemd services on Linux.
# Requires the pre-built package (bin/p2l + bin/p2l-client — no Python runtime needed).
#
# Usage (run as root or with sudo):
#   sudo ./deploy/install-linux.sh [package-dir]
#
#   package-dir defaults to the directory above this script (the extracted package root).

set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
info()  { echo -e "${CYAN}[INFO]${NC}  $*"; }
ok()    { echo -e "${GREEN}[OK]${NC}    $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC}  $*"; }
error() { echo -e "${RED}[ERROR]${NC} $*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || error "Run as root: sudo $0"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PKG_ROOT="${1:-$(cd "$SCRIPT_DIR/.." && pwd)}"
INSTALL_DIR="/opt/p2l"
P2L_USER="p2l"

# ── Pre-flight ────────────────────────────────────────────────────────────────
[[ -f "$PKG_ROOT/bin/p2l" ]]        || error "Not found: $PKG_ROOT/bin/p2l"
[[ -f "$PKG_ROOT/bin/p2l-client" ]] || error "Not found: $PKG_ROOT/bin/p2l-client"

info "Package root : $PKG_ROOT"
info "Install dir  : $INSTALL_DIR"
echo ""

# ── 1. System user ────────────────────────────────────────────────────────────
if ! id -u "$P2L_USER" &>/dev/null; then
    useradd --system --no-create-home --shell /usr/sbin/nologin "$P2L_USER"
    ok "Created system user: $P2L_USER"
else
    ok "System user '$P2L_USER' already exists"
fi

# ── 2. Install binaries ───────────────────────────────────────────────────────
info "Installing binaries ..."
mkdir -p "$INSTALL_DIR/logs"
install -m 0755 "$PKG_ROOT/bin/p2l"        "$INSTALL_DIR/p2l"
install -m 0755 "$PKG_ROOT/bin/p2l-client" "$INSTALL_DIR/p2l-client"
ok "Installed p2l and p2l-client"

# SQLite native library — required by p2l at runtime
SQLITE_INSTALLED=false
for lib in "$PKG_ROOT/bin/"libe_sqlite3.* "$PKG_ROOT/bin/"e_sqlite3.*; do
    if [[ -f "$lib" ]]; then
        install -m 0644 "$lib" "$INSTALL_DIR/"
        ok "Installed SQLite lib: $(basename "$lib")"
        SQLITE_INSTALLED=true
    fi
done
$SQLITE_INSTALLED || warn "SQLite native lib not found — p2l may fail at runtime"

# appsettings.json — skip if already customised
if [[ -f "$PKG_ROOT/config/appsettings.json" ]]; then
    if [[ -f "$INSTALL_DIR/appsettings.json" ]]; then
        warn "appsettings.json exists — keeping existing config"
    else
        install -m 0640 "$PKG_ROOT/config/appsettings.json" "$INSTALL_DIR/appsettings.json"
        ok "Installed appsettings.json  <- edit before starting the service"
    fi
fi

# ── 3. Permissions ────────────────────────────────────────────────────────────
chown -R "$P2L_USER:$P2L_USER" "$INSTALL_DIR"
chmod 755 "$INSTALL_DIR"
chmod 750 "$INSTALL_DIR/logs"
ok "Permissions set"

# ── 4. Systemd units ──────────────────────────────────────────────────────────
info "Writing systemd units ..."

cat > /etc/systemd/system/p2l.service << UNIT
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

cat > /etc/systemd/system/p2l-client.service << UNIT
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

# ── 5. Optionally start now ───────────────────────────────────────────────────
echo ""
read -r -p "Start both services now? [y/N] " START_NOW
if [[ "${START_NOW,,}" == "y" ]]; then
    systemctl start p2l; sleep 1
    systemctl start p2l-client; sleep 1
    systemctl status p2l --no-pager
    systemctl status p2l-client --no-pager
else
    info "Not started. When ready:"
    echo "    sudo systemctl start p2l"
    echo "    sudo systemctl start p2l-client"
fi

echo ""
ok "=== Installation complete ==="
echo "  Dashboard     : http://localhost:8765"
echo "  Config        : $INSTALL_DIR/appsettings.json"
echo "  Logs (bridge) : journalctl -u p2l -f"
echo "  Logs (client) : journalctl -u p2l-client -f"
