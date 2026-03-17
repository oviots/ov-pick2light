#!/usr/bin/env bash
# install-linux.sh — Install P2L bridge service + Python web client as systemd services on Linux.
#
# Usage (run as root or with sudo):
#   sudo ./deploy/install-linux.sh [path/to/p2l-binary] [csv-task-file] [path/to/client-dir]
#
# Defaults:
#   binary      : ./artifacts/linux-x64/p2l  (or linux-arm64 if on ARM)
#   csv-task    : /opt/p2l/tasks.csv
#   client-dir  : ./client
#
# What this script does:
#   1. Creates system user/group "p2l"
#   2. Copies binary + config to /opt/p2l
#   3. Installs Python client + venv to /opt/p2l/client
#   4. Installs systemd units (p2l.service + p2l-client.service) and enables both
#   5. Optionally starts both services immediately

set -euo pipefail

# ── Colour helpers ──────────────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
info()  { echo -e "${CYAN}[INFO]${NC}  $*"; }
ok()    { echo -e "${GREEN}[OK]${NC}    $*"; }
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
CLIENT_SRC="${3:-$SCRIPT_DIR/../client}"
INSTALL_DIR="/opt/p2l"
CLIENT_INSTALL_DIR="$INSTALL_DIR/client"
SERVICE_NAME="p2l"
CLIENT_SERVICE_NAME="p2l-client"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
CLIENT_SERVICE_FILE="/etc/systemd/system/${CLIENT_SERVICE_NAME}.service"

# ── Pre-flight ────────────────────────────────────────────────────────────────
[[ -f "$BINARY" ]]     || error "Binary not found: $BINARY\n  Build first with: ./deploy/publish.sh $DEFAULT_RID"
[[ -d "$CLIENT_SRC" ]] || error "Client source not found: $CLIENT_SRC"
python3 --version &>/dev/null || error "python3 not found — install Python 3.10+ first"
pip3 --version    &>/dev/null || error "pip3 not found — install pip first"

info "Installing P2L from     : $BINARY"
info "Install directory       : $INSTALL_DIR"
info "CSV task file           : $CSV_TASK"
info "Python client source    : $CLIENT_SRC"
info "Python client install   : $CLIENT_INSTALL_DIR"
echo ""

# ── 1. Create system user ────────────────────────────────────────────────────
if ! id -u p2l &>/dev/null; then
    info "Creating system user/group 'p2l'..."
    useradd --system --no-create-home --shell /usr/sbin/nologin p2l
else
    ok "System user 'p2l' already exists — skipping."
fi

# ── 2. Install binary + config ───────────────────────────────────────────────
info "Installing P2L binary to $INSTALL_DIR ..."
mkdir -p "$INSTALL_DIR/logs"
install -m 0755 "$BINARY" "$INSTALL_DIR/p2l"

APPSETTINGS="$(dirname "$BINARY")/appsettings.json"
[[ ! -f "$APPSETTINGS" ]] && APPSETTINGS="$SCRIPT_DIR/../appsettings.json"
if [[ -f "$APPSETTINGS" ]]; then
    install -m 0640 "$APPSETTINGS" "$INSTALL_DIR/appsettings.json"
    chown p2l:p2l "$INSTALL_DIR/appsettings.json"
    ok "Copied appsettings.json"
else
    warn "appsettings.json not found — using compiled-in defaults."
fi

# ── 3. Install Python web client ─────────────────────────────────────────────
info "Installing Python web client to $CLIENT_INSTALL_DIR ..."
mkdir -p "$CLIENT_INSTALL_DIR"

rsync -a --delete \
    --exclude='__pycache__' --exclude='*.pyc' --exclude='venv' \
    --exclude='*.db-shm' --exclude='*.db-wal' --exclude='logs' \
    "$CLIENT_SRC/" "$CLIENT_INSTALL_DIR/"

info "Creating Python virtual environment ..."
python3 -m venv "$CLIENT_INSTALL_DIR/venv"
"$CLIENT_INSTALL_DIR/venv/bin/pip" install --quiet --upgrade pip
"$CLIENT_INSTALL_DIR/venv/bin/pip" install --quiet -r "$CLIENT_INSTALL_DIR/requirements.txt"
ok "Python client installed (venv ready)"

# ── 4. Fix permissions ────────────────────────────────────────────────────────
chown -R p2l:p2l "$INSTALL_DIR"
chmod 755 "$INSTALL_DIR"
chmod 750 "$INSTALL_DIR/logs"

# ── 5. Write systemd units ───────────────────────────────────────────────────
info "Writing systemd unit: $SERVICE_FILE ..."
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

info "Writing systemd unit: $CLIENT_SERVICE_FILE ..."
cat > "$CLIENT_SERVICE_FILE" <<EOF
[Unit]
Description=Pick2Light Web Dashboard (P2L Client)
After=network-online.target p2l.service
Wants=network-online.target

[Service]
Type=simple
User=p2l
Group=p2l
WorkingDirectory=$CLIENT_INSTALL_DIR
ExecStart=$CLIENT_INSTALL_DIR/venv/bin/python $CLIENT_INSTALL_DIR/run.py
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
ReadWritePaths=$CLIENT_INSTALL_DIR $CLIENT_INSTALL_DIR/logs

[Install]
WantedBy=multi-user.target
EOF

# ── 6. Enable services ────────────────────────────────────────────────────────
info "Reloading systemd and enabling services ..."
systemctl daemon-reload
systemctl enable "$SERVICE_NAME" "$CLIENT_SERVICE_NAME"

# ── 7. Optionally start now ───────────────────────────────────────────────────
echo ""
read -r -p "Start both services now? [y/N] " START_NOW
if [[ "${START_NOW,,}" == "y" ]]; then
    systemctl start "$SERVICE_NAME"
    sleep 1
    systemctl start "$CLIENT_SERVICE_NAME"
    sleep 1
    systemctl status "$SERVICE_NAME" --no-pager
    systemctl status "$CLIENT_SERVICE_NAME" --no-pager
else
    info "Services not started. Run when ready:"
    echo "    sudo systemctl start p2l"
    echo "    sudo systemctl start p2l-client"
fi

echo ""
ok "=== Installation complete ==="
echo "  Dashboard       : http://localhost:8765"
echo "  Logs (bridge)   : journalctl -u p2l -f"
echo "  Logs (client)   : journalctl -u p2l-client -f"
echo "  Stop bridge     : sudo systemctl stop p2l"
echo "  Stop client     : sudo systemctl stop p2l-client"
echo "  Status bridge   : sudo systemctl status p2l"
echo "  Status client   : sudo systemctl status p2l-client"
