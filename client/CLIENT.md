# P2L Web Dashboard — `client/`

A local browser-based control panel for **Pick2Light (P2L)** hardware.
Built with **Python / FastAPI**, real-time updates over **WebSocket**, and a
dark-themed vanilla-JS single-page app.

All commands are forwarded to the **P2L .NET bridge service** via **MQTT**.
The Python client never connects directly to Aioi hardware.

```
Browser → FastAPI REST → MQTT broker → P2L .NET bridge → Aioi hardware
Browser ← WebSocket   ← MQTT events ← P2L .NET bridge ← Aioi hardware
```

When **Connect** is clicked, the dashboard optionally launches the P2L .NET
service automatically if it is not already running (configurable via
`service.auto_start` and `service.binary_path`).

---

## Quick start

```bash
cd client
pip install -r requirements.txt
python run.py
# → http://127.0.0.1:8765
```

> Requires Python 3.11+ (uses `str | None` union syntax).

---

## Directory structure

```
client/
├── app/
│   ├── main.py              # FastAPI app, lifespan, global state, MQTT event wiring
│   ├── config.py            # AppConfig, MqttConfig, TlsConfig, ServiceConfig
│   ├── db.py                # SQLite persistence for device registry (p2l_devices.db)
│   ├── service.py           # P2L .NET service lifecycle (check running, start, stop)
│   ├── protocol/
│   │   ├── enums.py         # LightState, LightColor, ModuleStatus, CompletionStatus
│   │   └── models.py        # LightModuleState, PickingInstruction, ConfirmationEvent
│   ├── core/
│   │   └── registry.py      # LightModuleRegistry (in-memory device state)
│   ├── mqtt/
│   │   └── client.py        # MqttClient: pub/sub, reconnect, enum name mapping
│   └── api/
│       ├── routes.py        # REST endpoints + WebSocket
│       └── ws_manager.py    # WebSocket broadcast manager
├── static/
│   ├── css/style.css
│   └── js/app.js
├── templates/
│   └── index.html
├── p2l_devices.db           # SQLite device registry (auto-created on first run)
├── requirements.txt
├── run.py
└── CLIENT.md                # this file
```

---

## Configuration

On first run the app tries to read `../appsettings.json` (the main project
config) and copies the relevant fields.  Subsequent saves write
`p2l_config.json` next to `client/`.

### General

| Field | Default | Description |
|---|---|---|
| `block_no` | `"00"` | Default block sent with `batch_finish` / `terminate_all` |
| `auto_connect` | `false` | Connect to MQTT broker on startup |

### MQTT

| Field | Default | Description |
|---|---|---|
| `mqtt.host` | `"localhost"` | Broker hostname |
| `mqtt.port` | `8883` | Broker port |
| `mqtt.client_id` | `"p2l-webclient"` | MQTT client identifier |
| `mqtt.username` / `password` | — | Broker credentials |
| `mqtt.command_topic` | `"p2l/commands"` | Topic for outgoing commands |
| `mqtt.event_topic` | `"p2l/events"` | Topic for confirmations / batch events |
| `mqtt.devices_topic` | `"p2l/devices"` | Topic prefix for per-device state (`{topic}/{addr}`) |
| `mqtt.status_topic` | `"p2l/status"` | Bridge health / Last Will |

#### TLS

| Field | Default | Description |
|---|---|---|
| `mqtt.tls.enabled` | `true` | Use TLS for broker connection |
| `mqtt.tls.ca_cert_path` | `null` | Custom CA cert (PEM); `null` = OS trust store |
| `mqtt.tls.client_cert_path` / `client_key_path` | `null` | Mutual TLS |
| `mqtt.tls.allow_untrusted_certs` | `false` | Skip cert verification — **dev/test only** |

### Service

| Field | Default | Description |
|---|---|---|
| `service.binary_path` | `""` | Absolute path to the `p2l` binary |
| `service.args` | `[]` | CLI arguments passed to the binary (e.g. `["run", "tasks.csv"]`) |
| `service.working_dir` | `""` | Working directory; defaults to the binary's parent folder |
| `service.auto_start` | `true` | Launch the service automatically when clicking Connect |

All settings are editable live in the **Settings** tab and saved with **Save Settings**.

---

## Device persistence (SQLite)

Device addresses and labels are stored in `p2l_devices.db` (SQLite, created
automatically at startup) so they survive server restarts.

