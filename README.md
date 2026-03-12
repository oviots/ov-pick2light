# Pick2Light (P2L) — Aioi Light Module Controller

A cross-platform .NET 10 CLI for controlling Aioi Systems L-PICK light modules over TCP.
Runs as one-shot commands, a live dashboard, or a sequential guided-picking runner.
Publishes to a **single self-contained binary** with no runtime dependency.

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Repository Layout](#2-repository-layout)
3. [Build](#3-build) — includes Native AOT / `rd.xml` note
4. [Configuration](#4-configuration)
5. [USB-LAN Connection Verification](#5-usb-lan-connection-verification)
6. [Command Reference](#6-command-reference)
   - [light on / off / terminate](#light--individual-module-control)
   - [light set — adjust parameters](#light-set--adjust-module-parameters)
   - [pick — batch session](#pick--batch-picking-session)
   - [run — sequential CSV pick](#run--sequential-csv-pick)
   - [devices — scan & list](#devices--scan-and-list)
   - [export — CSV export](#export--export-to-csv)
   - [import — CSV bulk apply](#import--bulk-apply-from-csv)
7. [Bulk CSV Workflow](#7-bulk-csv-workflow)
8. [Audit Logs](#8-audit-logs)
9. [MQTT Bridge](#9-mqtt-bridge)
   - [Offline Buffering](#offline-buffering)
   - [Client Examples (Python / Java)](#client-examples--offline-buffer-python--java)
10. [Self-Healing & 24/7 Operation](#10-self-healing--247-operation)
11. [Protocol Notes](#11-protocol-notes)
12. [Troubleshooting](#12-quick-troubleshooting-reference)

---

## 1. Prerequisites

### .NET SDK
P2L targets **net10.0**. The SDK is installed at `~/.dotnet/dotnet` and is **not** in the system PATH.

```bash
~/.dotnet/dotnet --version   # Expected: 10.0.x
```

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
    Transport/         TcpTransport (Channel-based, auto-reconnect, SemaphoreSlim stream lock)
    AioiGateway.cs     High-level domain API — SendImmediateAsync for O-ACK priority
  P2L.Core/            Business logic
    LightModuleRegistry.cs   Thread-safe module state (ConcurrentDictionary + StateChanged event)
    PickingEngine.cs         Batch orchestration, confirmation tracking
    Services/
      HeartbeatService.cs    Keep-alive loop (Az every 30 s)
      DeviceScanService.cs   Concurrent Az range scan
      PreemptionGate.cs      Two-priority gate: MQTT commands preempt CSV runner
      CsvPickTaskRunner.cs   Sequential CSV pick runner with auto-delete and MQTT events
      WatchdogService.cs     Monitors gateway connectivity; triggers restart after timeout
  P2L.Mqtt/            Optional MQTT bridge (MQTTnet v5, TLS)
    Configuration/     MqttSettings, MqttTlsSettings
    Messages/          Commands (light_on, batch_start, csv_run, csv_control, …)
                       Events   (confirmed, light_changed, task_event,
                                 batch_completed, bridge_status)
                       P2LMqttJsonContext (source-gen JSON context for AOT)
    MqttCommandDispatcher.cs  Deserializes commands → PickingEngine / CsvPickTaskRunner
    MqttEventPublisher.cs     Subscribes to engine + runner events → broker;
                              offline JSONL buffer when disconnected, auto-replay on reconnect
    MqttBridgeService.cs      Lifecycle owner: TLS connect, reconnect loop, LWT
  P2L.CLI/             Spectre.Console CLI, DI wiring
    Commands/          RootCommand, LightCommands, RunPickCommand,
                       ListDevicesCommand, ConfigureDeviceCommand,
                       ExportCommand, ImportCommand
    Infrastructure/    TypeRegistrar, RotatingFileLogger, ParameterParser,
                       DeviceConfigCsvService, SelfHealHandler
    Configuration/     AppSettings, GatewaySettings, SelfHealSettings
    appsettings.json
publish/               Published binary output (gitignored)
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

### Self-contained single-file binary (production)

```bash
# macOS Apple Silicon
~/.dotnet/dotnet publish src/P2L.CLI/P2L.CLI.csproj \
  -c Release -r osx-arm64 -o ./publish

# macOS x64
~/.dotnet/dotnet publish src/P2L.CLI/P2L.CLI.csproj \
  -c Release -r osx-x64 -o ./publish/osx-x64

# Linux x64 — warehouse industrial PC / Raspberry Pi
~/.dotnet/dotnet publish src/P2L.CLI/P2L.CLI.csproj \
  -c Release -r linux-x64 -o ./publish/linux-x64

# Windows x64
~/.dotnet/dotnet publish src/P2L.CLI/P2L.CLI.csproj \
  -c Release -r win-x64 -o ./publish/win-x64
```

> `appsettings.json` must stay beside the binary — it is read from `AppContext.BaseDirectory` at startup.

### Native AOT and Spectre.Console reflection

P2L is published with `PublishAot=true`. Spectre.Console.Cli uses runtime reflection
(`GetInterfaces` / `GetGenericArguments`) to discover the `Settings` type of each command;
without explicit preservation the AOT linker trims that metadata and the binary exits silently
with no output (even for `--help`).

`src/P2L.CLI/rd.xml` lists every command and settings class with `Dynamic="Required All"` and
is wired into the project via `<RdXmlFile Include="rd.xml" />`. **If you add a new command,
add its type and nested `Settings` type to `rd.xml`.**

```xml
<Type Name="P2L.CLI.Commands.MyNewCommand"          Dynamic="Required All" />
<Type Name="P2L.CLI.Commands.MyNewCommand+Settings" Dynamic="Required All" />
```

If the binary starts producing no output and `crash_history.jsonl` shows
`Could not get settings type for command of type '...'`, a command type is missing from `rd.xml`.

---

## 4. Configuration

Edit `appsettings.json` before publishing, or override at runtime with environment variables:

```json
{
  "Gateway": {
    "Host":    "192.168.1.254",
    "Port":    5003,
    "BlockNo": "00"
  },
  "LogDirectory":        "logs",
  "MaxLogFileSizeBytes": 10485760,
  "SelfHeal": {
    "Enabled":               true,
    "MaxRestarts":           5,
    "RestartWindowMinutes":  10,
    "BackOffSeconds":        3,
    "WatchdogTimeoutMinutes": 5
  },
  "Logging": {
    "LogLevel": {
      "Default":           "Information",
      "P2L.Hardware":      "Debug",
      "P2L.Mqtt":          "Debug",
      "Microsoft.Hosting": "Warning"
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

Both adapters on the same subnet confuse macOS routing. Fix: pin only the Aioi gateway IP to the
USB-LAN interface using a host route, leaving WiFi as the default for everything else.

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

---

### `pick` — Batch picking session

Lights up multiple modules simultaneously using a P1 block command. Each module must be
confirmed (button press) before the batch is considered complete.

```bash
# All modules use the same color
./p2l pick "0001:5,0002:3,0003:10"
./p2l pick "0001:5,0002:3" --color Yellow --block 01

# Per-entry color override (append :color as a third token)
./p2l pick "0001:5:Red,0002:3,0003:10:Blue" --color Green
#          ^^^^^^^^^^  ^^^^^^  ^^^^^^^^^^^
#          Red         Green   Blue
#                      ↑ no color token → falls back to --color
```

**Format:** `<address>:<quantity>[:<color>]` — comma-separated. Color is case-insensitive and
optional per entry; omitted entries use the global `--color` (default `Green`).

---

### `run` — Sequential CSV pick

Processes a CSV file of pick tasks **one row at a time**:

1. Light the module specified in the current row.
2. Wait for the operator to press **CONFIRM** on that module.
3. **Auto-delete** the confirmed row from the CSV file.
4. Publish `task_started` / `task_confirmed` events to MQTT.
5. Advance to the next row.

Real-time MQTT commands arriving on `p2l/commands` are given **strict priority** — the runner
pauses between tasks to let any in-flight MQTT command complete before starting the next.

```bash
./p2l run tasks.csv                  # default color = Green
./p2l run tasks.csv --color Red      # global default color
./p2l run tasks.csv --color Green --block 01
```

**CSV format** (`tasks.csv`):

```csv
# address,quantity[,color][,label]
# Color is optional — blank entries use the --color default.
# Lines starting with # and the header row are ignored.
address,quantity,color,label
0001,5,Red,Part-A
0002,3,,Part-B
0003,10,Blue,Part-C
```

| Option | Default | Description |
|---|---|---|
| `--color` | `Green` | Default color for rows that omit the color column |
| `--block` | `00` | Block number passed to the gateway |

**Resumability:** completed rows are deleted immediately on confirmation. If the process is
stopped and restarted, it continues from the first remaining row.

**MQTT control while running** (publish to `p2l/commands`):

```json
{ "type": "csv_control", "action": "pause"  }
{ "type": "csv_control", "action": "resume" }
{ "type": "csv_control", "action": "cancel" }
```

---

### `devices` — Scan and list

Probes a range of module addresses via `Az` (status inquiry) and lists all that respond.

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

---

### `export` — Export to CSV

Writes all currently tracked modules to a UTF-8 CSV file.

```bash
./p2l export                              # default filename: devices_YYYYMMDD_HHmmss.csv
./p2l export --output shelf-a.csv
./p2l export --scan                       # scan first, then export
./p2l export --scan --from 1 --to 200 --output warehouse.csv
```

---

### `import` — Bulk apply from CSV

Reads a device-configuration CSV, validates rows, shows a preview, then applies to hardware.

```bash
./p2l import devices.csv               # preview → confirm → apply
./p2l import devices.csv --dry-run     # preview only, no hardware writes
./p2l import devices.csv --yes         # apply without confirmation prompt
./p2l import devices.csv --skip-invalid  # skip bad rows instead of aborting
```

**Import rules:**
- Column order is flexible (matched by header name, case-insensitive)
- Comment lines (`#`) and blank lines are ignored
- An empty field means *keep current registry value*
- The `Status` column is informational and ignored on import
- Addresses are left-padded to 4 digits automatically (`1` → `0001`)

---

## 7. Bulk CSV Workflow

### 1 · Scan and export
```bash
./p2l export --scan --output warehouse.csv
```

### 2 · Edit the file

```csv
# P2L Device Configuration Export
# VALID VALUES:
#   Color:  Off Red Green Blue Yellow Cyan Magenta White
#   LedMode: Off On Flash HighFlash
#   ...
Address,Display,Color,LedMode,SegMode,BuzMode,AfterConfirm,BuzAfterConfirm,Status,Label
0001,5,Green,On,On,Off,Off,Off,Standby,Shelf A1
0002,10,Red,Flash,On,Off,Off,Off,Standby,Shelf A2
```

### 3 · Dry-run preview
```bash
./p2l import warehouse.csv --dry-run
```

### 4 · Apply
```bash
./p2l import warehouse.csv --yes
```

---

## 8. Audit Logs

All hardware TX/RX frames are written to rotating files in the `logs/` directory.

```bash
ls -lh logs/
tail -f logs/p2l_*.log
```

- File naming: `p2l_YYYYMMDD_HH.log` (rotates hourly or on size limit, default 10 MB)
- Hardware traffic level: `Debug`
- Console level: `Information` and above

---

## 9. MQTT Bridge

The optional MQTT bridge lets external systems (WMS, ERP, automation scripts) control lights
and receive events over a standard MQTT broker with TLS encryption.

### Architecture

```
External System
    │  publish  p2l/commands          (JSON command)
    ▼
[ MQTT Broker ]  ←→  TLS 8883
    │  subscribe p2l/commands
    ▼
[ MqttBridgeService ]
    ├── MqttCommandDispatcher → PreemptionGate → PickingEngine / CsvPickTaskRunner
    │                                                  ↓ hardware
    └── MqttEventPublisher   ← StateChanged / ItemConfirmed / TaskEvent
                                   │
                                   ├─→ p2l/events            (confirmations, task events)
                                   ├─→ p2l/devices/{address} (per-device state, retained)
                                   └─→ p2l/status            (bridge health, LWT, retained)
```

- **Reconnect:** exponential back-off starting at 0.5 s, capped at 2 min
- **LWT:** broker publishes `bridge_status { online: false }` automatically on ungraceful disconnect
- **Priority:** every MQTT command calls `PreemptionGate.Preempt()` before executing and
  `Release()` after — the CSV runner checks the gate between tasks and waits
- **Offline buffering:** when the broker is unreachable, events are written to a local JSONL file
  instead of being dropped; on reconnect they are replayed in order before live events resume

---

### EMQX Cloud Setup (free managed broker)

[EMQX Cloud](https://www.emqx.com/en/cloud) provides a free Serverless tier with TLS included.

#### Step 1 — Create a free deployment

Sign up at **cloud.emqx.com** → **New Deployment** → **Serverless** → select region → **Deploy**.
Once ready the overview shows a hostname like:

```
xxxxxxxx.ala.asia-southeast1.emqxsl.com
```

Copy the hostname — use it as `Mqtt.Host`.

#### Step 2 — Create credentials

In the deployment, open **Access Control** → **Authentication** → **Add**.
Enter a username (e.g. `pick2light`) and password. Click **Confirm**.

#### Step 3 — Configure P2L

```json
{
  "Mqtt": {
    "Enabled":      true,
    "Host":         "xxxxxxxx.ala.asia-southeast1.emqxsl.com",
    "Port":         8883,
    "ClientId":     "p2l-bridge",
    "Username":     "pick2light",
    "Password":     "your-password",
    "CommandTopic":      "p2l/commands",
    "EventTopic":        "p2l/events",
    "DevicesTopic":      "p2l/devices",
    "StatusTopic":       "p2l/status",
    "OfflineBufferPath": "mqtt_offline_buffer.jsonl",
    "Tls": {
      "Enabled":             true,
      "CaCertPath":          null,
      "AllowUntrustedCerts": false
    }
  }
}
```

> EMQX Cloud uses a **Let's Encrypt** certificate trusted by all OS trust stores.
> Leave `CaCertPath` null — no extra cert files needed.

#### Step 4 — Run and verify

```bash
./p2l
```

Expected log output:

```
[MQTT] Bridge starting → xxxxxxxx.ala.asia-southeast1.emqxsl.com:8883 (TLS=True)
[MQTT] Connected to xxxxxxxx.ala.asia-southeast1.emqxsl.com:8883
[MQTT] Published  bridge_status → p2l/status
```

Subscribe to watch all events (MQTT Explorer, EMQX Console WebSocket, or CLI):

```bash
mosquitto_sub -h xxxxxxxx.ala.asia-southeast1.emqxsl.com -p 8883 \
  -u pick2light -P your-password --tls-version tlsv1.2 \
  -t "p2l/#" -v
```

---

### Enable the bridge

```json
{
  "Mqtt": {
    "Enabled":      true,
    "Host":         "mqtt.example.com",
    "Port":         8883,
    "ClientId":     "p2l-bridge",
    "Username":     "p2l",
    "Password":     "secret",
    "CommandTopic":      "p2l/commands",
    "EventTopic":        "p2l/events",
    "DevicesTopic":      "p2l/devices",
    "StatusTopic":       "p2l/status",
    "OfflineBufferPath": "mqtt_offline_buffer.jsonl",
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

### TLS configuration

| Scenario | Settings |
|---|---|
| Public broker with valid cert (Let's Encrypt) | `Tls.Enabled: true`, `CaCertPath: null` — OS trust store |
| Self-signed / private CA | Set `CaCertPath` to your CA `.crt` file |
| Mutual TLS — PFX | Set `ClientCertPath` to `.pfx`, `CertPassword` if encrypted |
| Mutual TLS — PEM | Set `ClientCertPath` to cert `.pem`, `ClientKeyPath` to key `.pem` |
| Dev/test — skip cert validation | `AllowUntrustedCerts: true` — **never use in production** |

---

### Offline Buffering

When the MQTT broker is unreachable P2L never freezes or drops events. Instead the drain loop
writes every pending event to a local **JSONL** file and polls for reconnection every 500 ms.
On reconnect, all buffered lines are replayed to the broker in order before live events resume.
The file is deleted after a complete replay; after a partial replay (e.g. the broker drops again
mid-sync) only the un-synced tail is kept so the next reconnect picks up exactly where it left off.
The buffer also survives process restarts — events written before a crash are replayed on the
next successful connect.

**Buffer file format** (`mqtt_offline_buffer.jsonl`):
```json
{"topic":"p2l/events","retain":false,"payload":"eyJ0eXBlIjoiY29uZmlybWVkIiwi..."}
{"topic":"p2l/devices/0003","retain":true,"payload":"eyJ0eXBlIjoibGlnaHRf..."}
```

Each line stores the pre-serialized payload as base64 together with the target topic and retain
flag — no re-serialization is needed during replay.

**Configuration:**

| Key | Default | Description |
|---|---|---|
| `OfflineBufferPath` | `mqtt_offline_buffer.jsonl` | Path to the JSONL buffer file (relative to working directory) |

Set `OfflineBufferPath` to an empty string `""` to disable file buffering and fall back to the
in-memory channel only (events are dropped when the channel fills up after 1 000 entries).

**Log output during offline/replay:**
```
[MQTT] Offline buffered → p2l/events
[MQTT] Replaying 42 offline buffered event(s) from mqtt_offline_buffer.jsonl
[MQTT] Buffer replay complete — 42 event(s) synced
```

---

### MQTT Topics

| Topic | Direction | Retain | Description |
|---|---|---|---|
| `p2l/commands` | subscribe | — | Incoming light control commands (QoS 1) |
| `p2l/events` | publish | no | Confirmations, task events, batch completed (QoS 1) |
| `p2l/devices/{address}` | publish | **yes** | Latest state per module — updated on every change |
| `p2l/status` | publish | **yes** | Bridge health + Last Will Testament (QoS 1) |

**Receiving queued events:** subscribers that connect with `CleanStart: false` and a
`SessionExpiryInterval > 0` will receive all `p2l/events` messages published while offline.

**Per-device state:** subscribe to `p2l/devices/+` (wildcard) to receive retained state for all
modules immediately on connect, even without pressing a button.

---

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
| `blockNo` | no | `"00"` | Block number |
| `correlationId` | no | null | Echoed back in `confirmed` event |

#### `light_off` — Extinguish a single module
```json
{ "type": "light_off", "address": "0003" }
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

#### `batch_finish` — Terminate a block
```json
{ "type": "batch_finish", "blockNo": "01" }
```

#### `terminate_all` — Emergency stop
```json
{ "type": "terminate_all" }
```

#### `get_snapshot` — Dump current state of all known modules
Triggers one `light_changed` event per tracked module on `p2l/devices/{address}`.
```json
{ "type": "get_snapshot" }
```

#### `csv_run` — Start a sequential CSV pick task
```json
{
  "type":    "csv_run",
  "file":    "tasks.csv",
  "color":   "Green",
  "blockNo": "00"
}
```

The `file` path is resolved on the machine running P2L. The runner processes one row at a time,
waits for confirmation, deletes the row, then advances. Real-time MQTT commands keep strict
priority.

#### `csv_control` — Pause / resume / cancel the CSV runner
```json
{ "type": "csv_control", "action": "pause"  }
{ "type": "csv_control", "action": "resume" }
{ "type": "csv_control", "action": "cancel" }
```

---

### Event messages

P2L publishes events to `p2l/events`. All events include `type` and `timestamp` (UTC ISO-8601).

#### `confirmed` — Operator pressed CONFIRM on a module
```json
{
  "type":            "confirmed",
  "timestamp":       "2026-03-11T08:30:00.000Z",
  "address":         "0003",
  "status":          "Normal",
  "adjustedDisplay": null,
  "correlationId":   "order-42"
}
```

#### `batch_completed` — All modules in the active batch confirmed
```json
{
  "type":      "batch_completed",
  "timestamp": "2026-03-11T08:30:05.000Z"
}
```

#### `task_started` — CSV runner lit a module
```json
{
  "type":      "task_started",
  "timestamp": "2026-03-11T08:30:00.000Z",
  "address":   "0001",
  "quantity":  5,
  "color":     "Red",
  "label":     "Part-A"
}
```

#### `task_confirmed` — Operator confirmed a CSV task row (row deleted from CSV)
```json
{
  "type":      "task_confirmed",
  "timestamp": "2026-03-11T08:30:08.000Z",
  "address":   "0001",
  "quantity":  5,
  "color":     "Red",
  "label":     "Part-A"
}
```

#### `task_error` — Error processing a CSV row (row skipped)
```json
{
  "type":      "task_error",
  "timestamp": "2026-03-11T08:30:01.000Z",
  "address":   "0001",
  "quantity":  5,
  "color":     "Red",
  "label":     "Part-A"
}
```

#### `csv_completed` — All rows in the CSV file have been confirmed
```json
{
  "type":      "csv_completed",
  "timestamp": "2026-03-11T08:35:00.000Z",
  "address":   ""
}
```

#### `light_changed` — Module state changed (published to `p2l/devices/{address}`)
```json
{
  "type":             "light_changed",
  "timestamp":        "2026-03-11T08:30:00.000Z",
  "address":          "0003",
  "moduleStatus":     "Active",
  "color":            "Green",
  "ledMode":          "On",
  "displayData":      "00005",
  "blockNo":          "00",
  "adjustedQuantity": 5,
  "lastInstructionAt":"2026-03-11T08:30:00.000Z",
  "lastConfirmedAt":  null
}
```

#### `bridge_status` — Bridge health / Last Will Testament (published to `p2l/status`)
```json
{
  "type":             "bridge_status",
  "timestamp":        "2026-03-11T08:30:00.000Z",
  "online":           true,
  "gatewayConnected": true
}
```

Published on connect and on gateway connection changes. `online: false` is also the LWT payload.

---

### Client Examples — Offline Buffer (Python / Java)

Both examples write to the same `mqtt_offline_buffer.jsonl` format P2L uses, so a buffer
written by P2L can be replayed by these clients and vice versa.

#### Python (`paho-mqtt`)

```python
# pip install paho-mqtt
import json, base64, os, time, threading
import paho.mqtt.client as mqtt

BROKER_HOST  = "broker.example.com"
BROKER_PORT  = 8883
BUFFER_FILE  = "mqtt_offline_buffer.jsonl"
EVENT_TOPIC  = "p2l/events"
STATUS_TOPIC = "p2l/status"

# ── Buffer helpers ────────────────────────────────────────────────────────────

def append_to_buffer(topic: str, payload: bytes, retain: bool = False):
    entry = {"Topic": topic, "Retain": retain,
             "Payload": base64.b64encode(payload).decode()}
    with open(BUFFER_FILE, "a") as f:
        f.write(json.dumps(entry) + "\n")

def replay_buffer(client):
    if not os.path.exists(BUFFER_FILE):
        return
    lines = [l.strip() for l in open(BUFFER_FILE) if l.strip()]
    print(f"[replay] {len(lines)} buffered event(s)...")
    for line in lines:
        try:
            e = json.loads(line)
            info = client.publish(e["Topic"], base64.b64decode(e["Payload"]),
                                  qos=1, retain=e.get("Retain", False))
            info.wait_for_publish(timeout=5)
        except Exception as ex:
            print(f"[replay] skipped: {ex}")
    os.remove(BUFFER_FILE)

# ── Publish with fallback ─────────────────────────────────────────────────────

_connected = threading.Event()

def enqueue(topic: str, payload: bytes, retain: bool = False):
    if _connected.is_set():
        client.publish(topic, payload, qos=1, retain=retain)
    else:
        append_to_buffer(topic, payload, retain)

# ── Callbacks ─────────────────────────────────────────────────────────────────

def on_connect(client, userdata, flags, rc, props):
    if rc == 0:
        _connected.set()
        replay_buffer(client)               # flush file before live events
        client.subscribe("p2l/commands", qos=1)

def on_disconnect(client, userdata, flags, rc, props):
    _connected.clear()

def on_message(client, userdata, msg):
    print(f"[command] {json.loads(msg.payload)}")

# ── Setup ─────────────────────────────────────────────────────────────────────

client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2,
                     client_id="p2l-py-client", clean_session=False)
client.on_connect    = on_connect
client.on_disconnect = on_disconnect
client.on_message    = on_message
client.will_set(STATUS_TOPIC,
    json.dumps({"type": "bridge_status", "Online": False, "GatewayConnected": False}),
    qos=1, retain=True)
client.tls_set()        # OS trust store; pass ca_certs= for a custom CA
client.connect_async(BROKER_HOST, BROKER_PORT)
client.loop_start()

# Simulate picking confirmations (works offline — written to file if disconnected)
for i in range(1, 6):
    payload = json.dumps({"type": "confirmed", "address": f"{i:04d}",
                          "status": "Completed"}).encode()
    enqueue(EVENT_TOPIC, payload)
    time.sleep(0.5)

input("Press Enter to stop\n")
client.loop_stop()
client.disconnect()
```

#### Java (Eclipse Paho MQTTv5)

```java
// Maven: org.eclipse.paho:org.eclipse.paho.mqttv5.client:1.2.5
//        org.json:json:20231013
import org.eclipse.paho.mqttv5.client.*;
import org.eclipse.paho.mqttv5.client.persist.MqttDefaultFilePersistence;
import org.eclipse.paho.mqttv5.common.*;
import org.json.JSONObject;
import java.io.*;
import java.nio.file.*;
import java.util.Base64;

public class P2LOfflineBuffer {

    static final String BROKER      = "ssl://broker.example.com:8883";
    static final String CLIENT_ID   = "p2l-java-client";
    static final String EVENT_TOPIC = "p2l/events";
    static final Path   BUFFER_FILE = Path.of("mqtt_offline_buffer.jsonl");

    MqttAsyncClient client;
    volatile boolean connected = false;

    void start() throws Exception {
        var persistence = new MqttDefaultFilePersistence("./mqtt-persist");
        client = new MqttAsyncClient(BROKER, CLIENT_ID, persistence);

        // In-memory disconnected buffer — complements the file layer for brief outages
        var bufOpts = new DisconnectedBufferOptions();
        bufOpts.setBufferEnabled(true);
        bufOpts.setBufferSize(500);
        bufOpts.setPersistBuffer(true);
        client.setBufferOpts(bufOpts);

        client.setCallback(new MqttCallback() {
            public void connectComplete(boolean reconnect, String uri) {
                connected = true;
                try { replayFileBuffer(); } catch (Exception e) { e.printStackTrace(); }
            }
            public void disconnected(MqttDisconnectResponse r) { connected = false; }
            public void messageArrived(String topic, MqttMessage msg) {
                System.out.println("[command] " + new String(msg.getPayload()));
            }
            public void deliveryComplete(IMqttToken t) {}
            public void mqttErrorOccurred(MqttException e) { e.printStackTrace(); }
            public void authPacketArrived(int r, MqttProperties p) {}
        });

        var opts = new MqttConnectionOptions();
        opts.setCleanStart(false);
        opts.setAutomaticReconnect(true);
        var lwtPayload = "{\"type\":\"bridge_status\",\"Online\":false,\"GatewayConnected\":false}"
                         .getBytes();
        opts.setWill("p2l/status", new MqttMessage(lwtPayload, 1, true, null));
        // opts.setSocketFactory(sslContext.getSocketFactory()); // custom CA if needed
        client.connect(opts).waitForCompletion();
    }

    void enqueue(String topic, byte[] payload, boolean retain) {
        if (connected) {
            try { client.publish(topic, new MqttMessage(payload, 1, retain, null)); return; }
            catch (MqttException ignored) {}
        }
        appendToFileBuffer(topic, payload, retain);
    }

    void appendToFileBuffer(String topic, byte[] payload, boolean retain) {
        try {
            var entry = new JSONObject()
                .put("Topic", topic).put("Retain", retain)
                .put("Payload", Base64.getEncoder().encodeToString(payload));
            Files.writeString(BUFFER_FILE, entry + "\n",
                StandardOpenOption.CREATE, StandardOpenOption.APPEND);
        } catch (IOException e) { e.printStackTrace(); }
    }

    void replayFileBuffer() throws Exception {
        if (!Files.exists(BUFFER_FILE)) return;
        var lines = Files.readAllLines(BUFFER_FILE);
        System.out.println("[replay] " + lines.size() + " buffered event(s)...");
        for (String line : lines) {
            if (line.isBlank()) continue;
            var e       = new JSONObject(line);
            var payload = Base64.getDecoder().decode(e.getString("Payload"));
            client.publish(e.getString("Topic"),
                new MqttMessage(payload, 1, e.getBoolean("Retain"), null))
                .waitForCompletion(5_000);
        }
        Files.deleteIfExists(BUFFER_FILE);
        System.out.println("[replay] done");
    }

    public static void main(String[] args) throws Exception {
        var bridge = new P2LOfflineBuffer();
        bridge.start();
        for (int i = 1; i <= 5; i++) {
            bridge.enqueue(EVENT_TOPIC,
                ("{\"type\":\"confirmed\",\"address\":\"" + String.format("%04d", i)
                 + "\",\"status\":\"Completed\"}").getBytes(), false);
            Thread.sleep(500);
        }
        Thread.sleep(2_000);
        bridge.client.disconnect().waitForCompletion();
    }
}
```

**How the two layers interact:**
```
P2L event fires
      │
      ▼
  Connected? ──yes──► publish QoS 1 ──► broker
      │
     no
      ├──► DisconnectedBufferOptions (RAM — survives brief reconnects)
      └──► mqtt_offline_buffer.jsonl (disk — survives process restarts)
                 └─── on connectComplete() → replayFileBuffer() → delete
```

---

## 10. Self-Healing & 24/7 Operation

### In-process self-heal (all platforms)

P2L includes a built-in self-healing subsystem that handles two failure modes:

| Failure mode | Detection | Recovery |
|---|---|---|
| Unhandled exception / crash | `AppDomain.UnhandledException` | Log → back-off → `Process.Start` self |
| Gateway silent / persistent disconnect | `WatchdogService` (5-min timer) | Log → back-off → restart |

**Crash-loop guard:** crash timestamps are persisted to `crash_history.jsonl` next to the binary.
If the process crashes more than `MaxRestarts` times within `RestartWindowMinutes`, the self-restart
is disabled and the process exits cleanly (exit 1) to prevent runaway loops.

**Configuration** (in `appsettings.json`):

```json
"SelfHeal": {
  "Enabled": true,
  "MaxRestarts": 5,
  "RestartWindowMinutes": 10,
  "BackOffSeconds": 3,
  "WatchdogTimeoutMinutes": 5
}
```

Set `WatchdogTimeoutMinutes: 0` to disable the gateway watchdog (keep crash-handler only).
Set `Enabled: false` to turn off the entire subsystem (useful during development).

### systemd Service (Linux 24/7 Operation)

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
Restart=on-failure
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

> **Note:** `Restart=on-failure` (exit codes ≠ 0) complements the in-process self-heal.
> If the crash-loop guard fires (exit 1), systemd also catches it and retries after `RestartSec`.

```bash
sudo systemctl daemon-reload
sudo systemctl enable p2l
sudo systemctl start  p2l
sudo systemctl status p2l
```

For a CSV-based guided pick session that restarts automatically:

```ini
ExecStart=/opt/p2l/p2l run /opt/p2l/tasks.csv --color Green
```

---

## 11. Protocol Notes

### Initialization sequence
Every command sends `Z` once before any operation. This resets all modules to standby.
Subsequent commands within the same process skip the second `Z` (idempotent via `EnsureInitializedAsync`).

### `light on` protocol (single module)
```
Z  →  P1 <blockNo> <addr><display>   (block operation instruction, steady)
   or
   →  FP1 <blockNo> <addr><display>  (block operation instruction, flash)
```

### `pick` protocol (batch)
```
Z  →  P1  <blockNo> <addr1><disp1> <addr2><disp2> ...   (steady)
   or
   →  FP1 <blockNo> <addr1><disp1> <addr2><disp2> ...   (flash)
```

Overwrite uses `P5` / `FP5`.

### Confirmation interrupt
The gateway pushes an unsolicited `t` packet when an operator presses CONFIRM. P2L ACKs it
immediately with `O` via `SendImmediateAsync` — bypasses the send queue to guarantee
sub-millisecond response regardless of pending commands.

### Forbidden bytes
Commands must **not** contain `0x02` (STX) or `0x03` (ETX). Also avoid `0x11` (XON) and
`0x13` (XOFF) — some gateway firmware strips these silently as legacy serial flow-control bytes.
P2L uses `NoChange` (0xFF) instead of `Off` (0x11) for after-confirm LED states.

---

## 12. Quick Troubleshooting Reference

| Symptom | Likely cause | Fix |
|---|---|---|
| `Cannot connect to 192.168.1.254:5003` (repeated) | USB-LAN has no IP or route missing | Re-run `networksetup -setmanual` and host route steps in §5 |
| `nc: No route to host 192.168.1.254` | Static IP or host route lost after reboot | Re-apply; install launchd plist for persistence |
| Gateway reachable but internet broken | WiFi and USB-LAN on same subnet | Follow Case B in §5 |
| `Timeout waiting for ACK` on `Z` | Gateway powered off or busy | Power-cycle gateway; check LED status |
| `Gateway rejected command ... with 'n'` | Invalid command format or address | Enable Debug logging; check address range |
| `devices` scan returns 0 modules | Modules off, wrong address range | Widen `--from`/`--to`; verify wiring |
| `light set` shows no change on hardware | Module already terminated | Re-instruct with `light on` first |
| Light doesn't turn on, no error | Module not at that address | Run `./p2l devices` to discover actual addresses |
| `import` aborts with "Unknown Color value" | Typo in CSV Color column | Fix value; re-run with `--dry-run` |
| `appsettings.json not found` | Binary moved without config file | Keep `appsettings.json` next to `p2l` |
| `[MQTT] Connection failed` (repeated) | Broker unreachable or TLS mismatch | Check host/port; try `AllowUntrustedCerts: true` in dev |
| MQTT commands received but no hardware action | Gateway disconnected | Check `bridge_status.gatewayConnected` on `p2l/status` |
| `bridge_status { online: false }` on broker | LWT triggered — P2L crashed | Restart P2L; bridge auto-reconnects |
| `run` CSV runner stops mid-file | Process killed or error on a row | Restart `./p2l run` — confirmed rows were deleted, picks up from next row |
| CSV runner ignores MQTT commands | Commands arrive while row is in-flight | Expected — runner finishes current row confirm then yields; MQTT preempts between rows |
| No data on `p2l/events` | No subscriber active when event published | Subscribe before running; or use persistent session (`CleanStart: false`) |
| No data on `p2l/devices/+` | No pick commands sent yet | Send `get_snapshot` MQTT command to dump all known module states |
| `mqtt_offline_buffer.jsonl` grows indefinitely | Broker permanently unreachable | Fix broker connectivity; file is auto-deleted after successful replay |
| Buffer replay log shows partial sync on every connect | Broker rejects some message type | Check `[MQTT] Buffer replay rejected` warnings in log for the specific reason code |
| Want to discard buffered events and start fresh | Stale buffer from a different session | Delete `mqtt_offline_buffer.jsonl` manually before starting P2L |
| `./p2l --help` shows nothing, exits code 1 | AOT command type missing from `rd.xml` | Add the type to `rd.xml`; see §3 Native AOT note; delete `crash_history.jsonl` to reset counter |
| Process restarts unexpectedly | In-process self-heal triggered by unhandled exception | Check `crash_history.jsonl` for the exception; fix root cause |
| Process restarts every ~5 min | Watchdog fires — gateway stays disconnected | Fix network path to gateway; or set `WatchdogTimeoutMinutes: 0` to disable watchdog |
| Process no longer auto-restarts after repeated crashes | Crash-loop guard reached `MaxRestarts` | Fix root cause; delete `crash_history.jsonl` to reset the counter |
