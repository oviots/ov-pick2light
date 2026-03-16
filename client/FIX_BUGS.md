# Client Bug Fix Report

> **Scope:** Python FastAPI web dashboard + JavaScript frontend (`client/`)
> **Date:** 2026-03-15
> **Reference spec:** L-PICK Controller Command Reference V4.1, §5.5.2

---

## BUG-C001 — LED Color Channel Mapping Incorrect

**File:** `app/protocol/enums.py`
**Severity:** 🔴 Critical
**Status:** Fixed

### Description

`color_to_led_states()` had **Red and Blue swapped**, making Yellow and Cyan wrong too.
Requesting `LightColor.RED` physically showed **blue** on the module.

### Root Cause

The docstring and inline comments assumed `LED0 = Red, LED2 = Blue`. The Aioi spec §5.5.2 states the opposite for all full-color modules (AW2xxxF, TW2xxxF, DW2xxxF, TW2928, NW294x, NW297x):

| Channel | Physical Color |
|---------|----------------|
| LED0    | **Blue**       |
| LED1    | **Green**      |
| LED2    | **Red**        |

### Impact

| LightColor | Before (wrong)        | After (correct)       |
|------------|-----------------------|-----------------------|
| `RED`      | LED0=ON → **Blue**    | LED2=ON → **Red**     |
| `BLUE`     | LED2=ON → **Red**     | LED0=ON → **Blue**    |
| `YELLOW`   | LED0+LED1 → **Cyan**  | LED2+LED1 → **Yellow**|
| `CYAN`     | LED1+LED2 → **Yellow**| LED1+LED0 → **Cyan**  |
| `MAGENTA`  | LED2+LED0 → Magenta ✓ | LED2+LED0 → Magenta ✓ |
| `GREEN`    | LED1=ON → Green ✓     | LED1=ON → Green ✓     |
| `WHITE`    | All ON → White ✓      | All ON → White ✓      |

### Fix

```python
# Before (WRONG):
"""Returns (LED2/Blue, LED1/Green, LED0/Red) LightState tuple ..."""
LightColor.RED:     (off, off, m),    # LED0 → physical Blue
LightColor.BLUE:    (m,   off, off),  # LED2 → physical Red
LightColor.YELLOW:  (off, m,   m),    # LED0+LED1 → Cyan
LightColor.CYAN:    (m,   m,   off),  # LED1+LED2 → Yellow

# After (CORRECT — per §5.5.2):
"""Returns (LED2/Red, LED1/Green, LED0/Blue) LightState tuple ..."""
LightColor.RED:     (m,   off, off),  # LED2 (Red) on
LightColor.BLUE:    (off, off, m),    # LED0 (Blue) on
LightColor.YELLOW:  (m,   m,   off),  # LED2 (Red) + LED1 (Green)
LightColor.CYAN:    (off, m,   m),    # LED1 (Green) + LED0 (Blue)
```

Also corrected inline comments on `YELLOW`, `CYAN`, `MAGENTA` enum members.

> **Note:** The same bug existed in `src/P2L.Shared/Enums/LightColor.cs` and was fixed simultaneously. See root `FIX_BUGS.md` BUG-001.

---

## BUG-C002 — Race Condition: `registry.get()` Missing Lock

**File:** `app/core/registry.py` — line 109
**Severity:** 🔴 Critical
**Status:** Fixed

### Description

`LightModuleRegistry.get()` read from `self._modules` without acquiring `self._lock`. All write operations held the lock, making reads inconsistent under concurrent load (e.g., an MQTT event writing while a WebSocket broadcast reads).

### Fix

```python
# Before:
def get(self, address: str) -> Optional[LightModuleState]:
    return self._modules.get(self._normalize(address))

# After:
def get(self, address: str) -> Optional[LightModuleState]:
    with self._lock:
        return self._modules.get(self._normalize(address))
```

---

## BUG-C003 — Race Condition: Direct Private `_modules` Write Without Lock

**File:** `app/main.py` — lines 107, 131–132
**Severity:** 🔴 Critical
**Status:** Fixed

### Description

`_on_mqtt_event()` wrote state via `registry._modules[addr] = module` (bypassing the mutex) then called `registry._notify()` separately. There was a window between these two calls where readers could observe a partially-updated state. Additionally, `registry._normalize()` was accessed as a private method.

### Fix

Added two public methods to `LightModuleRegistry`:

```python
# New public methods in registry.py:
@staticmethod
def normalize_address(address: str) -> str:
    return address.zfill(4)[-4:]

def update_module_state(self, module: LightModuleState) -> None:
    addr = self._normalize(module.address)
    with self._lock:
        self._modules[addr] = module
    self._notify()
```

Replaced in `main.py`:
```python
# Before:
stored = insert_if_new(registry._normalize(addr))
...
registry._modules[registry._normalize(addr)] = module
registry._notify()

# After:
stored = insert_if_new(LightModuleRegistry.normalize_address(addr))
...
registry.update_module_state(module)
```

