# Pick2Light (P2L) — Build & Execution Guide

## 1. Prerequisites

### .NET SDK
P2L targets **net10.0**. The SDK is installed at `~/.dotnet/dotnet` and is **not** in the system PATH.

```bash
# Verify SDK version
~/.dotnet/dotnet --version
# Expected: 10.0.x
```

### Native AOT toolchain (for production builds only)
Native AOT requires a native C compiler. Install based on your platform:

| Platform | Requirement |
|---|---|
| macOS | Xcode Command Line Tools (`xcode-select --install`) |
| Linux (Debian/Ubuntu) | `sudo apt install clang zlib1g-dev` |
| Windows | Visual Studio Build Tools with C++ workload |

### NuGet packages (restored automatically)
All dependencies are declared in the `.csproj` files. Key packages:

| Project | Package | Version |
|---|---|---|
| P2L.CLI | `Spectre.Console`, `Spectre.Console.Cli` | 0.49.1 |
| P2L.CLI | `Microsoft.Extensions.*` (DI, Logging, Configuration) | 10.0.0 |
| P2L.Hardware, P2L.Core | `Microsoft.Extensions.Logging.Abstractions` | 10.0.0 |

---

## 2. Repository Layout

```
P2L.sln
src/
  P2L.Shared/      — Enums, Models, AioiConstants (no external deps)
  P2L.Hardware/    — TcpTransport, AioiGateway, Protocol codecs
  P2L.Core/        — LightModuleRegistry, PickingEngine, HeartbeatService
  P2L.CLI/         — Spectre.Console CLI, DI wiring, appsettings.json
tests/
  P2L.IntegrationTests/
docs/              — Aioi hardware PDFs
```

**Dependency order:** `Shared → Hardware → Core → CLI`

---

## 3. Build

### Debug build (all projects)
```bash
~/.dotnet/dotnet build P2L.sln
```
Output lands in each project's `bin/Debug/net10.0/` directory.

### Release build
```bash
~/.dotnet/dotnet build P2L.sln -c Release
```

### Native AOT single-file binary (production)
P2L.CLI is configured with `<PublishAot>true</PublishAot>` and `<PublishSingleFile>true</PublishSingleFile>`. Run the publish command targeting your platform:

```bash
# Linux x64 (warehouse industrial PC)
~/.dotnet/dotnet publish src/P2L.CLI/P2L.CLI.csproj \
  -c Release -r linux-x64 -o ./publish/linux-x64

# macOS ARM64 (Apple Silicon dev machine)
~/.dotnet/dotnet publish src/P2L.CLI/P2L.CLI.csproj \
  -c Release -r osx-arm64 -o ./publish/osx-arm64

# Windows x64
~/.dotnet/dotnet publish src/P2L.CLI/P2L.CLI.csproj \
  -c Release -r win-x64 -o ./publish/win-x64
```

The output directory will contain a single self-contained binary named **`p2l`** (or `p2l.exe` on Windows) alongside `appsettings.json`.

> **Important:** `appsettings.json` is marked `CopyToOutputDirectory: PreserveNewest`. It must remain next to the binary — it is loaded from `AppContext.BaseDirectory` at startup.

---

## 4. Configuration

Edit `src/P2L.CLI/appsettings.json` before publishing:

```json
{
  "Gateway": {
    "Host": "192.168.1.254",
    "Port": 5003,
    "BlockNo": "00"
  },
  "LogDirectory": "logs",
  "MaxLogFileSizeBytes": 10485760
}
```

Configuration can also be overridden at runtime without rebuilding:

```bash
# Via environment variables (prefix: P2L_)
export P2L_Gateway__Host=192.168.10.50
export P2L_Gateway__Port=5003
./p2l

# Via CLI arguments
./p2l --Gateway:Host=192.168.10.50
```

---

## 5. USB-LAN Connection Verification

The Aioi gateway communicates over **TCP port 5003**. When using a USB-to-Ethernet adapter, verify the network interface is configured correctly before launching P2L.

### Step 1 — Identify the USB-LAN interface

```bash
# macOS
ifconfig | grep -A 4 "en"

# Linux
ip link show
# or
nmcli device status
```

Look for a new interface that appeared after plugging in the adapter (typically `en2`, `en3` on macOS, or `eth1`, `usb0` on Linux).

### Step 2 — Assign a static IP on the same subnet as the gateway

