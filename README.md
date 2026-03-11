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
9. [systemd Service](#9-systemd-service-linux-247-operation)
10. [Protocol Notes](#10-protocol-notes)
11. [Troubleshooting](#11-quick-troubleshooting-reference)

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

**Dependency order:** `Shared → Hardware → Core → CLI`

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

The Aioi gateway communicates over **TCP port 5003**. Verify the network path before launching P2L.

### Step 1 — Identify the USB-LAN interface
```bash
# macOS
ifconfig | grep -A 4 "en"

# Linux
ip link show          # or: nmcli device status
```
Look for a new interface that appeared after plugging in the adapter (e.g. `en2` / `eth1` / `usb0`).

### Step 2 — Assign a static IP on the gateway's subnet
The gateway default is `192.168.1.254`. Your host must be on `192.168.1.x/24`:
```bash
# macOS
sudo networksetup -setmanual "USB 10/100/1000 LAN" 192.168.1.100 255.255.255.0 192.168.1.1

# Linux — nmcli (persistent)
sudo nmcli con add type ethernet ifname eth1 \
  ipv4.method manual ipv4.addresses 192.168.1.100/24

# Linux — ip (temporary)
sudo ip addr add 192.168.1.100/24 dev eth1 && sudo ip link set eth1 up
```

### Step 3 — Ping the gateway
```bash
ping -c 4 192.168.1.254    # all 4 packets must return
```

### Step 4 — Verify TCP port 5003
```bash
nc -zv 192.168.1.254 5003
# Expected: Connection to 192.168.1.254 port 5003 [tcp] succeeded!
```
If refused: gateway may need power-cycling or physical panel initialization.

### Step 5 — Check for firewall rules
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

Protocol: `Z` → `Am1` (display mode) → `Am2` (after-confirm mode) → `L` (operation instruction).

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

Lights up multiple modules simultaneously using a PP1 block command. Each module must be
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

## 9. systemd Service (Linux 24/7 Operation)

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
sudo systemctl start  p2l
sudo systemctl status p2l
```

---

## 10. Protocol Notes

### Initialization sequence
Every command sends `Z` (full initialization) once before any operation instruction. This resets all modules to standby. Subsequent commands within the same process skip the second `Z` (idempotent via `EnsureInitializedAsync`).

### `light on` protocol (single module)
```
Z  →  Am1 <addr> [LED2] [LED1+LED0] [SEG+BUZ]   (display status during instruction)
   →  Am2 <addr> [LED2] [LED1+LED0] [SEG+BUZ]   (display status after CONFIRM)
   →  L   <addr> <display-5-chars>               (operation instruction)
```
`Am1`/`Am2` use **binary nibble-packed bytes** per Aioi spec §4.4:
- LED2 byte: `0x30 | state` — Off=`0x31`, On=`0x32`, Flash=`0x33`, HighFlash=`0x34`
- LED1+LED0 byte: `(LED1_nibble << 4) | LED0_nibble`
- SEG+BUZ byte: `(SEG_nibble << 4) | BUZ_nibble`

### `pick` protocol (batch)
```
Z  →  M [mode array]   (set global LED/SEG/BUZ mode for all subsequent instructions)
   →  PP1 <blockNo> <addr1><disp1> <addr2><disp2> ...   (block operation instruction)
```

### Nack handling
The controller responds `o` (ACK) or `n` (NACK) to every command. P2L throws an error on `n`:
```
Error: Gateway rejected command 'Am10003...' with 'n' — check command format/address.
```
If you see this, enable `Debug` logging to inspect the raw TX bytes and compare against the Aioi Command Reference.

### Forbidden bytes
Commands must **not** contain `0x02` (STX) or `0x03` (ETX) in data bytes — these are the packet frame delimiters. P2L uses Latin-1 encoding for command bytes to correctly transmit values above `0x7F`.

---

## 11. Quick Troubleshooting Reference

| Symptom | Likely cause | Fix |
|---|---|---|
| `Cannot connect to 192.168.1.254:5003` (repeated) | Wrong IP or adapter not on subnet | Re-check Steps 2–4 in §5 |
| `Timeout waiting for ACK` on `Z` | Gateway powered off or busy | Power-cycle gateway; check physical LED status |
| `Gateway rejected command ... with 'n'` | Invalid command format or address | Enable Debug logging; check address range; verify hardware model matches TW/AW series |
| `devices` scan returns 0 modules | Modules off, wrong address range, or wiring fault | Widen `--from`/`--to`; verify wiring |
| `light set` shows no change on hardware | Module already terminated (Standby) | Re-instruct with `light on` first, then use `light set` |
| Light doesn't turn on, no error shown | Module not at that address | Run `./p2l devices` to discover actual addresses |
| `import` aborts with "Unknown Color value" | Typo in CSV Color column | Fix value; re-run with `--dry-run` to verify |
| Build error `clang not found` | Native AOT requires C compiler | Install Xcode CLT (macOS) or `clang` (Linux) |
| `appsettings.json not found` warning | Binary moved without config file | Keep `appsettings.json` next to the `p2l` binary |
