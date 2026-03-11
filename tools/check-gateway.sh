#!/usr/bin/env bash
# check-gateway.sh — Verify network connectivity to the Aioi L-PICK gateway.
#
# Usage:
#   ./tools/check-gateway.sh [gateway-ip]
#
# Default gateway IP is 192.168.1.254 (Aioi factory default).
# Requires sudo to set a static IP on the USB LAN adapter.

set -euo pipefail

GATEWAY_IP="${1:-192.168.1.254}"
GATEWAY_PORT=5003
LOCAL_IP="192.168.1.100"
NETMASK="255.255.255.0"
ADAPTER="en8"     # USB 10/100/1000 LAN — confirmed by networksetup

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'

pass() { echo -e "${GREEN}  ✓  $*${NC}"; }
fail() { echo -e "${RED}  ✗  $*${NC}"; }
info() { echo -e "${CYAN}  ▶  $*${NC}"; }
warn() { echo -e "${YELLOW}  ⚠  $*${NC}"; }

echo ""
echo -e "${CYAN}═══════════════════════════════════════════════════${NC}"
echo -e "${CYAN}   Pick2Light — Gateway Connectivity Check${NC}"
echo -e "${CYAN}═══════════════════════════════════════════════════${NC}"
echo ""

# ── Step 1: Confirm adapter is present ────────────────────────────────────
info "Step 1: Checking USB LAN adapter ($ADAPTER)..."
if ! ifconfig "$ADAPTER" &>/dev/null; then
    fail "Adapter $ADAPTER not found. Check USB connection."
    networksetup -listallhardwareports | grep -A2 "USB 10/100"
    exit 1
fi

LINK_STATUS=$(ifconfig "$ADAPTER" | grep "status:" | awk '{print $2}')
if [ "$LINK_STATUS" != "active" ]; then
    fail "Adapter $ADAPTER link is not active (status: $LINK_STATUS)."
    warn "Check the Ethernet cable between your Mac and the Aioi gateway."
    exit 1
fi
pass "Adapter $ADAPTER is present and link is active."

SPEED=$(ifconfig "$ADAPTER" | grep "media:" | grep -oE '[0-9]+base[A-Za-z]+' | head -1)
info "  Link speed: ${SPEED:-unknown}"

# ── Step 2: Set static IP on the adapter ──────────────────────────────────
info "Step 2: Configuring static IP $LOCAL_IP on $ADAPTER..."

CURRENT_IP=$(ipconfig getifaddr "$ADAPTER" 2>/dev/null || echo "none")
if [[ "$CURRENT_IP" == "$LOCAL_IP" ]]; then
    pass "Static IP $LOCAL_IP already configured."
else
    warn "Current IP on $ADAPTER: ${CURRENT_IP} — setting to $LOCAL_IP (requires sudo)"
    sudo ifconfig "$ADAPTER" inet "$LOCAL_IP" netmask "$NETMASK"
    sleep 1
    NEW_IP=$(ipconfig getifaddr "$ADAPTER" 2>/dev/null || echo "none")
    if [[ "$NEW_IP" == "$LOCAL_IP" ]]; then
        pass "Static IP set to $NEW_IP"
    else
        fail "Failed to set IP. Got: $NEW_IP"
        exit 1
    fi
fi

# ── Step 3: Ping the gateway ──────────────────────────────────────────────
info "Step 3: Pinging gateway at $GATEWAY_IP (5 packets)..."
if ping -c 5 -W 1000 -I "$ADAPTER" "$GATEWAY_IP" 2>/dev/null; then
    echo ""
    pass "Gateway $GATEWAY_IP is reachable."
else
    echo ""
    fail "Gateway $GATEWAY_IP did not respond to ping."
    warn "Possible causes:"
    warn "  • Gateway is powered off"
    warn "  • Gateway IP has been changed from the factory default (192.168.1.254)"
    warn "  • Gateway has ICMP ping disabled (try TCP check below)"
    warn ""
    warn "To find the actual gateway IP, run the ARP scan in Step 5."
fi

# ── Step 4: TCP port check ────────────────────────────────────────────────
info "Step 4: Checking TCP port $GATEWAY_PORT on $GATEWAY_IP..."
if nc -z -w 3 "$GATEWAY_IP" "$GATEWAY_PORT" 2>/dev/null; then
    pass "TCP port $GATEWAY_PORT is open — gateway is ready for P2L commands."
else
    fail "TCP port $GATEWAY_PORT is not responding on $GATEWAY_IP."
    warn "Gateway may be reachable but not in communication mode."
    warn "Ensure the gateway is powered on and initialized."
fi

# ── Step 5: ARP scan to find device if default IP changed ─────────────────
info "Step 5: ARP scan for Aioi devices on 192.168.1.0/24..."
echo ""

# Use arp-scan if available, otherwise fall back to nmap/manual ping sweep
if command -v arp-scan &>/dev/null; then
    sudo arp-scan --interface="$ADAPTER" --localnet 2>/dev/null | grep -v "^$\|Interface\|packets\|Ending" || true
elif command -v nmap &>/dev/null; then
    nmap -sn 192.168.1.0/24 -e "$ADAPTER" 2>/dev/null | grep -E "report|MAC" || true
else
    warn "Neither arp-scan nor nmap found. Running manual ping sweep (slow)..."
    for i in $(seq 1 254); do
        ip="192.168.1.$i"
        ping -c 1 -W 500 -I "$ADAPTER" "$ip" &>/dev/null && echo "  Found: $ip" &
    done
    wait
fi

# Show ARP table for any devices already discovered
echo ""
info "ARP table entries on subnet 192.168.1.x:"
arp -an 2>/dev/null | grep "192\.168\.1\." || echo "  (none yet — try pinging first)"

# ── Summary ───────────────────────────────────────────────────────────────
echo ""
echo -e "${CYAN}═══════════════════════════════════════════════════${NC}"
echo -e "${CYAN}   Summary${NC}"
echo -e "${CYAN}═══════════════════════════════════════════════════${NC}"
echo ""
echo "  Mac adapter : $ADAPTER  →  $LOCAL_IP"
echo "  Gateway     : $GATEWAY_IP:$GATEWAY_PORT"
echo ""
echo "  To connect with P2L:"
echo "    ~/.dotnet/dotnet run --project src/P2L.CLI"
echo ""
echo "  To override gateway IP:"
echo "    P2L_GATEWAY__HOST=$GATEWAY_IP ~/.dotnet/dotnet run --project src/P2L.CLI"
echo ""