The gateway default is `192.168.1.254`. Your host must be on the `192.168.1.x/24` subnet:

```bash
# macOS (via networksetup)
sudo networksetup -setmanual "USB 10/100/1000 LAN" 192.168.1.100 255.255.255.0 192.168.1.1

# Linux (nmcli)
sudo nmcli con add type ethernet ifname eth1 \
  ipv4.method manual ipv4.addresses 192.168.1.100/24

# Linux (ip command, temporary)
sudo ip addr add 192.168.1.100/24 dev eth1
sudo ip link set eth1 up
```

### Step 3 — Verify layer-3 reachability (ping)

```bash
ping -c 4 192.168.1.254
```

All 4 packets must return. If the host is unreachable, check: subnet mismatch, adapter driver, or cable.

### Step 4 — Verify TCP port 5003 is open

```bash
# Using nc (netcat)
nc -zv 192.168.1.254 5003
# Expected: Connection to 192.168.1.254 port 5003 [tcp] succeeded!

# Alternative using bash TCP /dev/tcp
timeout 3 bash -c 'echo >/dev/tcp/192.168.1.254/5003' && echo "Port open" || echo "Port closed"
```

If TCP is refused, the gateway may not be powered on or its firmware may require initialization via the physical panel button first.

### Step 5 — Confirm no firewall is blocking outbound TCP 5003

```bash
# macOS — check application firewall
sudo /usr/libexec/ApplicationFirewall/socketfilterfw --getglobalstate

# Linux (iptables)
sudo iptables -L OUTPUT -n | grep 5003

# Linux (ufw)
sudo ufw status
```

---

## 6. Running the Application

### Interactive dashboard (default mode)
```bash
./p2l
# Or with a custom gateway:
./p2l --Gateway:Host=192.168.1.254
```

On startup the system:
1. Loads `appsettings.json`
2. Starts `TcpTransport` background send/receive loops with auto-reconnect (`ReconnectDelayMs = 1000 ms`)
3. Starts `HeartbeatService` (ping every `30000 ms`)
4. Sends the **Z** (initialize) command and waits for the `o` ACK
5. Launches the Spectre.Console warehouse dashboard

To skip the initialization command (e.g., gateway already initialized):
```bash
./p2l --no-init
```

### One-shot commands (non-interactive)

```bash
# Light ON: address 0001, quantity 5, green
./p2l light on 0001 5

# Light ON with flash and custom color
./p2l light on 0002 3 --color Red --flash

# Light OFF
./p2l light off 0001

# Terminate all modules
./p2l light terminate

# Terminate specific block
./p2l light terminate --block 01

# Batch pick session
./p2l pick "0001:5,0002:3,0003:10"
./p2l pick "0001:5,0002:3" --color Yellow --block 01
```

### Available colors
`Off` `Red` `Green` `Blue` `Yellow` `Cyan` `Magenta` `White`

---

## 7. Audit Logs

All hardware TX/RX frames are written to rotating log files in the `logs/` directory (configurable via `LogDirectory`). Each file rotates at **10 MB** by default. These logs are the primary evidence source for mis-pick investigations.

```bash
ls -lh logs/
tail -f logs/p2l-*.log
```

Log level for hardware traffic is `Debug`. The console shows `Information` and above by default.

---

## 8. systemd Service (Linux 24/7 Operation)

Create `/etc/systemd/system/p2l.service`:

```ini
[Unit]
Description=Pick2Light Gateway Controller
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=/opt/p2l
ExecStart=/opt/p2l/p2l
Restart=always
RestartSec=5
Environment=P2L_Gateway__Host=192.168.1.254

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable p2l
sudo systemctl start p2l
sudo systemctl status p2l
```

---

## 9. Quick Troubleshooting Reference

| Symptom | Likely cause | Fix |
|---|---|---|
| `Cannot connect to 192.168.1.254:5003` (repeated) | Wrong IP or adapter not on subnet | Re-check Steps 2–4 above |
| `Timeout waiting for ACK` on `Z` command | Gateway powered off or busy | Power-cycle gateway; check physical LED status |
| Build error `clang not found` | Native AOT requires C compiler | Install Xcode CLT (macOS) or `clang` (Linux) |
| `appsettings.json not found` warning | Binary moved without config file | Keep `appsettings.json` next to the `p2l` binary |
| `Channel full (512)` log warning | Commands queued faster than hardware processes | Reduce batch rate; check TCP throughput |