| Function | Behaviour |
|---|---|
| `init_db()` | Creates `devices(address PK, label)` table if absent |
| `load_devices()` | Returns all rows sorted by address — loaded into registry on startup |
| `upsert_device(address, label)` | Insert or overwrite; used by explicit API calls (`POST /api/devices`, `PATCH /api/devices/{address}`) |
| `insert_if_new(address)` | Insert with empty label only if address is unknown; returns the stored label.  Used when `light_changed` MQTT events arrive so auto-discovered devices persist without overwriting user-set labels |
| `update_label(address, label)` | Update label only (address must already exist) |
| `remove_device(address)` | Delete one row |
| `clear_devices()` | Delete all rows |

---

## P2L service lifecycle

`app/service.py` manages the P2L .NET process:

- **`is_running(binary_path)`** — uses `psutil` to find a running `p2l` process by exe path or
  name; falls back to tracking the subprocess spawned by this manager.
- **`start(binary_path, args, working_dir)`** — launches the binary with
  `asyncio.create_subprocess_exec`; stdout/stderr are appended to
  `p2l_webclient.log` in the working directory.  Raises `RuntimeError` if the
  binary is missing or exits within 1.5 s.
- **`stop()`** — sends SIGTERM to the managed subprocess (5 s timeout, then SIGKILL).

`POST /api/connect` calls `service.start()` automatically when
`service.auto_start = true` and the binary is not already running, then waits
2 s for the service to connect to the broker before opening the MQTT session.

---

## MQTT communication

```
Browser → FastAPI → MqttClient.publish_command() → Broker → P2L .NET bridge
                                                           ↓
Browser ← FastAPI WebSocket ← MqttClient (event sub) ← Broker ← P2L .NET bridge
```

- All write commands are published to `p2l/commands` (QoS 1).
- Incoming events (`p2l/events`, `p2l/devices/#`, `p2l/status`) update the
  in-memory registry and are forwarded to the browser over WebSocket.
- `MqttClient` auto-reconnects with exponential back-off (1 s → 30 s).

### Command envelope (`p2l/commands`, QoS 1)

All field names are **camelCase**; enum strings are **PascalCase** (matching
the P2L .NET `P2LMqttJsonContext` serialisation settings).

```jsonc
// Light a single module
{ "type": "light_on", "address": "0001", "quantity": 5, "color": "Green", "ledMode": "On", "label": "SKU-123" }

// Turn off a single module
{ "type": "light_off", "address": "0001" }

// Start a picking batch
{
  "type": "batch_start",
  "ledMode": "On",
  "items": [
    { "address": "0001", "quantity": 3, "color": "Green" },
    { "address": "0002", "quantity": 1, "color": "Yellow", "label": "FRAGILE" }
  ]
}

// Finish / terminate batch
{ "type": "batch_finish", "blockNo": "00" }

// Emergency terminate all
{ "type": "terminate_all" }
```

### Event envelope (`p2l/events`, `p2l/devices/#`, `p2l/status`)

```jsonc
// Operator pressed CONFIRM (p2l/events)
{ "type": "confirmed", "address": "0001", "status": "Normal", "adjustedDisplay": null }

// Module state changed (p2l/devices/0001, retained)
{ "type": "light_changed", "address": "0001", "moduleStatus": "Operating",
  "color": "Green", "ledMode": "On", "displayData": "    5", "blockNo": "00" }

// All items confirmed (p2l/events)
{ "type": "batch_completed" }

// Bridge / gateway health (p2l/status, retained)
{ "type": "bridge_status", "online": true, "gatewayConnected": true }
```

### Enum name mapping

| Python | MQTT / C# |
|---|---|
| `LightColor.GREEN` | `"Green"` |
| `LightState.ON` | `"On"` |
| `LightState.FLASH` | `"Flash"` |
| `LightState.HIGH_FLASH` | `"HighFlash"` |
| `ModuleStatus.OPERATING` | `"Operating"` |
| `CompletionStatus.NORMAL` | `"Normal"` |
| `CompletionStatus.SHORTAGE` | `"Shortage"` |

---

## REST API

Base URL: `http://127.0.0.1:8765`

All request/response bodies are JSON.

### Connection

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/status` | Status snapshot: `{connected, host, port, total_devices, active_devices, pending_confirm}` |
| `POST` | `/api/connect` | Start P2L service (if configured) then connect to MQTT broker |
| `POST` | `/api/disconnect` | Disconnect from MQTT broker |

`POST /api/connect` response: `{"status": "connecting", "service_started": bool}`

### Service

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/service/status` | `{"running": bool, "binary_path": str}` |
| `POST` | `/api/service/start` | Start the P2L service manually |
| `POST` | `/api/service/stop` | Stop the managed P2L service subprocess |

