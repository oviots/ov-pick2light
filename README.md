# Pick2Light (P2L) — Aioi Light Module Controller

A cross-platform .NET 10 CLI for controlling Aioi Systems L-PICK light modules over TCP.
Runs as one-shot commands from scripts, CI, or automation pipelines.
Publishes to a **single self-contained native binary** (Native AOT) with no runtime dependency.

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Repository Layout](#2-repository-layout)
3. [Build](#3-build)
4. [Configuration](#4-configuration)
5. [USB-LAN Connection Verification](#5-usb-lan-connection-verification)
6. [Command Reference](#6-command-reference)
   - [light on / off / terminate](#light--individual-module-control)
   - [light set — adjust parameters](#light-set--adjust-module-parameters)
   - [pick — batch session](#pick--batch-picking-session)
   - [devices — scan & list](#devices--scan-and-list)
   - [export — CSV export](#export--export-to-csv)
   - [import — CSV bulk apply](#import--bulk-apply-from-csv)
7. [Bulk CSV Workflow](#7-bulk-csv-workflow)
8. [Audit Logs](#8-audit-logs)
9. [MQTT Bridge](#9-mqtt-bridge)
10. [systemd Service](#10-systemd-service-linux-247-operation)
11. [Protocol Notes](#11-protocol-notes)
12. [Troubleshooting](#12-quick-troubleshooting-reference)

---

## 1. Prerequisites

### .NET SDK
P2L targets **net10.0**. The SDK is installed at `~/.dotnet/dotnet` and is **not** in the system PATH.

```bash
~/.dotnet/dotnet --version   # Expected: 10.0.x
```

### Native AOT toolchain (production builds only)

| Platform | Requirement |
|---|---|
| macOS | Xcode Command Line Tools: `xcode-select --install` |
| Linux (Debian/Ubuntu) | `sudo apt install clang zlib1g-dev` |
| Windows | Visual Studio Build Tools with C++ workload |

### NuGet packages (restored automatically)

| Project | Package | Version |
|---|---|---|
| P2L.CLI | `Spectre.Console`, `Spectre.Console.Cli` | 0.49.1 |
| P2L.CLI | `Microsoft.Extensions.*` (DI, Logging, Configuration) | 10.0.0 |
| P2L.Hardware, P2L.Core | `Microsoft.Extensions.Logging.Abstractions` | 10.0.0 |
| P2L.Mqtt | `MQTTnet` | 5.1.0.1559 |
| P2L.Mqtt | `Microsoft.Extensions.Logging.Abstractions` | 10.0.0 |

---

## 2. Repository Layout

```
P2L.sln
src/
  P2L.Shared/          Enums, Models, AioiConstants — no external dependencies
    Enums/             LightColor, LightState, ModuleStatus
    Models/            LightModuleState, PickingInstruction, ConfirmationEvent,
                       ModuleParameters
  P2L.Hardware/        TCP transport and Aioi protocol codecs
    Protocol/          AioiPacket, AioiEncoder, AioiDecoder
    Transport/         TcpTransport (Channel-based, auto-reconnect)
    AioiGateway.cs     High-level domain API over TcpTransport
  P2L.Core/            Business logic
    LightModuleRegistry.cs   Thread-safe module state (ConcurrentDictionary)
    PickingEngine.cs         Batch orchestration, confirmation tracking
    Services/
      HeartbeatService.cs    Keep-alive loop (Az every 30 s)
      DeviceScanService.cs   Concurrent Az range scan
  P2L.Mqtt/            Optional MQTT bridge (MQTTnet v5, Native AOT safe)
    Configuration/     MqttSettings, MqttTlsSettings
    Messages/          Commands (light_on, batch_start, …)
                       Events   (confirmed, light_changed, batch_completed, bridge_status)
                       P2LMqttJsonContext (source-gen JSON context for AOT)
    MqttCommandDispatcher.cs  Deserializes incoming commands → PickingEngine
    MqttEventPublisher.cs     Subscribes to PickingEngine events → publishes to broker
    MqttBridgeService.cs      Lifecycle owner: TLS connect, reconnect loop, LWT
  P2L.CLI/             Spectre.Console CLI, DI wiring
    Commands/          RootCommand, LightCommands, ListDevicesCommand,
                       ConfigureDeviceCommand, ExportCommand, ImportCommand
    Infrastructure/    TypeRegistrar, RotatingFileLogger, ParameterParser,
                       DeviceConfigCsvService
    Configuration/     AppSettings, GatewaySettings
    appsettings.json
tests/
  P2L.IntegrationTests/
docs/                  Aioi hardware PDFs (Command Reference V4.1, Dev Guide V4.0,
                       Lightstep Programming Guide V3.0)
publish/               Native AOT binary output (gitignored)
```

**Dependency order:** `Shared → Hardware → Core → Mqtt → CLI`

---

## 3. Build

### Debug (all projects)
```bash
~/.dotnet/dotnet build P2L.sln
```

### Release
```bash
~/.dotnet/dotnet build P2L.sln -c Release
```

### Native AOT single-file binary (production)

```bash
# Linux x64 — warehouse industrial PC / Raspberry Pi
~/.dotnet/dotnet publish src/P2L.CLI/P2L.CLI.csproj \
  -c Release -r linux-x64 --self-contained true \
  -o ./publish/linux-x64

# macOS ARM64 — Apple Silicon dev / handheld
~/.dotnet/dotnet publish src/P2L.CLI/P2L.CLI.csproj \
  -c Release -r osx-arm64 --self-contained true \
  -o ./publish/osx-arm64

# Windows x64
~/.dotnet/dotnet publish src/P2L.CLI/P2L.CLI.csproj \
  -c Release -r win-x64 --self-contained true \
  -o ./publish/win-x64
```

Output: a single `p2l` (or `p2l.exe`) binary (~9 MB) plus `appsettings.json`.

> `appsettings.json` must stay beside the binary — it is read from `AppContext.BaseDirectory` at startup.

---

## 4. Configuration

Edit `src/P2L.CLI/appsettings.json` before publishing, or override at runtime with environment variables:

```json
{
  "Gateway": {
    "Host":    "192.168.1.254",
    "Port":    5003,
    "BlockNo": "00"
  },
  "LogDirectory":       "logs",
  "MaxLogFileSizeBytes": 10485760,
  "Logging": {
    "LogLevel": {
      "Default":          "Information",
      "P2L.Hardware":     "Debug",
      "Microsoft.Hosting":"Warning"
    }
  },
  "Mqtt": {
    "Enabled": false
  }
}
```

**Runtime overrides** via environment variables (prefix `P2L_`, double-underscore for nesting):

```bash
export P2L_Gateway__Host=192.168.10.50
export P2L_Gateway__Port=5003
./p2l light on 0001 5
```

---

## 5. USB-LAN Connection Verification

The Aioi gateway communicates over **TCP port 5003**. The USB-LAN adapter must reach the gateway
while WiFi continues to handle internet traffic. Follow the steps below for your OS.

---

### macOS

#### Step 1 — Identify the USB-LAN interface name
```bash
networksetup -listallhardwareports | grep -A 2 "USB"
# Example output:
#   Hardware Port: USB 10/100/1000 LAN
#   Device: en2          ← note this name
```

#### Step 2 — Check your WiFi subnet
```bash
networksetup -getinfo Wi-Fi
# Note the IP Address and Subnet Mask lines
```

The setup path depends on whether WiFi shares the `192.168.1.x` subnet with the gateway:

---

##### Case A — WiFi is on a DIFFERENT subnet (e.g. `192.168.0.x`, `10.x.x.x`)

No conflict. Just assign a static IP to the USB-LAN adapter and put WiFi first in service order:

```bash
# Assign static IP — no router argument (avoids overriding the WiFi default route)
sudo networksetup -setmanual "USB 10/100/1000 LAN" 192.168.1.100 255.255.255.0

# Keep WiFi as the primary (internet) interface
sudo networksetup -ordernetworkservices \
  "Wi-Fi" "USB 10/100/1000 LAN" "Thunderbolt Bridge"
```

---

##### Case B — WiFi is ALSO on `192.168.1.x` (subnet conflict)

Both adapters on the same subnet confuse macOS routing — assigning USB-LAN a static IP breaks
the WiFi default route. Fix: pin only the Aioi gateway IP to the USB-LAN interface using a
host route, leaving WiFi as the default for everything else.

**Immediate setup (lost on reboot):**

```bash
# 1. Assign static IP to USB-LAN (no router)
sudo networksetup -setmanual "USB 10/100/1000 LAN" 192.168.1.100 255.255.255.0

# 2. Pin the Aioi gateway to USB-LAN only (replace en2 with your device name from Step 1)
sudo route add -host 192.168.1.254 -interface en2

# 3. Put WiFi first so it stays the default route
sudo networksetup -ordernetworkservices \
  "Wi-Fi" "USB 10/100/1000 LAN" "Thunderbolt Bridge"
```

**Make it persistent across reboots:**

```bash
# Create the route script (replace en2 with your interface)
sudo tee /usr/local/bin/p2l-routes.sh > /dev/null << 'EOF'
#!/bin/bash
sleep 5   # wait for USB-LAN interface to be ready
/sbin/route add -host 192.168.1.254 -interface en2 2>/dev/null || true
EOF
sudo chmod +x /usr/local/bin/p2l-routes.sh

# Register as a system startup daemon
sudo tee /Library/LaunchDaemons/com.p2l.routes.plist > /dev/null << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.p2l.routes</string>
    <key>ProgramArguments</key>
    <array>
        <string>/usr/local/bin/p2l-routes.sh</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>StandardOutPath</key>
    <string>/var/log/p2l-routes.log</string>
    <key>StandardErrorPath</key>
    <string>/var/log/p2l-routes.log</string>
</dict>
</plist>
EOF

sudo launchctl load /Library/LaunchDaemons/com.p2l.routes.plist
```

---

#### Step 3 — Verify both paths work
```bash
# Default route must go via en0 (WiFi), not USB-LAN
netstat -rn | grep default
# Expected: default  <wifi-gateway>  UGScg  en0

# Aioi gateway reachable via USB-LAN
ping -c 4 192.168.1.254

# TCP port 5003 open
nc -zv 192.168.1.254 5003
# Expected: Connection to 192.168.1.254 port 5003 [tcp] succeeded!

# Internet still works
ping -c 2 8.8.8.8
```

If `nc` reports `No route to host`, re-apply the static IP and host route (Steps 2–3).

---

### Linux

#### Step 1 — Identify the USB-LAN interface
```bash
ip link show   # look for the new interface after plugging in the adapter (e.g. eth1, usb0)
```

#### Step 2 — Assign a static IP
```bash
# nmcli (persistent across reboots)
sudo nmcli con add type ethernet ifname eth1 \
  ipv4.method manual ipv4.addresses 192.168.1.100/24 \
  ipv4.never-default yes   # prevents this interface from becoming the default route

# ip (temporary — lost on reboot)
sudo ip addr add 192.168.1.100/24 dev eth1
sudo ip link set eth1 up
```

#### Step 3 — Verify
```bash
ping -c 4 192.168.1.254
nc -zv 192.168.1.254 5003
ping -c 2 8.8.8.8
```

---

### Firewall check (if nc still fails)
```bash
# macOS
sudo /usr/libexec/ApplicationFirewall/socketfilterfw --getglobalstate

# Linux — iptables / ufw
sudo iptables -L OUTPUT -n | grep 5003
sudo ufw status
```

---

## 6. Command Reference

Every command connects to the gateway, sends the `Z` initialization command (once per process), then executes. Press **Ctrl+C** at any time for a clean shutdown.

---

### `light` — Individual module control

```bash
# Turn on a module
./p2l light on  <address> <quantity>
./p2l light on  0001 5                     # green, steady
./p2l light on  0002 3 --color Red
./p2l light on  0003 10 --color Yellow --flash

# Turn off a single module
./p2l light off <address>
./p2l light off 0001

# Terminate all modules (D00)
./p2l light terminate

# Terminate a specific block
./p2l light terminate --block 01
```

**Colors:** `Off` `Red` `Green` `Blue` `Yellow` `Cyan` `Magenta` `White`

---

### `light set` — Adjust module parameters

Reconfigures an active or standby module's LED colour, flash mode, 7-segment display mode,
buzzer, and after-CONFIRM behaviour **in-place** without re-instructing the module.

```bash
# Read-only: show current parameters
./p2l light set 0001

# Change colour and LED mode only (all other params unchanged)
./p2l light set 0001 --color Red --mode Flash

# Change the displayed quantity
./p2l light set 0001 --display 42

# Full parameter override
./p2l light set 0001 \
  --color Yellow \
  --mode Flash \
  --seg On \
  --buz On \
  --after-confirm Off \
  --buz-confirm On \
  --display 7
```

| Option | Values | Default (from registry) |
|---|---|---|
| `--color` | `Off Red Green Blue Yellow Cyan Magenta White` | current |
| `-m, --mode` | `Off On Flash HighFlash` | current |
| `--seg` | `Off On Flash` | current |
| `--buz` | `Off On Flash` | current |
| `--after-confirm` | `Off On Flash NoChange` | current |
| `--buz-confirm` | `Off On Flash` | current |
| `-d, --display` | integer | current displayed value |

Omitted options are read from the module's current registry state and sent unchanged.
Produces a before/after diff table before sending.

---

### `pick` — Batch picking session

Lights up multiple modules simultaneously using a P1 block command. Each module must be
confirmed (button press) before the batch is considered complete.

```bash
./p2l pick "0001:5,0002:3,0003:10"
./p2l pick "0001:5,0002:3" --color Yellow --block 01
```

Format: comma-separated `<address>:<quantity>` pairs.

---

### `devices` — Scan and list

Probes a range of module addresses via `Az` (status inquiry) and lists all that respond.
Discovered modules are written into the registry immediately.

```bash
./p2l devices                          # scan 0001–0099
./p2l devices --from 1 --to 200        # custom range
./p2l devices --from 1 --to 50 -c 8   # 8 concurrent queries
```

| Option | Default | Description |
|---|---|---|
| `--from` | 1 | First address to probe |
| `--to` | 99 | Last address to probe (inclusive) |
| `-c, --concurrency` | 4 | Parallel Az queries (max 16) |

Outputs a rounded table: **Address · Status · Color · Mode · Display · Last Instruction**.
Absent modules (timeout or `?` response) are silently skipped.

---

### `export` — Export to CSV

Writes all currently tracked modules to a UTF-8 CSV file. The file includes a self-documenting
comment header with valid values and usage instructions.

```bash
./p2l export                              # default filename: devices_YYYYMMDD_HHmmss.csv
./p2l export --output shelf-a.csv         # explicit path

# Scan first, then export (combines devices + export in one step)
./p2l export --scan
./p2l export --scan --from 1 --to 200 --output warehouse.csv
```

| Option | Default | Description |
|---|---|---|
| `-o, --output` | `devices_YYYYMMDD_HHmmss.csv` | Output file path |
| `--scan` | off | Scan for modules before exporting |
| `--from` | 1 | Scan start address (requires `--scan`) |
| `--to` | 99 | Scan end address (requires `--scan`) |
| `-c, --concurrency` | 4 | Concurrent scan queries (requires `--scan`) |

Export does **not** require a live gateway connection unless `--scan` is used.

---

### `import` — Bulk apply from CSV

Reads a CSV file, validates each row, shows a preview table, then applies the changes to hardware.

```bash
./p2l import devices.csv               # preview → confirm → apply
./p2l import devices.csv --dry-run     # preview only, no hardware writes
./p2l import devices.csv --yes         # apply without confirmation prompt
./p2l import devices.csv --skip-invalid  # skip bad rows instead of aborting
```

| Option | Description |
|---|---|
| `--dry-run` | Show preview table and exit (exit 0) — no commands sent |
| `-y, --yes` | Skip the `Apply N configurations to hardware?` prompt |
| `--skip-invalid` | Skip rows with invalid field values; default aborts on first error |

**Import rules:**
- Column order is flexible (columns are matched by header name, case-insensitive)
- Comment lines (`#`) and blank lines are ignored
- An empty field means *keep current registry value*; if the module is unknown, the default for that parameter is used
- The `Status` column is informational and ignored on import
- Addresses are left-padded to 4 digits automatically (`1` → `0001`)
- Exit code `0` = all rows applied; `1` = one or more failures

---

## 7. Bulk CSV Workflow

The export → edit → import cycle lets you configure many modules offline (e.g. in Excel or any text editor) and push the changes to hardware in one step.

### 1 · Scan and export
```bash
./p2l export --scan --output warehouse.csv
```

### 2 · Edit the file
Open `warehouse.csv` in any editor or spreadsheet. The file contains a comment header explaining valid values:

```csv
# P2L Device Configuration Export
# Gateway:  192.168.1.254:5003
# Exported: 2026-03-11 08:30:00 UTC
#
# VALID VALUES:
#   Color:          Off Red Green Blue Yellow Cyan Magenta White
#   LedMode:        Off On Flash HighFlash
#   ...
#
Address,Display,Color,LedMode,SegMode,BuzMode,AfterConfirm,BuzAfterConfirm,Status,Label
0001,5,Green,On,On,Off,Off,Off,Standby,Shelf A1
0002,10,Red,Flash,On,Off,Off,Off,Standby,Shelf A2
0003,3,Yellow,Flash,On,On,Off,On,Standby,Shelf B1
```

**Tips:**
- Leave `Display` blank to keep the currently displayed quantity unchanged.
- Leave any other parameter blank to preserve its current value.
- Delete rows you don't want to reconfigure.
- The `Status` column is read-only; editing it has no effect.

### 3 · Dry-run preview
```bash
./p2l import warehouse.csv --dry-run
```
Validates all rows and shows the preview table without touching hardware.

### 4 · Apply
```bash
./p2l import warehouse.csv --yes
```
Sends mode + instruction commands sequentially for each valid row.
A progress bar tracks completion; any per-row failures are reported in the summary.

---

## 8. Audit Logs

All hardware TX/RX frames are written to rotating files in the `logs/` directory.
Rotate at **10 MB** by default (configurable via `MaxLogFileSizeBytes`).
These logs are the primary evidence source for mis-pick investigations.

```bash
ls -lh logs/
tail -f logs/p2l_*.log
```

- File naming: `p2l_YYYYMMDD_HH.log` (rotates hourly or on size limit)
- Hardware traffic level: `Debug`
- Console level: `Information` and above

To see all protocol-level TX/RX in the console, set `P2L.Hardware` log level to `Debug` in `appsettings.json` (already the default).

---

## 9. MQTT Bridge

The optional MQTT bridge lets external systems (WMS, ERP, automation scripts) control lights and receive confirmation events over a standard MQTT broker — fully encrypted with TLS.

### Architecture

```
External System
    │  publish  p2l/commands   (JSON command)
    ▼
[ MQTT Broker ]  ←→  TLS 8883
    │  subscribe p2l/commands
    ▼
[ MqttBridgeService ]
    ├── MqttCommandDispatcher → PickingEngine → AioiGateway → Hardware
    └── MqttEventPublisher   ← PickingEngine events → p2l/events
                                                     → p2l/status (LWT)
```

- **Command flow:** broker → P2L → Aioi hardware
- **Event flow:** hardware confirmation → P2L → broker
- **Reconnect:** exponential back-off 2 s → 2 min on broker disconnect
- **LWT:** broker publishes `bridge_status { online: false }` automatically on ungraceful disconnect

---

### HiveMQ Cloud Setup (free managed broker)

[HiveMQ Cloud](https://www.hivemq.com/mqtt-cloud-broker/) provides a free managed MQTT broker
with TLS already configured — no certificates to manage. The free tier supports up to
100 concurrent connections and is suitable for warehouse deployments.

#### Step 1 — Create a free account

Go to **console.hivemq.cloud** and sign up. After email verification you land on the
**Clusters** overview.

#### Step 2 — Create a cluster

Click **Create cluster**, choose the **Free** tier, select the nearest region, and click
**Create**. Cluster creation takes ~1 minute.

Once ready, the cluster card shows a hostname like:

```
xxxxxxxxxxxxxxxx.s1.eu.hivemq.cloud
```

Copy that hostname — you will use it as `Mqtt.Host`.

#### Step 3 — Create MQTT credentials

In the cluster, open the **Access Management** tab → **Credentials** → **Add new credentials**.

- Enter a **username** (e.g. `p2l`)
- Enter a **password** (store it somewhere safe — it is shown once)
- Click **Save**

#### Step 4 — Verify connectivity (optional but recommended)

Install [MQTT Explorer](https://mqtt-explorer.com) or use `mosquitto_pub`:

```bash
# macOS / Linux — brew install mosquitto
mosquitto_pub \
  -h xxxxxxxxxxxxxxxx.s1.eu.hivemq.cloud \
  -p 8883 \
  --cafile ""  \
  -u p2l -P secret \
  -t p2l/test -m "hello" \
  --tls-version tlsv1.2
```

Expected output: `Client mosq-xxxx sending CONNECT` → no error = broker is reachable.

#### Step 5 — Configure P2L

Edit `appsettings.json` (or set environment variables) with the cluster details:

```json
{
  "Mqtt": {
    "Enabled":  true,
    "Host":     "xxxxxxxxxxxxxxxx.s1.eu.hivemq.cloud",
    "Port":     8883,
    "ClientId": "p2l-bridge",
    "Username": "p2l",
    "Password": "secret",
    "CommandTopic": "p2l/commands",
    "EventTopic":   "p2l/events",
    "StatusTopic":  "p2l/status",
    "Tls": {
      "Enabled":             true,
      "CaCertPath":          null,
      "AllowUntrustedCerts": false
    }
  }
}
```

> HiveMQ Cloud uses a **Let's Encrypt** certificate trusted by all OS trust stores.
> Leave `CaCertPath` null and `AllowUntrustedCerts: false` — no extra cert files needed.

#### Step 6 — Run P2L

```bash
./p2l
```

Expected log output on startup:

```
[MQTT] Bridge starting → xxxxxxxxxxxxxxxx.s1.eu.hivemq.cloud:8883 (TLS=True)
[MQTT] Connected to xxxxxxxxxxxxxxxx.s1.eu.hivemq.cloud:8883
```

Subscribe to all P2L topics to watch events in real time:

```bash
mosquitto_sub \
  -h xxxxxxxxxxxxxxxx.s1.eu.hivemq.cloud \
  -p 8883 \
  -u p2l -P secret \
  -t "p2l/#" -v \
  --tls-version tlsv1.2
```

#### Step 7 — Send a test command

In a second terminal, publish a `light_on` command:

```bash
mosquitto_pub \
  -h xxxxxxxxxxxxxxxx.s1.eu.hivemq.cloud \
  -p 8883 \
  -u p2l -P secret \
  -t p2l/commands \
  --tls-version tlsv1.2 \
  -m '{"type":"light_on","address":"0001","quantity":5,"color":"Green"}'
```

The `mosquitto_sub` terminal should immediately show a `light_changed` event, and the physical
module at address `0001` lights up. When the operator presses CONFIRM, a `confirmed` event
appears on `p2l/events`.

---

### Enable the bridge

Set `Mqtt.Enabled: true` in `appsettings.json` (or via environment variable):

```json
{
  "Mqtt": {
    "Enabled":      true,
    "Host":         "mqtt.example.com",
    "Port":         8883,
    "ClientId":     "p2l-bridge",
    "Username":     "p2l",
    "Password":     "secret",
    "CommandTopic": "p2l/commands",
    "EventTopic":   "p2l/events",
    "StatusTopic":  "p2l/status",
    "Tls": {
      "Enabled":             true,
      "CaCertPath":          null,
      "ClientCertPath":      null,
      "ClientKeyPath":       null,
      "CertPassword":        null,
      "AllowUntrustedCerts": false
    }
  }
}
```

Or via environment variables:

```bash
export P2L_Mqtt__Enabled=true
export P2L_Mqtt__Host=mqtt.example.com
export P2L_Mqtt__Username=p2l
export P2L_Mqtt__Password=secret
./p2l
```

### TLS configuration

| Scenario | Settings |
|---|---|
| Public broker with valid cert (e.g. Let's Encrypt) | `Tls.Enabled: true`, leave `CaCertPath` null — OS trust store is used |
| Self-signed / private CA | Set `Tls.CaCertPath` to your CA `.crt` file |
| Mutual TLS (client cert) — PFX | Set `ClientCertPath` to `.pfx`, `CertPassword` if encrypted |
| Mutual TLS (client cert) — PEM | Set `ClientCertPath` to cert `.pem`, `ClientKeyPath` to key `.pem` |
| Dev/test only — skip cert validation | `AllowUntrustedCerts: true` — **never use in production** |

### MQTT Topics

| Topic | Direction | Description |
|---|---|---|
| `p2l/commands` | subscribe | Incoming light control commands (QoS 1) |
| `p2l/events` | publish | Confirmation and state-change events (QoS 1) |
| `p2l/status` | publish | Bridge health + Last Will Testament (QoS 1, retain) |

### Command messages

All commands are JSON with a `type` discriminator field. Publish to `p2l/commands`.

#### `light_on` — Light a single module
```json
{
  "type":          "light_on",
  "correlationId": "order-42",
  "address":       "0003",
  "quantity":      5,
  "color":         "Green",
  "ledMode":       "On",
  "blockNo":       "00"
}
```

| Field | Required | Default | Description |
|---|---|---|---|
| `address` | yes | — | 4-digit module address |
| `quantity` | yes | — | Number to display |
| `color` | no | `Green` | `Off Red Green Blue Yellow Cyan Magenta White` |
| `ledMode` | no | `On` | `On` or `Flash` |
| `blockNo` | no | `"00"` | Block number (matches gateway block) |
| `correlationId` | no | null | Echoed back in `confirmed` event |

#### `light_off` — Extinguish a single module
```json
{
  "type":    "light_off",
  "address": "0003"
}
```

#### `batch_start` — Light multiple modules simultaneously
```json
{
  "type":    "batch_start",
  "blockNo": "01",
  "color":   "Yellow",
  "ledMode": "Flash",
  "items": [
    { "address": "0001", "quantity": 5,  "label": "Shelf A1" },
    { "address": "0002", "quantity": 10, "label": "Shelf A2" },
    { "address": "0003", "quantity": 3  }
  ]
}
```

#### `batch_finish` — Terminate a block (turn off all lights in block)
```json
{
  "type":    "batch_finish",
  "blockNo": "01"
}
```

#### `terminate_all` — Emergency stop: extinguish all modules
```json
{
  "type": "terminate_all"
}
```

### Event messages

P2L publishes events to `p2l/events`. All events include `type` and `timestamp` (UTC ISO-8601).

#### `confirmed` — Operator pressed CONFIRM on a module
```json
{
  "type":            "confirmed",
  "timestamp":       "2026-03-11T08:30:00.000Z",
  "address":         "0003",
  "status":          "Confirmed",
  "adjustedDisplay": null,
  "correlationId":   "order-42"
}
```

#### `light_changed` — Module state changed
```json
{
  "type":         "light_changed",
  "timestamp":    "2026-03-11T08:30:00.000Z",
  "address":      "0003",
  "moduleStatus": "Active",
  "color":        "Green",
  "ledMode":      "On",
  "displayData":  "00005",
  "blockNo":      "00"
}
```

#### `batch_completed` — All modules in the active batch confirmed
```json
{
  "type":      "batch_completed",
  "timestamp": "2026-03-11T08:30:05.000Z"
}
```

#### `bridge_status` — Bridge health / Last Will Testament
```json
{
  "type":             "bridge_status",
  "timestamp":        "2026-03-11T08:30:00.000Z",
  "online":           true,
  "gatewayConnected": true
}
```

Published on connect and on gateway connection changes. `online: false` is also the LWT payload — the broker delivers it automatically if P2L disconnects ungracefully.

---

## 10. systemd Service (Linux 24/7 Operation)

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
# Optional MQTT bridge
Environment=P2L_Mqtt__Enabled=true
Environment=P2L_Mqtt__Host=mqtt.example.com
Environment=P2L_Mqtt__Username=p2l
Environment=P2L_Mqtt__Password=secret

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable p2l
sudo systemctl start  p2l
sudo systemctl status p2l
```

---

## 11. Protocol Notes

### Initialization sequence
Every command sends `Z` (full initialization) once before any operation instruction. This resets all modules to standby. Subsequent commands within the same process skip the second `Z` (idempotent via `EnsureInitializedAsync`).

### `light on` protocol (single module)
```
Z  →  P1 <blockNo> <addr><display>   (block operation instruction, steady)
   or
   →  FP1 <blockNo> <addr><display>  (block operation instruction, flash)
```

`P1` and `FP1` are block commands — even single-module instructions use the block framing.
Flash mode is encoded in the command prefix (`FP1` / `FP5`) rather than a separate mode command.

### `pick` protocol (batch)
```
Z  →  P1  <blockNo> <addr1><disp1> <addr2><disp2> ...   (steady block instruction)
   or
   →  FP1 <blockNo> <addr1><disp1> <addr2><disp2> ...   (flash block instruction)
```

Overwrite (rewrite active instructions without re-confirming) uses `P5` / `FP5`.

### Confirmation interrupt
The gateway pushes an unsolicited `t` packet when an operator presses CONFIRM. P2L ACKs it immediately with `O` (highest-priority response).

### Nack handling
The controller responds `o` (ACK) or `n` (NACK) to every command. P2L throws an error on `n`:
```
Error: Gateway rejected command 'P10000001000005' with 'n' — check command format/address.
```
If you see this, enable `Debug` logging to inspect the raw TX bytes and compare against the Aioi Command Reference.

### Forbidden bytes
Commands must **not** contain `0x02` (STX) or `0x03` (ETX) in data bytes — these are the packet frame delimiters. Additionally, avoid `0x11` (XON) and `0x13` (XOFF) in data bytes — some gateway firmware strips these as legacy serial flow-control characters, silently corrupting packet length. P2L uses `NoChange` (0xFF) instead of `Off` (0x11) for after-confirm LED states to avoid this issue.

---

## 12. Quick Troubleshooting Reference

| Symptom | Likely cause | Fix |
|---|---|---|
| `Cannot connect to 192.168.1.254:5003` (repeated) | USB-LAN has no IP or route missing | Re-run `networksetup -setmanual` and host route steps in §5 |
| `nc: No route to host 192.168.1.254` | USB-LAN static IP not applied or host route lost after reboot | Re-apply static IP; re-run `route add -host 192.168.1.254 -interface en2`; install launchd plist for persistence |
| Gateway reachable but internet broken via WiFi | WiFi and USB-LAN on same `192.168.1.x` subnet — USB-LAN took the default route | Follow Case B in §5: use host route for gateway, keep WiFi first in service order |
| Internet works but gateway unreachable after reboot | Host route lost on reboot | Install the launchd plist in §5 Case B to restore it automatically |
| `Timeout waiting for ACK` on `Z` | Gateway powered off or busy | Power-cycle gateway; check physical LED status |
| `Gateway rejected command ... with 'n'` | Invalid command format or address | Enable Debug logging; check address range; verify hardware model matches TW/AW series |
| `devices` scan returns 0 modules | Modules off, wrong address range, or wiring fault | Widen `--from`/`--to`; verify wiring |
| `light set` shows no change on hardware | Module already terminated (Standby) | Re-instruct with `light on` first, then use `light set` |
| Light doesn't turn on, no error shown | Module not at that address | Run `./p2l devices` to discover actual addresses |
| `import` aborts with "Unknown Color value" | Typo in CSV Color column | Fix value; re-run with `--dry-run` to verify |
| Build error `clang not found` | Native AOT requires C compiler | Install Xcode CLT (macOS) or `clang` (Linux) |
| `appsettings.json not found` warning | Binary moved without config file | Keep `appsettings.json` next to the `p2l` binary |
| `[MQTT] Connection failed` (repeated) | Broker unreachable or TLS mismatch | Check host/port; verify cert chain; try `AllowUntrustedCerts: true` in dev |
| MQTT commands received but no hardware action | Bridge connected but gateway disconnected | Check `bridge_status.gatewayConnected` on `p2l/status` topic |
| `bridge_status { online: false }` on broker | LWT triggered — P2L crashed or lost network | Restart P2L service; bridge auto-reconnects on next start |
| MQTT events published but `correlationId` missing | Command sent without `correlationId` field | Include `correlationId` in `light_on` command to correlate with `confirmed` events |
