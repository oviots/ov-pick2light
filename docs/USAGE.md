# Pick2Light Usage Guide

This guide explains how to configure, run, and operate Pick2Light in day-to-day use.

---

## 1. What You Run

P2L usually consists of two deployed services:

- the **P2L bridge**: talks to the Aioi gateway and hardware
- the **P2L client**: hosts the web dashboard and local operator UI

In simple setups, both run on the same machine.

---

## 2. Before You Start

Make sure you have:

- network access to the Aioi gateway
- broker details if MQTT integration is enabled
- deployment output from the packaging scripts or published binaries
- write access to the config and log directories

Typical gateway defaults in this project:

- host: `192.168.1.254`
- port: `5003`

---

## 3. Configuration Files

Common config files:

- bridge config: `appsettings.json`
- client config: `config/p2l_client_config.json`
- local bridge config copy/defaults: `config/p2l_config.json`

Installers in this repository are designed to place default config files automatically.

Key bridge settings include:

- gateway host/port
- MQTT enabled flag
- MQTT host, port, username, password, TLS
- topic names for commands, events, status, and devices
- log directory and self-heal settings

Client-side settings typically include:

- API/UI host and port
- MQTT connection details for the UI workflow
- paths to local config and device registry data

---

## 4. Running the Bridge

### Development

Run the .NET application from the repository root:

```bash
~/.dotnet/dotnet run --project src/P2L.CLI
```

You can also use the CLI subcommands described in `README.md`.

### Production

Run the packaged binary or install it as a service:

- Linux: `systemd`
- Windows: Windows Service
- macOS: local binary or service wrapper, depending on deployment choice

The bridge should start before or alongside the client.

---

## 5. Running the Client

### Development

From `client/`, run the FastAPI entry point using the project’s normal startup path.

If you use the provided packaged client, prefer that over manual Python setup.

### Production

Use the installed service/binary from the deployment package. After startup, open the web UI in the browser using the configured host and port.

---

## 6. First-Time Bring-Up

Recommended sequence:

1. verify the gateway is reachable on the network
2. start the P2L bridge
3. confirm the bridge is connected to MQTT, if enabled
4. start the P2L client
5. open the browser UI
6. check service status in the dashboard
7. scan devices or load known configuration

If MQTT is disabled, use direct bridge/CLI workflows only.

---

## 7. Using the Web UI

### Dashboard

Use the dashboard to monitor:

- bridge service state
- MQTT connection state
- gateway connectivity
- incoming device state changes
- batch/picking activity

### Settings -> P2L/Client Settings

Use this area to configure client-side behavior and connectivity.

### Settings -> P2L/Bridge Settings

Use this area to:

- edit bridge-related settings
- start or stop the P2L service where supported
- verify current bridge status

### Settings -> Scan Devices

This tab is shown when the client is connected.

Workflow:

1. enter **From address**
2. enter **To address**
3. click **Start Scan**
4. wait for the scan to complete
5. review discovered devices in the results panel

Behavior:

- the scan button is disabled while a scan is running
- results appear live on the right panel
- the button is re-enabled when the scan finishes

---

## 8. MQTT Usage

MQTT is the integration path for web UI actions and external systems.

### Default topics

| Role | Default topic | Retain |
|---|---|---|
| Inbound commands | `p2l/commands` | No |
| Business events | `p2l/events` | No |
| Bridge status / LWT | `p2l/status` | Yes |
| Per-device state | `p2l/devices/{address}` | Yes |

The bridge subscribes to the configured command topic and publishes events/status back to the broker.

Important current behavior:

- startup no longer clears retained messages from the broker
- retained topics are preserved as-is

This is useful when you want broker-side retained state to survive bridge restarts.

### Sample command payloads

#### `light_on`

Topic: `p2l/commands`

```json
{
  "type": "light_on",
  "correlationId": "demo-001",
  "address": "0001",
  "quantity": 5,
  "color": "Green",
  "ledMode": "On",
  "buzMode": "Off",
  "label": "A-01"
}
```

#### `light_off`

```json
{
  "type": "light_off",
  "correlationId": "demo-002",
  "address": "0001"
}
```

#### `batch_start`

```json
{
  "type": "batch_start",
  "correlationId": "batch-1001",
  "color": "Green",
  "ledMode": "On",
  "buzMode": "Off",
  "items": [
    {
      "address": "0001",
      "quantity": 3,
      "label": "SKU-A1"
    },
    {
      "label": "SKU-B9",
      "quantity": 2,
      "color": "Red",
      "ledMode": "Flash"
    }
  ]
}
```