---

## BUG-C004 — File Handle Leak in Service Manager

**File:** `app/service.py` — line 66
**Severity:** 🟠 High
**Status:** Fixed

### Description

The subprocess log file (`p2l_webclient.log`) was opened via `open()` into a local variable and never closed. On repeated `start()` / `stop()` cycles or when the process exits immediately, the file descriptor was leaked.

### Fix

Promoted to module-level `_log_file`:

```python
_log_file: Optional[object] = None

async def start(...):
    global _proc, _log_file
    ...
    _log_file = open(log_path, "a")
    _proc = await asyncio.create_subprocess_exec(..., stdout=_log_file, stderr=_log_file)

async def stop():
    global _proc, _log_file
    ...
    _proc = None
    if _log_file:
        _log_file.close()
        _log_file = None
```

---

## BUG-C005 — Debug `print(binary)` Left in Production Endpoint

**File:** `app/api/routes.py` — line 179
**Severity:** 🟠 High
**Status:** Fixed

### Description

`GET /api/status` (polled every 5 seconds by the browser) contained a bare `print(binary)` that printed the P2L service binary path to stdout on every request. This leaked filesystem paths and polluted server output.

### Fix

Removed the debug statement.

---

## BUG-C006 — MQTT Connect Check Used Polling Loop

**Files:** `app/mqtt/client.py`, `app/main.py`
**Severity:** 🟡 Medium
**Status:** Fixed

### Description

`_do_connect()` waited for the MQTT handshake by spinning 150 times with `asyncio.sleep(0.1)` (up to 15 s). This wasted CPU cycles and introduced latency proportional to the polling interval even when the broker responded instantly.

### Fix

Added `asyncio.Event` to `MqttClient`:

```python
# In MqttClient.__init__:
self._connected_event: asyncio.Event = asyncio.Event()

# Set on connect, cleared on disconnect in _connect_loop.

# New public method:
async def wait_connected(self, timeout: float = 15.0) -> None:
    try:
        await asyncio.wait_for(self._connected_event.wait(), timeout=timeout)
    except asyncio.TimeoutError:
        raise RuntimeError(f"Timed out waiting for MQTT broker ...")
```

```python
# main.py — before:
for _ in range(150):
    if mc.is_connected:
        return
    await asyncio.sleep(0.1)
raise RuntimeError("Timed out ...")

# main.py — after:
await mc.wait_connected(timeout=15.0)
```

---

## BUG-C007 — Unused `Jinja2Templates` Instance in `main.py`

**File:** `app/main.py` — line 209
**Severity:** 🟡 Medium
**Status:** Fixed

### Description

`Jinja2Templates(directory=TEMPLATES_DIR)` was called at module level but the return value was discarded. Routes already construct their own `Jinja2Templates` instance in `_get_templates()`. The unused import was also left in place.

### Fix

Removed the dead call and the `from fastapi.templating import Jinja2Templates` import from `main.py`.

---

## BUG-C008 — WebSocket Broadcast Used Deprecated `asyncio.get_event_loop()`

**File:** `app/api/ws_manager.py` — line 44
**Severity:** 🟡 Medium
**Status:** Fixed

### Description

`broadcast_sync()` called `asyncio.get_event_loop()`, deprecated since Python 3.10 and raises `DeprecationWarning`. In Python 3.12+ it raises `RuntimeError` if there is no current event loop.

### Fix

```python
# Before:
loop = asyncio.get_event_loop()

# After:
loop = asyncio.get_running_loop()
```

---

## BUG-C009 — WebSocket Broadcasts Sent Serially (Slow Client Blocks Others)

**File:** `app/api/ws_manager.py` — `broadcast()` method
**Severity:** 🟡 Medium
**Status:** Fixed

### Description

The broadcast loop awaited each `ws.send_text()` one at a time. A stalled or slow WebSocket client would hold up delivery to all subsequent clients, delaying real-time updates across the dashboard.

### Fix

```python
# Before — serial:
for ws in list(self._connections):
    try:
        await ws.send_text(text)
    except Exception:
        dead.append(ws)

# After — parallel:
results = await asyncio.gather(
    *[ws.send_text(text) for ws in list(self._connections)],
    return_exceptions=True,
)
dead = [ws for ws, result in zip(list(self._connections), results)
        if isinstance(result, Exception)]
```

---

## BUG-C010 — `batch_status` API Returned Inverted `complete` Flag

**File:** `app/api/routes.py` — line 402
**Severity:** 🟡 Medium
**Status:** Fixed

### Description

The `complete` field was:
```python
"complete": len(active) > 0 and len(pending) == 0
```
This evaluates to `True` when modules are **actively operating** and nothing is confirmed yet — the exact opposite of "batch complete". A finished batch has zero active modules.

