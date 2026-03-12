#!/usr/bin/env bash
# install-linux.sh — Install P2L as a systemd service on Linux.
#
# Usage (run as root or with sudo):
#   sudo ./deploy/install-linux.sh [path/to/p2l-binary] [csv-task-file]
#
# Defaults:
#   binary      : ./artifacts/linux-x64/p2l  (or linux-arm64 if on ARM)
#   csv-task    : /opt/p2l/tasks.csv
#
# What this script does:
#   1. Creates system user/group "p2l"
#   2. Copies binary + config to /opt/p2l
#   3. Installs systemd unit and enables it to start on boot
#   4. Optionally starts the service immediately

set -euo pipefail

# ── Colour helpers ──────────────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
info()  { echo -e "${GREEN}[INFO]${NC}  $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC}  $*"; }
error() { echo -e "${RED}[ERROR]${NC} $*" >&2; exit 1; }

# ── Root check ───────────────────────────────────────────────────────────────
[[ $EUID -eq 0 ]] || error "Run this script with sudo."

# ── Detect architecture ──────────────────────────────────────────────────────
ARCH=$(uname -m)
case "$ARCH" in
    x86_64)  DEFAULT_RID="linux-x64"   ;;
    aarch64) DEFAULT_RID="linux-arm64" ;;
    *)       DEFAULT_RID="linux-x64"   ;;
esac

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ARTIFACTS="$SCRIPT_DIR/../artifacts/$DEFAULT_RID"

BINARY="${1:-$ARTIFACTS/p2l}"
CSV_TASK="${2:-/opt/p2l/tasks.csv}"
INSTALL_DIR="/opt/p2l"
SERVICE_NAME="p2l"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"

# ── Pre-flight ────────────────────────────────────────────────────────────────
[[ -f "$BINARY" ]] || error "Binary not found: $BINARY\n  Build first with: ./deploy/publish.sh $DEFAULT_RID"

info "Installing P2L from: $BINARY"
info "Install directory  : $INSTALL_DIR"
info "CSV task file      : $CSV_TASK"
echo ""

# ── 1. Create system user ────────────────────────────────────────────────────
if ! id -u p2l &>/dev/null; then
    info "Creating system user/group 'p2l'..."
    useradd --system --no-create-home --shell /usr/sbin/nologin p2l
else
    info "System user 'p2l' already exists — skipping."
fi

# ── 2. Install binary + config ───────────────────────────────────────────────
info "Installing files to $INSTALL_DIR ..."
mkdir -p "$INSTALL_DIR/logs"
install -m 0755 "$BINARY" "$INSTALL_DIR/p2l"

# Copy appsettings.json from build output (if present)
APPSETTINGS="$(dirname "$BINARY")/appsettings.json"
if [[ -f "$APPSETTINGS" ]]; then
    install -m 0640 "$APPSETTINGS" "$INSTALL_DIR/appsettings.json"
    chown p2l:p2l "$INSTALL_DIR/appsettings.json"
else
    warn "appsettings.json not found alongside binary — using compiled-in defaults."
fi

chown -R p2l:p2l "$INSTALL_DIR"
chmod 755 "$INSTALL_DIR"
chmod 750 "$INSTALL_DIR/logs"

# ── 3. Write systemd unit ────────────────────────────────────────────────────
info "Writing systemd unit to $SERVICE_FILE ..."
cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=Pick2Light (P2L) Warehouse Control Daemon
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=p2l
Group=p2l
WorkingDirectory=$INSTALL_DIR
ExecStart=$INSTALL_DIR/p2l run $CSV_TASK
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
ReadWritePaths=$INSTALL_DIR/logs $INSTALL_DIR

[Install]
WantedBy=multi-user.target
EOF

# ── 4. Enable service ────────────────────────────────────────────────────────
info "Reloading systemd and enabling p2l.service ..."
systemctl daemon-reload
systemctl enable "$SERVICE_NAME"

# ── 5. Optionally start now ───────────────────────────────────────────────────
echo ""
read -r -p "Start the p2l service now? [y/N] " START_NOW
if [[ "${START_NOW,,}" == "y" ]]; then
    systemctl start "$SERVICE_NAME"
    sleep 1
    systemctl status "$SERVICE_NAME" --no-pager
else
    info "Service not started. Run when ready:"
    echo "    sudo systemctl start p2l"
fi

echo ""
info "Installation complete."
echo "  Logs  : journalctl -u p2l -f"
echo "  Stop  : sudo systemctl stop p2l"
echo "  Status: sudo systemctl status p2l"