Each item may include `address` directly, or use `label` and let the bridge resolve it from the client device database.

#### `batch_finish`

```json
{
  "type": "batch_finish",
  "correlationId": "batch-stop-1"
}
```

#### `terminate_all`

```json
{
  "type": "terminate_all",
  "correlationId": "panic-1"
}
```

#### `get_snapshot`

```json
{
  "type": "get_snapshot",
  "correlationId": "snapshot-1"
}
```

The bridge responds by publishing `light_changed` payloads on `p2l/devices/{address}` for tracked modules.

#### `csv_run`

```json
{
  "type": "csv_run",
  "correlationId": "csv-1",
  "file": "tasks/picklist.csv",
  "color": "Green"
}
```

#### `csv_control`

```json
{
  "type": "csv_control",
  "correlationId": "csv-ctl-1",
  "action": "pause"
}
```

Valid `action` values are `pause`, `resume`, and `cancel`.

#### `scan_devices`

```json
{
  "type": "scan_devices",
  "correlationId": "scan-1",
  "from": 1,
  "to": 99
}
```

### Sample event payloads

#### `confirmed`

Topic: `p2l/events`

```json
{
  "type": "confirmed",
  "timestamp": "2026-03-19T07:00:00Z",
  "address": "0001",
  "status": "Normal",
  "adjustedQuantity": 2,
  "adjustedDisplay": "00002",
  "correlationId": "batch-1001",
  "displayData": "00002",
  "label": "SKU-A1"
}
```

#### `batch_completed`

```json
{
  "type": "batch_completed",
  "timestamp": "2026-03-19T07:05:00Z"
}
```

#### `task_event`

```json
{
  "type": "task_started",
  "timestamp": "2026-03-19T07:10:00Z",
  "address": "0001",
  "quantity": 4,
  "color": "Green",
  "label": "SKU-A1"
}
```

#### `bridge_status`

Topic: `p2l/status`

```json
{
  "type": "bridge_status",
  "timestamp": "2026-03-19T07:15:00Z",
  "online": true,
  "gatewayConnected": true
}
```

#### `light_changed`

Topic: `p2l/devices/0001`

```json
{
  "type": "light_changed",
  "timestamp": "2026-03-19T07:16:00Z",
  "address": "0001",
  "moduleStatus": "Operating",
  "color": "Green",
  "ledMode": "On",
  "segMode": "On",
  "buzMode": "Off",
  "displayData": "00005",
  "blockNo": "00",
  "adjustedQuantity": 5,
  "lastInstructionAt": "2026-03-19T07:15:59Z",
  "lastConfirmedAt": null,
  "label": "A-01"
}
```

---

## 9. Common Operations

### Turn on a single module

Use either:

- CLI light commands
- MQTT `light_on`
- a web UI action that publishes the corresponding command

### Start a picking batch

You can run batches from:

- CLI commands
- CSV workflows
- MQTT commands from an upstream system

### Import or export labels/config

Use the existing CSV import/export workflows for:

- label mapping
- address mapping
- bulk editing of known devices

---

## 10. Browser Session Behavior

The client is designed to be resilient to browser refreshes.

Current behavior:

- reloading the page does not immediately disconnect the backend session
- MQTT/client connectivity is preserved across reloads
- if no browser session remains active, the server disconnects after 15 minutes of idle time

This prevents accidental disconnects during routine operator refreshes.

---

## 11. Logs and Diagnostics

Check logs when something is not working as expected:

- bridge logs for gateway and MQTT issues
- client logs for UI/API/service integration issues
- rotating log files in the configured log directory

Typical things to verify:

- gateway host and port
- MQTT credentials and TLS files
- topic names
- service running state
- whether the bridge sees the gateway as connected

---

## 12. Troubleshooting

### UI cannot connect

Check:

- client service is running
- configured host/port are correct
- browser can reach the FastAPI server

### MQTT connects but commands do nothing

Check:

- command topic matches between client and bridge
- bridge is subscribed successfully
- gateway is connected
- command payload shape matches expected command type

### No devices found during scan

Check:

- address range is correct
- gateway is connected
- light modules are present on the bus
- scan is being run against the expected controller segment

### Bridge starts but hardware does not respond

Check:

- TCP reachability to the Aioi gateway
- block/controller settings
- physical RS-485 or gateway-side wiring

---

## 13. Recommended Next References

- `README.md` for build and command reference
- `docs/ARCHITECTURE.md` for system design
- `BUILD.md` for packaging
- `WINDOWS_INSTALL.md` for Windows deployment