### Devices

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/devices` | List all registered modules |
| `POST` | `/api/devices` | Register a device — body: `{address, label?}` |
| `PATCH` | `/api/devices/{address}` | Update label — body: `{label}` |
| `DELETE` | `/api/devices` | Clear all devices from registry and DB |
| `DELETE` | `/api/devices/{address}` | Remove one device from registry and DB |
| `POST` | `/api/devices/{address}/light` | Turn on — body: `{quantity, color, led_mode, block_no, label?}` |
| `POST` | `/api/devices/{address}/off` | Turn off |

### Batch

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/batch/start` | Start batch — body: `{block_no, items: [{address, quantity, color, led_mode, label?}]}` |
| `POST` | `/api/batch/finish` | Finish batch (sends `batch_finish` to broker) |
| `GET` | `/api/batch/status` | `{active, pending: [addr…], complete}` |
| `POST` | `/api/terminate-all` | Publish `terminate_all` command |

### Configuration

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/config` | Current config (MQTT password omitted) |
| `PUT` | `/api/config` | Partial update — all fields optional |

`PUT /api/config` accepts: `block_no`, `auto_connect`, `mqtt_*`, `service_binary_path`,
`service_args`, `service_working_dir`, `service_auto_start`.

### Logs

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/logs` | Last 500 log entries |
| `DELETE` | `/api/logs` | Clear log buffer |

---

## WebSocket

Connect to `ws://127.0.0.1:8765/ws`.  Send `"ping"` to keep alive; server
replies `"pong"`.

### Server → client message types

| `type` | Payload | Trigger |
|---|---|---|
| `init` | `{connected, mqtt_connected, host, port, devices[]}` | On WS connect |
| `connection_changed` | `{connected, mqtt_connected, host, port}` | MQTT broker connect/disconnect |
| `state_update` | `[LightModuleState…]` | Any registry mutation |
| `confirmation` | `{address, status, adjusted_display, received_at}` | Operator presses CONFIRM |
| `batch_completed` | `{}` | All batch items confirmed |
| `bridge_status` | `{online, gateway_connected}` | P2L bridge health event |
| `log` | `{time, level, message}` | Internal log entry |

> `connected` = P2L .NET service is running (detected via `psutil`).
> `mqtt_connected` = Python client is connected to the MQTT broker.

---

## UI tabs

| Tab | Contents |
|---|---|
| **Dashboard** | Stats (total / active / pending / connected), Terminate All, confirmation event feed |
| **Devices** | Card grid per module — address, status badge, color bar, display value, Light ON modal, Off button |
| **Batch Pick** | Batch builder (manual entry or CSV import), active batch table with per-item confirmation ticks |
| **Data Processing** | Live grid of all known light addresses (qty, color, status, editable label) + Operating Events table + Event Stream feed |
| **Settings** | MQTT broker settings, P2L service settings, block no, auto-connect, Save button |
| **Logs** | Scrolling log stream, clear button |

### Light ON modal

Per-module color picker (8 swatches), quantity, LED mode (Steady / Flash /
High Flash), block number, and optional label.  Published as a `light_on`
command to the MQTT broker.

### CSV import (Batch Pick)

Format: one item per line, comma-separated:

```
address,quantity,color,label
0001,5,GREEN,SKU-A
0002,3,YELLOW,SKU-B
```

`color` defaults to `GREEN` if omitted or unrecognised.  `label` is optional.

---

## Dependencies

| Package | Version | Purpose |
|---|---|---|
| `fastapi` | ≥ 0.110 | Web framework, WebSocket |
| `uvicorn[standard]` | ≥ 0.29 | ASGI server |
| `websockets` | ≥ 12.0 | WS support for uvicorn |
| `pydantic` | ≥ 2.6 | Request/response validation |
| `jinja2` | ≥ 3.1.3 | HTML template rendering |
| `aiofiles` | ≥ 23.2 | Async file I/O |
| `python-multipart` | ≥ 0.0.9 | Form data parsing |
| `aiomqtt` | ≥ 2.0 | Async MQTT client |
| `psutil` | ≥ 5.9 | Process detection for service lifecycle manager |
| `sqlite3` | stdlib | Device registry persistence (`p2l_devices.db`) |
