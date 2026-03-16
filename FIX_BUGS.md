# Bug Fix Report

## BUG-001 — LED Color Channel Mapping Incorrect (C# + Python)

**Files:**
- `src/P2L.Shared/Enums/LightColor.cs`
- `client/app/protocol/enums.py`

**Severity:** Critical
**Status:** Fixed

---

### Description

The `LightColorExtensions.ToLedStates()` method (C#) and `color_to_led_states()` function (Python) had **Red and Blue swapped**, and consequently **Yellow and Cyan were also wrong**. Sending `LightColor.Red` to a module would physically show **blue**, and `LightColor.Blue` would show **red**.

---

### Root Cause

Both files assumed:
> `LED0 = Red, LED1 = Green, LED2 = Blue`

This is incorrect for all full-color Aioi modules. Per **L-PICK Controller Command Reference V4.1, §5.5.2** (*Relationship between Light Module Part Number and LED Colors*), the actual physical LED channel assignment for full-color modules (AW2xxxF, TW2xxxF, DW2xxxF, TW2928, NW294x, NW297x) is:

| Channel | Physical Color |
|---------|----------------|
| LED0    | **Blue**       |
| LED1    | **Green**      |
| LED2    | **Red**        |

This is confirmed by the official LED state → color table from the spec:

| LED2 | LED1 | LED0 | Resulting Color |
|------|------|------|-----------------|
| OFF  | OFF  | ON   | Blue            |
| OFF  | ON   | OFF  | Green           |
| ON   | OFF  | OFF  | Red             |
| OFF  | ON   | ON   | Light Blue (Cyan) |
| ON   | ON   | OFF  | Orange (Yellow) |
| ON   | OFF  | ON   | Purple (Magenta)|
| ON   | ON   | ON   | White           |

---

### Impact

All 5 affected colors produced the **wrong physical light color** on hardware:

| LightColor | Before (wrong)        | After (correct)       |
|------------|-----------------------|-----------------------|
| `Red`      | LED0=ON → **Blue**    | LED2=ON → **Red**     |
| `Blue`     | LED2=ON → **Red**     | LED0=ON → **Blue**    |
| `Yellow`   | LED0+LED1 → **Cyan**  | LED2+LED1 → **Yellow**|
| `Cyan`     | LED1+LED2 → **Yellow**| LED1+LED0 → **Cyan**  |
| `Magenta`  | LED2+LED0 → Magenta ✓ | LED2+LED0 → Magenta ✓ |
| `Green`    | LED1=ON → Green ✓     | LED1=ON → Green ✓     |
| `White`    | All ON → White ✓      | All ON → White ✓      |

---

### Fix Applied

**`src/P2L.Shared/Enums/LightColor.cs`** — corrected LED channel assignment in `ToLedStates()`:

```csharp
// Before (WRONG):
LightColor.Red     => (LightState.Off, LightState.Off, activeMode),  // LED0 → Blue
LightColor.Blue    => (activeMode,     LightState.Off, LightState.Off), // LED2 → Red
LightColor.Yellow  => (LightState.Off, activeMode,     activeMode),  // LED0+LED1 → Cyan
LightColor.Cyan    => (activeMode,     activeMode,     LightState.Off), // LED1+LED2 → Yellow

// After (CORRECT — per §5.5.2, LED2=Red, LED1=Green, LED0=Blue):
LightColor.Red     => (activeMode,     LightState.Off, LightState.Off),  // LED2=Red
LightColor.Blue    => (LightState.Off, LightState.Off, activeMode),      // LED0=Blue
LightColor.Yellow  => (activeMode,     activeMode,     LightState.Off),  // LED2+LED1 = Red+Green
LightColor.Cyan    => (LightState.Off, activeMode,     activeMode),      // LED1+LED0 = Green+Blue
```

**`client/app/protocol/enums.py`** — same fix applied to `color_to_led_states()` and enum comments:

```python
# Before (WRONG):
LightColor.RED:     (off, off, m),    # LED0 → Blue
LightColor.BLUE:    (m,   off, off),  # LED2 → Red
LightColor.YELLOW:  (off, m,   m),    # LED0+LED1 → Cyan
LightColor.CYAN:    (m,   m,   off),  # LED1+LED2 → Yellow

# After (CORRECT):
LightColor.RED:     (m,   off, off),  # LED2 (Red) on
LightColor.BLUE:    (off, off, m),    # LED0 (Blue) on
LightColor.YELLOW:  (m,   m,   off),  # LED2 (Red) + LED1 (Green)
LightColor.CYAN:    (off, m,   m),    # LED1 (Green) + LED0 (Blue)
```

---

### Reference

- **L-PICK Controller Command Reference V4.1**, Section 5.5.2
- Source document: `docs/L-Pick_ControllerComRefV41E.pdf`
- Verified against PTL Tester trace in `docs/color_light_02.jpg`

---

## BUG-002 — Race Condition: `registry.get()` Missing Lock

**File:** `client/app/core/registry.py`
**Severity:** Critical
**Status:** Fixed

### Description

`LightModuleRegistry.get()` read directly from `self._modules` without acquiring `self._lock`, while all write operations held the lock. This allowed stale or partially-written module states to be returned under concurrent load.

### Fix Applied

Wrapped the dict read in `with self._lock:`.

---

## BUG-003 — Race Condition: Private `_modules` Written Without Lock in `main.py`

**File:** `client/app/main.py`
**Severity:** Critical
**Status:** Fixed

### Description

`_on_mqtt_event()` wrote directly to `registry._modules[...]` (bypassing the lock) and then called `registry._notify()` separately, creating a window where other readers could see inconsistent state. It also accessed the private `registry._normalize()` method.

### Fix Applied

- Added `LightModuleRegistry.update_module_state(module)` — a public, lock-guarded upsert method.
- Added `LightModuleRegistry.normalize_address()` — a public alias for address normalization.
- Replaced all private access in `main.py` with the new public API.

---

## BUG-004 — File Handle Leak in `service.py`

**File:** `client/app/service.py`
**Severity:** High
**Status:** Fixed

### Description

The subprocess log file (`p2l_webclient.log`) was opened with `open()` but the handle was stored in a local variable and never closed. On long-running services or repeated start/stop cycles, this caused a file descriptor leak.

### Fix Applied

- Promoted `log_file` to a module-level `_log_file` variable.
- Added explicit `_log_file.close()` in `stop()` and in the early-exit path when the process terminates immediately.

---

## BUG-005 — Debug `print(binary)` Left in Production Code

**File:** `client/app/api/routes.py`
**Severity:** High
**Status:** Fixed

### Description

`get_status()` contained `print(binary)` which printed the P2L service binary path to stdout on every status poll (every 5 seconds from the browser). This leaked filesystem paths and polluted logs.

### Fix Applied

Removed the debug statement.

---

## BUG-006 — MQTT Connection Used Polling Instead of Async Event

**Files:** `client/app/mqtt/client.py`, `client/app/main.py`
**Severity:** Medium
**Status:** Fixed

### Description

`_do_connect()` polled `mc.is_connected` in a tight loop (150 × 0.1 s = 15 s max) to detect when the MQTT broker handshake completed. This wasted CPU cycles and added artificial latency.

### Fix Applied

- Added `asyncio.Event` (`_connected_event`) to `MqttClient`; set on connect, cleared on disconnect.
- Added `async def wait_connected(timeout)` public method using `asyncio.wait_for`.
- Replaced the polling loop in `main.py` with `await mc.wait_connected(timeout=15.0)`.

---

## BUG-007 — Unused Jinja2Templates Instance in `main.py`

**File:** `client/app/main.py`
**Severity:** Medium
**Status:** Fixed

### Description

`Jinja2Templates(directory=TEMPLATES_DIR)` was called but the result was discarded (not assigned to any variable). The import was unused. Routes already construct their own `Jinja2Templates` instance via `_get_templates()`.

### Fix Applied

Removed the dead call and the unused `from fastapi.templating import Jinja2Templates` import.

---

## BUG-008 — WebSocket Broadcast Used Deprecated `get_event_loop()`

**File:** `client/app/api/ws_manager.py`
**Severity:** Medium
**Status:** Fixed

### Description

`broadcast_sync()` called `asyncio.get_event_loop()`, deprecated in Python 3.10+ (emits `DeprecationWarning`; raises `RuntimeError` in Python 3.12+ if no current loop).

### Fix Applied

Replaced with `asyncio.get_running_loop()`.

---

## BUG-009 — WebSocket Broadcast Used Serial Loop Instead of Parallel Sends

**File:** `client/app/api/ws_manager.py`
**Severity:** Medium
**Status:** Fixed

### Description

`broadcast()` sent messages to each WebSocket client sequentially. A slow or stalled client would delay all subsequent clients from receiving real-time updates.

### Fix Applied

Replaced the serial loop with `asyncio.gather(..., return_exceptions=True)` to send to all clients concurrently. Dead connections are still detected and removed.

---

## BUG-010 — `batch_status` API Returned Inverted `complete` Flag

**File:** `client/app/api/routes.py`
**Severity:** Medium
**Status:** Fixed

### Description

The `complete` field was computed as:
```python
"complete": len(active) > 0 and len(pending) == 0
```
This evaluates to `True` when modules are actively operating and nothing is confirmed yet — the **opposite** of "complete". A finished batch has `active == 0`.

### Fix Applied

```python
"complete": len(active) == 0 and len(pending) == 0
```

---

## BUG-011 — `pushOperatingRow()` Defined But Never Called

**File:** `client/static/js/app.js`
**Severity:** Medium
**Status:** Fixed

### Description

`pushOperatingRow(d)` populates the *Operating Events* data table but was never called from the `state_update` WebSocket handler. The table remained permanently empty.

### Fix Applied

Added `pushOperatingRow(d)` call inside the `state_update` handler alongside the existing `upsertAddressRow(d)` call.

---

## BUG-012 — CSV Import Toast Showed Inflated Count (Included Header Row)

**File:** `client/static/js/app.js`
**Severity:** Low
**Status:** Fixed

### Description

After importing a CSV file the toast used `lines.length` (total lines including the header) as the item count. Importing a 4-row CSV with a header would show "Imported 5 item(s)" instead of "Imported 4 item(s)".

### Fix Applied

Replaced `lines.length` with an `importedCount` counter that only increments for successfully parsed data rows (those passing the numeric-address guard).

