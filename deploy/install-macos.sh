#!/usr/bin/env bash
# install-macos.sh — Install P2L bridge service + Python web client as launchd agents on macOS.
#
# Usage (no sudo required — installs as user LaunchAgents):
#   ./deploy/install-macos.sh [path/to/p2l-binary] [csv-task-file] [path/to/client-dir]
#
# Defaults:
#   binary      : ./artifacts/osx-arm64/p2l  (or osx-x64 on Intel)
#   csv-task    : ~/Library/Application Support/P2L/tasks.csv
#   client-dir  : ./client
#
# Installs to:
#   ~/Library/Application Support/P2L/          (P2L binary + config)
#   ~/Library/Application Support/P2L/client/   (Python web dashboard + venv)
#   ~/Library/LaunchAgents/com.p2l.service.plist
#   ~/Library/LaunchAgents/com.p2l.client.plist

set -euo pipefail

# ── Colour helpers ────────────────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
info()  { echo -e "${CYAN}[INFO]${NC}  $*"; }
ok()    { echo -e "${GREEN}[OK]${NC}    $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC}  $*"; }
error() { echo -e "${RED}[ERROR]${NC} $*" >&2; exit 1; }

# ── macOS check ───────────────────────────────────────────────────────────────
[[ "$(uname -s)" == "Darwin" ]] || error "This script is for macOS only. Use install-linux.sh on Linux."

# ── Detect architecture ───────────────────────────────────────────────────────
ARCH=$(uname -m)
case "$ARCH" in
    x86_64)  DEFAULT_RID="osx-x64"   ;;
    arm64)   DEFAULT_RID="osx-arm64" ;;
    *)       DEFAULT_RID="osx-arm64" ;;
esac

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ARTIFACTS="$SCRIPT_DIR/../artifacts/$DEFAULT_RID"

BINARY="${1:-$ARTIFACTS/p2l}"
CSV_TASK="${2:-$HOME/Library/Application Support/P2L/tasks.csv}"
CLIENT_SRC="${3:-$SCRIPT_DIR/../client}"

INSTALL_DIR="$HOME/Library/Application Support/P2L"
CLIENT_INSTALL_DIR="$INSTALL_DIR/client"
LOG_DIR="$HOME/Library/Logs/P2L"
LAUNCH_AGENTS_DIR="$HOME/Library/LaunchAgents"

# ── Pre-flight ────────────────────────────────────────────────────────────────
[[ -f "$BINARY" ]]     || error "Binary not found: $BINARY\n  Build first with: ./deploy/publish.sh $DEFAULT_RID"
[[ -d "$CLIENT_SRC" ]] || error "Client source not found: $CLIENT_SRC"
python3 --version &>/dev/null || error "python3 not found — install via: brew install python"
pip3 --version    &>/dev/null || error "pip3 not found — install via: brew install python"

info "Installing P2L binary    : $INSTALL_DIR"
info "Python client            : $CLIENT_INSTALL_DIR"
info "Logs                     : $LOG_DIR"
info "CSV task file            : $CSV_TASK"
echo ""

# ── 1. Create directories ────────────────────────────────────────────────────
mkdir -p "$INSTALL_DIR" "$CLIENT_INSTALL_DIR" "$LOG_DIR" "$LAUNCH_AGENTS_DIR"
mkdir -p "$(dirname "$CSV_TASK")"

# ── 2. Install P2L binary + config ───────────────────────────────────────────
info "Installing P2L binary ..."
cp "$BINARY" "$INSTALL_DIR/p2l"
chmod +x "$INSTALL_DIR/p2l"

APPSETTINGS="$(dirname "$BINARY")/appsettings.json"
[[ ! -f "$APPSETTINGS" ]] && APPSETTINGS="$SCRIPT_DIR/../appsettings.json"
if [[ -f "$APPSETTINGS" ]]; then
    cp "$APPSETTINGS" "$INSTALL_DIR/appsettings.json"
    ok "Copied appsettings.json"
else
    warn "appsettings.json not found — using compiled-in defaults"
fi

# Seed a stub tasks.csv if none exists
if [[ ! -f "$CSV_TASK" ]]; then
    echo "address,label,quantity,color" > "$CSV_TASK"
    warn "Created empty tasks.csv at: $CSV_TASK"
fi

ok "P2L binary installed"

# ── 3. Install Python web client ─────────────────────────────────────────────
info "Installing Python web client ..."
rsync -a --delete \
    --exclude='__pycache__' --exclude='*.pyc' --exclude='venv' \
    --exclude='*.db-shm' --exclude='*.db-wal' --exclude='logs' \
    "$CLIENT_SRC/" "$CLIENT_INSTALL_DIR/"