### Fix

```python
# Before (WRONG — true when batch just started):
"complete": len(active) > 0 and len(pending) == 0

# After (CORRECT — true when all modules done):
"complete": len(active) == 0 and len(pending) == 0
```

---

## BUG-C011 — `pushOperatingRow()` Defined But Never Called

**File:** `client/static/js/app.js`
**Severity:** 🟡 Medium
**Status:** Fixed

### Description

`pushOperatingRow(d)` was defined to populate the *Operating Events* data table, but was not called from the `state_update` WebSocket handler. As a result the table was always empty regardless of device activity.

### Fix

```javascript
// Before:
case 'state_update':
  msg.data.forEach(d => {
    upsertAddressRow(d);
    pushEvent('light_changed', d);
    ...

// After:
case 'state_update':
  msg.data.forEach(d => {
    upsertAddressRow(d);
    pushOperatingRow(d);   // ← added
    pushEvent('light_changed', d);
    ...
```

---

## BUG-C012 — CSV Import Toast Count Included Header Row

**File:** `client/static/js/app.js` — line 980
**Severity:** 🟢 Low
**Status:** Fixed

### Description

After importing a CSV the success toast used `lines.length` (all lines including the header) as the imported item count. Importing a 4-data-row CSV would show "Imported 5 item(s)".

### Fix

Replaced `lines.length` with an `importedCount` counter that only increments for rows that pass the numeric-address guard (i.e. actual data rows, not the header).

```javascript
// Before:
toast(`Imported ${lines.length} item(s)`, 'success');

// After:
let importedCount = 0;
lines.forEach(line => {
    ...
    if (!addr || !/^\d+$/.test(addr)) return;
    state.batchItems.push({ ... });
    importedCount++;
});
toast(`Imported ${importedCount} item(s)`, 'success');
```

---

## BUG-C013 — Export CSV: Address Stripped of Leading Zeros

**File:** `client/static/js/app.js` — `exportDevicesCSV()`
**Severity:** 🟠 High
**Status:** Fixed

### Description

The `address` field was written to CSV as a bare unquoted value (e.g. `0042`). Spreadsheet applications (Excel, Google Sheets, LibreOffice Calc) auto-detect unquoted numeric strings and coerce them to numbers, stripping leading zeros. Address `"0042"` would appear as `42` after import, breaking any re-import workflow.

The `label` field was also vulnerable: if a label contained a comma, it would corrupt the column alignment for all following fields.

### Fix

Added a `csvStr()` helper that wraps any value in double quotes and escapes internal double quotes (`"` → `""`). Applied to both `address` and `label` columns:

```javascript
function csvStr(v) {
  const s = String(v ?? '').replace(/"/g, '""');
  return `"${s}"`;
}

// address: "0042"  → preserved as text in all spreadsheet apps
// label:   "SKU, A" → correctly quoted, commas safe
```

### Output example

```
order_no,address,label,status,color,qty
1,"0001","Widget A",OPERATING,GREEN,5
2,"0042","Part, B",STANDBY,OFF,0
```

---

## Summary

| Bug      | Severity    | File                        | Issue                                         |
|----------|-------------|-----------------------------|-----------------------------------------------|
| BUG-C001 | 🔴 Critical | `protocol/enums.py`         | LED color channel mapping (Red↔Blue, Yellow↔Cyan swapped) |
| BUG-C002 | 🔴 Critical | `core/registry.py`          | `get()` reads `_modules` without lock         |
| BUG-C003 | 🔴 Critical | `main.py`                   | Private `_modules[]` write bypasses mutex     |
| BUG-C004 | 🟠 High     | `service.py`                | Log file handle never closed (FD leak)        |
| BUG-C005 | 🟠 High     | `api/routes.py`             | `print(binary)` debug statement in production |
| BUG-C006 | 🟡 Medium   | `mqtt/client.py` + `main.py`| MQTT connect used polling; replaced with `asyncio.Event` |
| BUG-C007 | 🟡 Medium   | `main.py`                   | `Jinja2Templates()` result discarded (dead code) |
| BUG-C008 | 🟡 Medium   | `api/ws_manager.py`         | `asyncio.get_event_loop()` deprecated in 3.10+ |
| BUG-C009 | 🟡 Medium   | `api/ws_manager.py`         | Serial WebSocket sends; now `asyncio.gather()` |
| BUG-C010 | 🟡 Medium   | `api/routes.py`             | `batch_status.complete` flag logic inverted   |
| BUG-C011 | 🟡 Medium   | `static/js/app.js`          | `pushOperatingRow()` never called; table always empty |
| BUG-C012 | 🟢 Low      | `static/js/app.js`          | CSV import count included header row          |
| BUG-C013 | 🟠 High     | `static/js/app.js`          | Export CSV: address not quoted — leading zeros stripped by spreadsheet apps |
