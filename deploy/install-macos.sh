#!/usr/bin/env bash
# install-macos.sh — Install P2L service + web client as launchd agents on macOS.
# No Python runtime required — uses pre-built binaries.
#
# Usage (no sudo required — installs as user LaunchAgents):
#   ./deploy/install-macos.sh [package-dir]
#
# Installs to: ~/Library/Application Support/P2L/

set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
info()  { echo -e "${CYAN}[INFO]${NC}  $*"; }
ok()    { echo -e "${GREEN}[OK]${NC}    $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC}  $*"; }
error() { echo -e "${RED}[ERROR]${NC} $*" >&2; exit 1; }

[[ "$(uname -s)" == "Darwin" ]] || error "This script is for macOS only."

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PKG_ROOT="${1:-$(cd "$SCRIPT_DIR/.." && pwd)}"
INSTALL_DIR="$HOME/Library/Application Support/P2L"
LOG_DIR="$HOME/Library/Logs/P2L"
LAUNCH_DIR="$HOME/Library/LaunchAgents"

# ── Pre-flight ────────────────────────────────────────────────────────────────
[[ -f "$PKG_ROOT/bin/p2l" ]]        || error "Not found: $PKG_ROOT/bin/p2l"
[[ -f "$PKG_ROOT/bin/p2l-client" ]] || error "Not found: $PKG_ROOT/bin/p2l-client"

info "Package root : $PKG_ROOT"
info "Install dir  : $INSTALL_DIR"
echo ""

# ── 1. Install binaries ───────────────────────────────────────────────────────
info "Installing binaries ..."
mkdir -p "$INSTALL_DIR/config" "$INSTALL_DIR/logs" "$LOG_DIR" "$LAUNCH_DIR"
install -m 0755 "$PKG_ROOT/bin/p2l"        "$INSTALL_DIR/p2l"
install -m 0755 "$PKG_ROOT/bin/p2l-client" "$INSTALL_DIR/p2l-client"
ok "Installed p2l and p2l-client"

# SQLite native library
for lib in "$PKG_ROOT/bin/"libe_sqlite3.* "$PKG_ROOT/bin/"e_sqlite3.*; do
    if [[ -f "$lib" ]]; then
        install -m 0644 "$lib" "$INSTALL_DIR/"
        ok "Installed SQLite lib: $(basename "$lib")"
    fi
done

# Preserve the bridge configuration on upgrade
if [[ -f "$PKG_ROOT/config/p2l_config.json" ]]; then
    if [[ -f "$INSTALL_DIR/config/p2l_config.json" ]]; then
        warn "config/p2l_config.json exists — keeping existing config"
    else
        install -m 0640 "$PKG_ROOT/config/p2l_config.json" "$INSTALL_DIR/config/p2l_config.json"
        ok "Installed config/p2l_config.json  <- edit before starting the service"
    fi
fi

# ── 2. Launchd plist: p2l bridge ─────────────────────────────────────────────
info "Writing launchd plists ..."

cat > "$LAUNCH_DIR/com.p2l.service.plist" << PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.p2l.service</string>

    <key>ProgramArguments</key>
    <array>
        <string>${INSTALL_DIR}/p2l</string>
        <string>serve</string>
    </array>

    <key>WorkingDirectory</key>
    <string>${INSTALL_DIR}</string>

    <key>EnvironmentVariables</key>
    <dict>
        <key>DYLD_LIBRARY_PATH</key>
        <string>${INSTALL_DIR}</string>
    </dict>

    <key>RunAtLoad</key>     <true/>
    <key>KeepAlive</key>     <dict><key>SuccessfulExit</key><false/></dict>
    <key>ThrottleInterval</key> <integer>5</integer>

    <key>StandardOutPath</key>   <string>${LOG_DIR}/p2l.log</string>
    <key>StandardErrorPath</key> <string>${LOG_DIR}/p2l.log</string>
</dict>
</plist>
PLIST

# ── 3. Launchd plist: p2l-client ─────────────────────────────────────────────
cat > "$LAUNCH_DIR/com.p2l.client.plist" << PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.p2l.client</string>

    <key>ProgramArguments</key>
    <array>
        <string>${INSTALL_DIR}/p2l-client</string>
    </array>

    <key>WorkingDirectory</key>
    <string>${INSTALL_DIR}</string>

    <key>EnvironmentVariables</key>
    <dict>
        <key>P2L_CLIENT_HOST</key>  <string>127.0.0.1</string>
        <key>P2L_CLIENT_PORT</key>  <string>8765</string>
    </dict>

    <key>RunAtLoad</key>     <true/>
    <key>KeepAlive</key>     <dict><key>SuccessfulExit</key><false/></dict>
    <key>ThrottleInterval</key> <integer>5</integer>

    <key>StandardOutPath</key>   <string>${LOG_DIR}/p2l-client.log</string>
    <key>StandardErrorPath</key> <string>${LOG_DIR}/p2l-client.log</string>
</dict>
</plist>
PLIST

ok "Launchd plists written"

# ── 4. Load agents ────────────────────────────────────────────────────────────
echo ""
read -r -p "Load and start both services now? [y/N] " START_NOW
if [[ "$START_NOW" == [yY] ]]; then
    launchctl unload "$LAUNCH_DIR/com.p2l.service.plist" 2>/dev/null || true
    launchctl unload "$LAUNCH_DIR/com.p2l.client.plist"  2>/dev/null || true
    launchctl load   "$LAUNCH_DIR/com.p2l.service.plist"
    sleep 1
    launchctl load   "$LAUNCH_DIR/com.p2l.client.plist"
    sleep 1
    launchctl list com.p2l.service 2>/dev/null && ok "com.p2l.service loaded"
    launchctl list com.p2l.client  2>/dev/null && ok "com.p2l.client loaded"
else
    info "Not started. When ready:"
    echo "    launchctl load \"$LAUNCH_DIR/com.p2l.service.plist\""
    echo "    launchctl load \"$LAUNCH_DIR/com.p2l.client.plist\""
fi

echo ""
ok "=== Installation complete ==="
echo "  Dashboard     : http://localhost:8765"
echo "  Config        : $INSTALL_DIR/config/p2l_config.json"
echo "  Logs (bridge) : tail -f $LOG_DIR/p2l.log"
echo "  Logs (client) : tail -f $LOG_DIR/p2l-client.log"
echo "  Stop bridge   : launchctl unload \"$LAUNCH_DIR/com.p2l.service.plist\""
echo "  Stop client   : launchctl unload \"$LAUNCH_DIR/com.p2l.client.plist\""
echo "  Uninstall     : rm -rf \"$INSTALL_DIR\" \"$LAUNCH_DIR/com.p2l.*.plist\""