info "Creating Python virtual environment ..."
python3 -m venv "$CLIENT_INSTALL_DIR/venv"
"$CLIENT_INSTALL_DIR/venv/bin/pip" install --quiet --upgrade pip
"$CLIENT_INSTALL_DIR/venv/bin/pip" install --quiet -r "$CLIENT_INSTALL_DIR/requirements.txt"
ok "Python client installed (venv ready)"

# ── 4. Write launchd plists ───────────────────────────────────────────────────
info "Writing launchd plists ..."

# P2L bridge service
cat > "$LAUNCH_AGENTS_DIR/com.p2l.service.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.p2l.service</string>

    <key>ProgramArguments</key>
    <array>
        <string>$INSTALL_DIR/p2l</string>
        <string>run</string>
        <string>$CSV_TASK</string>
    </array>

    <key>WorkingDirectory</key>
    <string>$INSTALL_DIR</string>

    <key>RunAtLoad</key>
    <true/>

    <key>KeepAlive</key>
    <dict>
        <key>SuccessfulExit</key>
        <false/>
    </dict>

    <key>ThrottleInterval</key>
    <integer>5</integer>

    <key>StandardOutPath</key>
    <string>$LOG_DIR/p2l.log</string>

    <key>StandardErrorPath</key>
    <string>$LOG_DIR/p2l.log</string>
</dict>
</plist>
PLIST

# Python web dashboard
cat > "$LAUNCH_AGENTS_DIR/com.p2l.client.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.p2l.client</string>

    <key>ProgramArguments</key>
    <array>
        <string>$CLIENT_INSTALL_DIR/venv/bin/python</string>
        <string>$CLIENT_INSTALL_DIR/run.py</string>
    </array>

    <key>WorkingDirectory</key>
    <string>$CLIENT_INSTALL_DIR</string>

    <key>EnvironmentVariables</key>
    <dict>
        <key>P2L_CLIENT_HOST</key>
        <string>0.0.0.0</string>
        <key>P2L_CLIENT_PORT</key>
        <string>8765</string>
    </dict>

    <key>RunAtLoad</key>
    <true/>

    <key>KeepAlive</key>
    <dict>
        <key>SuccessfulExit</key>
        <false/>
    </dict>

    <key>ThrottleInterval</key>
    <integer>5</integer>

    <key>StandardOutPath</key>
    <string>$LOG_DIR/p2l-client.log</string>

    <key>StandardErrorPath</key>
    <string>$LOG_DIR/p2l-client.log</string>
</dict>
</plist>
PLIST

ok "launchd plists written"

# ── 5. Load agents ────────────────────────────────────────────────────────────
echo ""
read -r -p "Load and start both services now? [y/N] " START_NOW
if [[ "${START_NOW,,}" == "y" ]]; then
    # Unload first if already loaded (clean reload)
    launchctl unload "$LAUNCH_AGENTS_DIR/com.p2l.service.plist" 2>/dev/null || true
    launchctl unload "$LAUNCH_AGENTS_DIR/com.p2l.client.plist"  2>/dev/null || true

    launchctl load "$LAUNCH_AGENTS_DIR/com.p2l.service.plist"
    sleep 1
    launchctl load "$LAUNCH_AGENTS_DIR/com.p2l.client.plist"
    sleep 1

    ok "Services started"
    launchctl list com.p2l.service 2>/dev/null && ok "com.p2l.service running"
    launchctl list com.p2l.client  2>/dev/null && ok "com.p2l.client running"
else
    info "Services not started. Run when ready:"
    echo "    launchctl load $LAUNCH_AGENTS_DIR/com.p2l.service.plist"
    echo "    launchctl load $LAUNCH_AGENTS_DIR/com.p2l.client.plist"
fi

echo ""
ok "=== Installation complete ==="
echo "  Dashboard       : http://localhost:8765"
echo "  Logs (bridge)   : tail -f $LOG_DIR/p2l.log"
echo "  Logs (client)   : tail -f $LOG_DIR/p2l-client.log"
echo "  Stop bridge     : launchctl unload $LAUNCH_AGENTS_DIR/com.p2l.service.plist"
echo "  Stop client     : launchctl unload $LAUNCH_AGENTS_DIR/com.p2l.client.plist"
echo "  Uninstall       : ./deploy/uninstall-macos.sh"
