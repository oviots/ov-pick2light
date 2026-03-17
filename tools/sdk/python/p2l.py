"""
P2L SDK — Python client for Pick2Light bridge.

Communicates with the P2L .NET bridge via MQTT.
The bridge must be running and connected to the Aioi gateway.

Install:
    pip install paho-mqtt

Quick start:
    from p2l import P2LClient, LightColor, LightMode

    async def main():
        async with P2LClient("localhost") as p2l:
            await p2l.light_on("0001", quantity=5, color=LightColor.GREEN)
            event = await p2l.wait_confirm("0001", timeout=30)
            print(event)
"""

from __future__ import annotations

import asyncio
import inspect
import json
import logging
import ssl
from collections import deque
from dataclasses import dataclass, field
from enum import Enum
from typing import Callable, Optional

import paho.mqtt.client as mqtt

logger = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# Enums
# ---------------------------------------------------------------------------

class LightColor(str, Enum):
    OFF     = "Off"
    RED     = "Red"
    GREEN   = "Green"
    BLUE    = "Blue"
    YELLOW  = "Yellow"
    CYAN    = "Cyan"
    MAGENTA = "Magenta"
    WHITE   = "White"


class LightMode(str, Enum):
    OFF        = "Off"
    ON         = "On"
    FLASH      = "Flash"
    HIGH_FLASH = "HighFlash"
    NO_CHANGE  = "NoChange"


class CompletionStatus(str, Enum):
    NORMAL   = "Normal"
    ABNORMAL = "Abnormal"
    SHORTAGE = "Shortage"


# ---------------------------------------------------------------------------
# Event dataclasses
# ---------------------------------------------------------------------------

@dataclass
class ConfirmEvent:
    address:          str
    status:           CompletionStatus
    adjusted_display: Optional[str] = None

    @staticmethod
    def from_dict(d: dict) -> "ConfirmEvent":
        return ConfirmEvent(
            address=d["address"],
            status=CompletionStatus(d.get("status", "Normal")),
            adjusted_display=d.get("adjustedDisplay"),
        )


@dataclass
class ModuleState:
    address:       str
    module_status: str
    color:         Optional[str] = None
    led_mode:      Optional[str] = None
    display_data:  Optional[str] = None
    block_no:      Optional[str] = None
    label:         Optional[str] = None

    @staticmethod
    def from_dict(d: dict) -> "ModuleState":
        return ModuleState(
            address=d["address"],
            module_status=d.get("moduleStatus", ""),
            color=d.get("color"),
            led_mode=d.get("ledMode"),
            display_data=d.get("displayData"),
            block_no=d.get("blockNo"),
            label=d.get("label"),
        )


@dataclass
class BridgeStatus:
    online:            bool
    gateway_connected: bool


@dataclass
class TaskEvent:
    """
    Fired for each CSV pick-task lifecycle step.

    ``event_type`` is one of:
      - ``"task_started"``    – a row was instructed (light turned on)
      - ``"task_confirmed"``  – operator pressed CONFIRM on that module
      - ``"task_error"``      – a row could not be processed
      - ``"csv_completed"``   – every row in the CSV has been confirmed
    """
    event_type: str
    address:    str
    quantity:   int
    color:      str
    label:      Optional[str] = None
    timestamp:  Optional[str] = None

    @staticmethod
    def from_dict(d: dict) -> "TaskEvent":
        return TaskEvent(
            event_type=d["type"],
            address=d.get("address", ""),
            quantity=d.get("quantity", 0),
            color=d.get("color", ""),
            label=d.get("label"),
            timestamp=d.get("timestamp"),
        )


# ---------------------------------------------------------------------------
# Batch item helper
# ---------------------------------------------------------------------------

@dataclass
class BatchItem:
    """
    One item in a ``batch_start`` command.

    ``address`` is optional: if omitted (``None``), the P2L bridge resolves the
    hardware address from ``label`` using the client device database.
    Items where neither field resolves to a known address are skipped by the bridge.
    """
    address:  Optional[str]           # None → bridge resolves from label
    quantity: int
    color:    LightColor         = LightColor.GREEN
    label:    Optional[str]      = None

    def to_dict(self) -> dict:
        d: dict = {"quantity": self.quantity, "color": self.color.value}
        if self.address:
            d["address"] = self.address
        if self.label:
            d["label"] = self.label
        return d


# ---------------------------------------------------------------------------
# Wave dispatcher
# ---------------------------------------------------------------------------

@dataclass
class DispatchItem:
    """
    One item in a wave dispatch queue.

    ``sku``      is shown on the light module segment display as the label.
    ``quantity`` is the number displayed on the LED segment.
    ``color``    controls the LED ring color (default GREEN).
    """
    sku:      str
    quantity: int
    color:    LightColor         = LightColor.GREEN
    led_mode: LightMode          = LightMode.ON
    buz_mode: Optional[LightMode] = None


@dataclass
class DispatchStats:
    total:     int
    confirmed: int
    positions: int
    cancelled: bool = False


def _call(fn: Callable, *args) -> None:
    """Invoke ``fn(*args)``, scheduling coroutines on the running loop."""
    if inspect.iscoroutinefunction(fn):
        asyncio.ensure_future(fn(*args))
    else:
        fn(*args)


class WaveDispatcher:
    """
    Distributes a queue of picking items across multiple light positions in parallel.

    Think of ``positions`` as parallel workers: each is filled with the next item
    from the queue as soon as it becomes free (i.e. the operator presses CONFIRM).
    The wave finishes when every item has been dispatched **and** confirmed.

    Example::

        items = [
            DispatchItem("SKU-001", quantity=5),
            DispatchItem("SKU-002", quantity=3, color=LightColor.BLUE),
            DispatchItem("SKU-003", quantity=10),
        ]
        dispatcher = WaveDispatcher(p2l, items, positions=["0001", "0002", "0003"])

        dispatcher.on_item_sent      = lambda addr, item: print(f"→ {addr} {item.sku}")
        dispatcher.on_item_confirmed = lambda addr, item, evt: print(f"✓ {addr} {item.sku}")
        dispatcher.on_wave_complete  = lambda stats: print(f"Done {stats.confirmed}/{stats.total}")

        await dispatcher.start()
        await dispatcher.wait()
    """

    def __init__(
        self,
        client: "P2LClient",
        items: list[DispatchItem],
        *,
        positions: list[str],
    ):
        self._client    = client
        self._queue:  deque[DispatchItem] = deque(items)
        self._positions = list(positions)
        # address -> currently dispatched item (None = idle)
        self._active: dict[str, Optional[DispatchItem]] = {a: None for a in self._positions}
        self._total     = len(items)
        self._confirmed = 0
        self._cancelled = False
        self._prev_handler: Optional[Callable] = None
        self._done = asyncio.Event()

        # Public callbacks
        self.on_item_sent:      Optional[Callable] = None  # (address, item)
        self.on_item_confirmed: Optional[Callable] = None  # (address, item, ConfirmEvent)
        self.on_wave_complete:  Optional[Callable] = None  # (DispatchStats)

    # ------------------------------------------------------------------
    # Lifecycle
    # ------------------------------------------------------------------

    async def start(self) -> None:
        """Begin the wave: fill all positions with items from the queue."""
        if self._total == 0:
            self._finish()
            return
        # Hook into the client's confirm path
        self._prev_handler = self._client._on_confirm
        self._client._on_confirm = self._handle_confirm
        # Initial fill — all positions in parallel
        for addr in self._positions:
            self._try_dispatch(addr)

    async def wait(self, timeout: Optional[float] = None) -> DispatchStats:
        """Block until all items are confirmed (or *timeout* seconds elapse)."""
        if timeout is not None:
            try:
                await asyncio.wait_for(self._done.wait(), timeout)
            except asyncio.TimeoutError:
                raise TimeoutError(
                    f"Wave did not complete within {timeout}s "
                    f"({self._confirmed}/{self._total} confirmed)"
                )
        else:
            await self._done.wait()
        return DispatchStats(
            total=self._total,
            confirmed=self._confirmed,
            positions=len(self._positions),
            cancelled=self._cancelled,
        )

    def cancel(self) -> None:
        """Cancel the wave: turn off all active lights and stop dispatching."""
        if self._done.is_set():
            return
        self._cancelled = True
        self._queue.clear()
        for addr, item in self._active.items():
            if item is not None:
                self._client.light_off(addr)
                self._active[addr] = None
        self._client._on_confirm = self._prev_handler
        self._done.set()

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------

    def _try_dispatch(self, address: str) -> None:
        """Pop the next item and send it to *address*, or mark it idle."""
        if self._cancelled:
            return
        if not self._queue:
            self._active[address] = None
            # All positions idle with empty queue → wave complete
            if all(v is None for v in self._active.values()):
                self._finish()
            return
        item = self._queue.popleft()
        self._active[address] = item
        self._client.light_on(
            address,
            quantity=item.quantity,
            color=item.color,
            led_mode=item.led_mode,
            buz_mode=item.buz_mode,
            label=item.sku,
        )
        if self.on_item_sent:
            _call(self.on_item_sent, address, item)

    def _handle_confirm(self, evt: "ConfirmEvent") -> None:
        if evt.address not in self._active:
            # Not a managed position — pass through to previous handler
            if self._prev_handler:
                self._prev_handler(evt)
            return
        item = self._active.get(evt.address)
        if item is None:
            return
        self._confirmed += 1
        if self.on_item_confirmed:
            _call(self.on_item_confirmed, evt.address, item, evt)
        self._try_dispatch(evt.address)

    def _finish(self) -> None:
        self._client._on_confirm = self._prev_handler
        self._done.set()
        if self.on_wave_complete:
            stats = DispatchStats(
                total=self._total,
                confirmed=self._confirmed,
                positions=len(self._positions),
            )
            _call(self.on_wave_complete, stats)


# ---------------------------------------------------------------------------
# Batch dispatcher  (items pre-assigned to positions by label)
# ---------------------------------------------------------------------------

@dataclass
class BatchDispatchItem:
    """
    One item in a batch, pre-assigned to a specific position identified by *label*.

    ``label``    – position label as stored in the device database, e.g. ``"CH.001"``
    ``sku``      – product code; shown on the light module as a label
    ``quantity`` – number displayed on the segment display
    """
    sku:      str
    label:    str
    quantity: int
    color:    LightColor          = LightColor.GREEN
    led_mode: LightMode           = LightMode.ON
    buz_mode: Optional[LightMode] = None


@dataclass
class BatchDispatch:
    """
    A batch of items to dispatch, each pre-assigned to a position label.

    Parsed directly from the JSON input format::

        {
            "batch_id": "abc123",
            "items": [
                {"sku": "SKU-001", "label": "CH.001", "quantity": 10},
                {"sku": "SKU-002", "label": "CH.002", "quantity": 5},
                {"sku": "SKU-003", "label": "CH.001", "quantity": 15},
                {"sku": "SKU-004", "label": "CH.002", "quantity": 1}
            ]
        }

    Items sharing the same ``label`` are processed **sequentially** at that
    position; items at different positions run in **parallel**.
    """
    batch_id: str
    items:    list[BatchDispatchItem]

    @staticmethod
    def from_dict(d: dict) -> "BatchDispatch":
        return BatchDispatch(
            batch_id=d["batch_id"],
            items=[
                BatchDispatchItem(
                    sku=i["sku"],
                    label=i["label"],
                    quantity=i["quantity"],
                    color=LightColor(i["color"]) if "color" in i else LightColor.GREEN,
                    led_mode=LightMode(i["ledMode"]) if "ledMode" in i else LightMode.ON,
                )
                for i in d["items"]
            ],
        )


async def discover_label_map(
    client: "P2LClient",
    timeout: float = 2.0,
) -> dict[str, str]:
    """
    Build a ``{label: address}`` map by collecting retained ``light_changed``
    messages from ``p2l/devices/#``.

    The broker pushes retained messages immediately after subscription, so the
    default *timeout* of 2 seconds is sufficient even for large installations.
    Devices that have no label set are excluded from the result.

    Example::

        label_map = await discover_label_map(p2l)
        # {"CH.001": "0001", "CH.002": "0002", ...}
    """
    result: dict[str, str] = {}
    prev = client._on_state

    def _collect(state: ModuleState) -> None:
        if state.label:
            result[state.label] = state.address
        if prev:
            prev(state)

    client._on_state = _collect
    try:
        await asyncio.sleep(timeout)
    finally:
        client._on_state = prev

    return result


class BatchDispatcher:
    """
    Dispatches a :class:`BatchDispatch` where every item targets a specific
    position identified by its *label* (e.g. ``"CH.001"``).

    Items sharing the same label are queued and processed **one at a time** at
    that position.  All positions work in **parallel**.

    Requires a ``label_map`` dict ``{label: address}`` to resolve position
    labels to hardware addresses.  Use :func:`discover_label_map` to build
    this automatically from the MQTT broker's retained device states.

    Example::

        import json

        payload   = json.loads(raw_json)
        batch     = BatchDispatch.from_dict(payload)
        label_map = await discover_label_map(p2l)
        dispatcher = BatchDispatcher(p2l, batch, label_map=label_map)

        dispatcher.on_item_sent      = lambda addr, item: \\
            print(f"→ [{item.label}] {item.sku} x{item.quantity}")
        dispatcher.on_item_confirmed = lambda addr, item, evt: \\
            print(f"✓ [{item.label}] {item.sku}")
        dispatcher.on_batch_complete = lambda stats: \\
            print(f"Batch {stats['batch_id']} done ({stats['confirmed']}/{stats['total']})")

        await dispatcher.start()
        await dispatcher.wait()
    """

    def __init__(
        self,
        client: "P2LClient",
        batch: BatchDispatch,
        *,
        label_map: dict[str, str],
        led_mode: LightMode = LightMode.ON,
    ):
        self._client   = client
        self._batch_id = batch.batch_id
        self._led_mode = led_mode

        # Group items by address (preserving insertion order per position)
        addr_queues: dict[str, deque[BatchDispatchItem]] = {}
        unknown: list[str] = []
        for item in batch.items:
            addr = label_map.get(item.label)
            if addr is None:
                if item.label not in unknown:
                    unknown.append(item.label)
                continue
            if addr not in addr_queues:
                addr_queues[addr] = deque()
            addr_queues[addr].append(item)

        if unknown:
            raise ValueError(
                f"BatchDispatcher: label(s) not found in label_map: {unknown}. "
                f"Available: {list(label_map.keys())}"
            )

        self._queues: dict[str, deque[BatchDispatchItem]] = addr_queues
        self._active: dict[str, Optional[BatchDispatchItem]] = {a: None for a in addr_queues}
        self._total     = len(batch.items)
        self._confirmed = 0
        self._cancelled = False
        self._done      = asyncio.Event()
        self._prev_handler: Optional[Callable] = None

        # Public callbacks
        self.on_item_sent:      Optional[Callable] = None  # (address, item)
        self.on_item_confirmed: Optional[Callable] = None  # (address, item, ConfirmEvent)
        self.on_batch_complete: Optional[Callable] = None  # (stats dict)

    # ------------------------------------------------------------------
    # Lifecycle
    # ------------------------------------------------------------------

    async def start(self) -> None:
        """
        Begin dispatching the batch.

        The **first** item for every position is sent in a single
        :meth:`~P2LClient.batch_start` call so all lights turn on
        simultaneously.  Subsequent items (after each confirm) are sent
        individually via :meth:`~P2LClient.light_on`.
        """
        if self._total == 0 or not self._queues:
            self._finish()
            return
        self._prev_handler = self._client._on_confirm
        self._client._on_confirm = self._handle_confirm

        # Pop the first item from each position queue and collect into one batch
        initial: list[BatchItem] = []
        for addr, queue in self._queues.items():
            if not queue:
                continue
            item = queue.popleft()
            self._active[addr] = item
            initial.append(BatchItem(
                address=addr,
                quantity=item.quantity,
                color=item.color,
                label=item.sku,
            ))
            if self.on_item_sent:
                _call(self.on_item_sent, addr, item)

        if initial:
            self._client.batch_start(initial, led_mode=self._led_mode)

    async def wait(self, timeout: Optional[float] = None) -> dict:
        """Block until every item is confirmed (or *timeout* seconds elapse)."""
        if timeout is not None:
            try:
                await asyncio.wait_for(self._done.wait(), timeout)
            except asyncio.TimeoutError:
                raise TimeoutError(
                    f"Batch '{self._batch_id}' did not complete within {timeout}s "
                    f"({self._confirmed}/{self._total} confirmed)"
                )
        else:
            await self._done.wait()
        return {
            "batch_id":  self._batch_id,
            "total":     self._total,
            "confirmed": self._confirmed,
            "cancelled": self._cancelled,
        }

    def cancel(self) -> None:
        """Turn off all active lights and stop dispatching."""
        if self._done.is_set():
            return
        self._cancelled = True
        for addr, item in self._active.items():
            if item is not None:
                self._client.light_off(addr)
                self._active[addr] = None
            self._queues.get(addr, deque()).clear()
        self._client._on_confirm = self._prev_handler
        self._done.set()

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------

    def _dispatch_next(self, address: str) -> None:
        if self._cancelled:
            return
        queue = self._queues.get(address)
        if not queue:
            self._active[address] = None
            # Done when every position's queue is empty and no light is active
            if all(
                not self._queues[a] and self._active[a] is None
                for a in self._queues
            ):
                self._finish()
            return
        item = queue.popleft()
        self._active[address] = item
        self._client.light_on(
            address,
            quantity=item.quantity,
            color=item.color,
            led_mode=item.led_mode,
            buz_mode=item.buz_mode,
            label=item.sku,
        )
        if self.on_item_sent:
            _call(self.on_item_sent, address, item)

    def _handle_confirm(self, evt: "ConfirmEvent") -> None:
        if evt.address not in self._active:
            if self._prev_handler:
                self._prev_handler(evt)
            return
        item = self._active.get(evt.address)
        if item is None:
            return
        self._confirmed += 1
        if self.on_item_confirmed:
            _call(self.on_item_confirmed, evt.address, item, evt)
        self._dispatch_next(evt.address)

    def _finish(self) -> None:
        self._client._on_confirm = self._prev_handler
        self._done.set()
        if self.on_batch_complete:
            _call(self.on_batch_complete, {
                "batch_id":  self._batch_id,
                "total":     self._total,
                "confirmed": self._confirmed,
            })


# ---------------------------------------------------------------------------
# TLS config
# ---------------------------------------------------------------------------

@dataclass
class TLSConfig:
    ca_cert:     Optional[str] = None   # path to CA cert PEM
    client_cert: Optional[str] = None   # path to client cert PEM
    client_key:  Optional[str] = None   # path to client key PEM
    insecure:    bool = False            # skip cert verification (dev only)


# ---------------------------------------------------------------------------
# P2LClient
# ---------------------------------------------------------------------------

class P2LClient:
    """
    Async MQTT client for the P2L bridge.

    Usage (async context manager):
        async with P2LClient("broker-host") as p2l:
            await p2l.light_on("0001", quantity=3, color=LightColor.GREEN)

    Usage (manual):
        p2l = P2LClient("broker-host")
        await p2l.connect()
        await p2l.light_on("0001", quantity=3)
        await p2l.disconnect()
    """

    DEFAULT_PORT           = 8883
    COMMAND_TOPIC          = "p2l/commands"
    EVENT_TOPIC            = "p2l/events"
    DEVICES_TOPIC          = "p2l/devices/#"
    STATUS_TOPIC           = "p2l/status"

    def __init__(
        self,
        host: str,
        port: int = DEFAULT_PORT,
        *,
        username: Optional[str] = None,
        password: Optional[str] = None,
        client_id: str = "p2l-sdk",
        tls: Optional[TLSConfig] = None,
    ):
        self._host      = host
        self._port      = port
        self._client_id = client_id

        self._loop: asyncio.AbstractEventLoop | None = None
        self._connected = asyncio.Event()
        # Resolved (None) on successful connect; raises ConnectionError on failure.
        # Only live during the connect() call; None the rest of the time.
        self._connect_done: Optional[asyncio.Future[None]] = None

        # Callbacks registered by the user
        self._on_confirm:        Optional[Callable[[ConfirmEvent], None]] = None
        self._on_batch_complete: Optional[Callable[[], None]] = None
        self._on_state:          Optional[Callable[[ModuleState], None]] = None
        self._on_bridge_status:  Optional[Callable[[BridgeStatus], None]] = None

        # Task-event (data-processing) callbacks
        self._on_task_event:    Optional[Callable[[TaskEvent], None]] = None
        self._on_task_started:  Optional[Callable[[TaskEvent], None]] = None
        self._on_task_confirmed: Optional[Callable[[TaskEvent], None]] = None
        self._on_task_error:    Optional[Callable[[TaskEvent], None]] = None
        self._on_csv_complete:  Optional[Callable[[], None]] = None

        # Pending waiters: address -> Future[ConfirmEvent]
        self._confirm_waiters: dict[str, asyncio.Future[ConfirmEvent]] = {}
        # Batch-complete waiter
        self._batch_complete_waiter: Optional[asyncio.Future[None]] = None
        # CSV-complete waiter
        self._csv_complete_waiter: Optional[asyncio.Future[None]] = None

        # paho client
        self._mqtt = mqtt.Client(
            client_id=client_id,
            protocol=mqtt.MQTTv5,
            callback_api_version=mqtt.CallbackAPIVersion.VERSION2,
        )
        if username:
            self._mqtt.username_pw_set(username, password)

        # Port 8883 is the standard MQTTS port — auto-enable TLS with system CAs
        # when no explicit TLS config is provided (same convention as HTTPS on 443).
        if tls is None and port == 8883:
            tls = TLSConfig()

        if tls:
            ctx = ssl.create_default_context()
            if tls.insecure:
                ctx.check_hostname = False
                ctx.verify_mode = ssl.CERT_NONE
            if tls.ca_cert:
                ctx.load_verify_locations(tls.ca_cert)
            if tls.client_cert and tls.client_key:
                ctx.load_cert_chain(tls.client_cert, tls.client_key)
            self._mqtt.tls_set_context(ctx)

        self._mqtt.on_connect    = self._on_connect
        self._mqtt.on_disconnect = self._on_disconnect
        self._mqtt.on_message    = self._on_message

    # ------------------------------------------------------------------
    # Context manager
    # ------------------------------------------------------------------

    async def __aenter__(self) -> "P2LClient":
        await self.connect()
        return self

    async def __aexit__(self, *_) -> None:
        await self.disconnect()

    # ------------------------------------------------------------------
    # Connection
    # ------------------------------------------------------------------

    async def connect(self, timeout: float = 10.0) -> None:
        """Connect to the MQTT broker and wait until ready."""
        self._loop = asyncio.get_running_loop()
        self._connected.clear()
        self._connect_done = self._loop.create_future()
        self._mqtt.connect_async(self._host, self._port, keepalive=60)
        self._mqtt.loop_start()
        try:
            await asyncio.wait_for(asyncio.shield(self._connect_done), timeout)
        except asyncio.TimeoutError:
            self._mqtt.loop_stop()
            raise ConnectionError(
                f"Timed out connecting to broker at {self._host}:{self._port} "
                f"(no response within {timeout}s)"
            )
        except ConnectionError:
            self._mqtt.loop_stop()
            raise
        finally:
            self._connect_done = None

    async def disconnect(self) -> None:
        self._mqtt.disconnect()
        self._mqtt.loop_stop()

    # ------------------------------------------------------------------
    # paho callbacks (called from paho thread → schedule on event loop)
    # ------------------------------------------------------------------

    def _on_connect(self, client, userdata, flags, reason_code, properties):
        if reason_code == 0:
            client.subscribe([
                (self.EVENT_TOPIC,   1),
                (self.DEVICES_TOPIC, 0),
                (self.STATUS_TOPIC,  0),
            ])
            if self._loop:
                def _resolve():
                    self._connected.set()
                    if self._connect_done and not self._connect_done.done():
                        self._connect_done.set_result(None)
                self._loop.call_soon_threadsafe(_resolve)
        else:
            logger.error("MQTT connect rejected by broker: %s", reason_code)
            if self._loop:
                def _reject():
                    if self._connect_done and not self._connect_done.done():
                        self._connect_done.set_exception(
                            ConnectionError(f"Broker rejected connection: {reason_code}")
                        )
                self._loop.call_soon_threadsafe(_reject)

    def _on_disconnect(self, client, userdata, disconnect_flags, reason_code, properties):
        if self._loop:
            def _handle():
                # During connect phase: treat any disconnect as a connect failure.
                if self._connect_done and not self._connect_done.done():
                    self._connect_done.set_exception(
                        ConnectionError(
                            f"Could not connect to {self._host}:{self._port} — "
                            f"{reason_code} (check host, port, TLS, credentials)"
                        )
                    )
                    self._connected.clear()
                    return
                # After a successful connection: log and clear.
                was_connected = self._connected.is_set()
                self._connected.clear()
                if was_connected and reason_code != 0:
                    logger.warning("MQTT disconnected unexpectedly: %s", reason_code)
            self._loop.call_soon_threadsafe(_handle)

    def _on_message(self, client, userdata, msg: mqtt.MQTTMessage):
        if self._loop:
            self._loop.call_soon_threadsafe(self._dispatch, msg.topic, msg.payload)

    def _dispatch(self, topic: str, payload: bytes) -> None:
        try:
            data: dict = json.loads(payload)
        except Exception:
            return

        kind = data.get("type", "")

        if kind == "confirmed":
            ev = ConfirmEvent.from_dict(data)
            waiter = self._confirm_waiters.pop(ev.address, None)
            if waiter and not waiter.done():
                waiter.set_result(ev)
            if self._on_confirm:
                self._on_confirm(ev)

        elif kind == "batch_completed":
            if self._batch_complete_waiter and not self._batch_complete_waiter.done():
                self._batch_complete_waiter.set_result(None)
            if self._on_batch_complete:
                self._on_batch_complete()

        elif kind in ("task_started", "task_confirmed", "task_error", "csv_completed"):
            ev = TaskEvent.from_dict(data)
            if self._on_task_event:
                self._on_task_event(ev)
            if kind == "task_started" and self._on_task_started:
                self._on_task_started(ev)
            elif kind == "task_confirmed" and self._on_task_confirmed:
                self._on_task_confirmed(ev)
            elif kind == "task_error" and self._on_task_error:
                self._on_task_error(ev)
            elif kind == "csv_completed":
                if self._csv_complete_waiter and not self._csv_complete_waiter.done():
                    self._csv_complete_waiter.set_result(None)
                if self._on_csv_complete:
                    self._on_csv_complete()

        elif kind == "light_changed":
            state = ModuleState.from_dict(data)
            if self._on_state:
                self._on_state(state)

        elif kind == "bridge_status":
            bs = BridgeStatus(
                online=data.get("online", False),
                gateway_connected=data.get("gatewayConnected", False),
            )
            if self._on_bridge_status:
                self._on_bridge_status(bs)

    # ------------------------------------------------------------------
    # Event subscriptions
    # ------------------------------------------------------------------

    def on_confirm(self, fn: Callable[[ConfirmEvent], None]) -> None:
        """Register callback: called when any module button is pressed."""
        self._on_confirm = fn

    def on_batch_complete(self, fn: Callable[[], None]) -> None:
        """Register callback: called when all batch items are confirmed."""
        self._on_batch_complete = fn

    def on_state_change(self, fn: Callable[[ModuleState], None]) -> None:
        """Register callback: called when a module's state changes."""
        self._on_state = fn

    def on_bridge_status(self, fn: Callable[[BridgeStatus], None]) -> None:
        """Register callback: called when P2L bridge health changes."""
        self._on_bridge_status = fn

    # ------------------------------------------------------------------
    # Data-processing (task_event) subscriptions
    # ------------------------------------------------------------------

    def on_task_event(self, fn: Callable[[TaskEvent], None]) -> None:
        """Register callback: called for every task lifecycle event (all types)."""
        self._on_task_event = fn

    def on_task_started(self, fn: Callable[[TaskEvent], None]) -> None:
        """Register callback: called when a CSV row is instructed (light turned on)."""
        self._on_task_started = fn

    def on_task_confirmed(self, fn: Callable[[TaskEvent], None]) -> None:
        """Register callback: called when an operator confirms a CSV task row."""
        self._on_task_confirmed = fn

    def on_task_error(self, fn: Callable[[TaskEvent], None]) -> None:
        """Register callback: called when a CSV task row cannot be processed."""
        self._on_task_error = fn

    def on_csv_complete(self, fn: Callable[[], None]) -> None:
        """Register callback: called when every row in the CSV has been confirmed."""
        self._on_csv_complete = fn

    # ------------------------------------------------------------------
    # Wait helpers (one-shot async)
    # ------------------------------------------------------------------

    async def wait_confirm(self, address: str, timeout: float = 30.0) -> ConfirmEvent:
        """Wait until the operator presses the button on *address*."""
        loop = asyncio.get_running_loop()
        fut: asyncio.Future[ConfirmEvent] = loop.create_future()
        self._confirm_waiters[address] = fut
        try:
            return await asyncio.wait_for(fut, timeout)
        except asyncio.TimeoutError:
            self._confirm_waiters.pop(address, None)
            raise TimeoutError(f"No confirmation from {address} within {timeout}s")

    async def wait_batch_complete(self, timeout: float = 120.0) -> None:
        """Wait until all items in the current batch are confirmed."""
        loop = asyncio.get_running_loop()
        fut: asyncio.Future[None] = loop.create_future()
        self._batch_complete_waiter = fut
        try:
            await asyncio.wait_for(fut, timeout)
        except asyncio.TimeoutError:
            self._batch_complete_waiter = None
            raise TimeoutError(f"Batch not completed within {timeout}s")
        finally:
            self._batch_complete_waiter = None

    async def wait_csv_complete(self, timeout: float = 300.0) -> None:
        """Wait until every row in the running CSV pick task has been confirmed."""
        loop = asyncio.get_running_loop()
        fut: asyncio.Future[None] = loop.create_future()
        self._csv_complete_waiter = fut
        try:
            await asyncio.wait_for(fut, timeout)
        except asyncio.TimeoutError:
            self._csv_complete_waiter = None
            raise TimeoutError(f"CSV pick task not completed within {timeout}s")
        finally:
            self._csv_complete_waiter = None

    # ------------------------------------------------------------------
    # Commands
    # ------------------------------------------------------------------

    def _publish(self, payload: dict) -> None:
        self._mqtt.publish(self.COMMAND_TOPIC, json.dumps(payload), qos=1)

    def light_on(
        self,
        address: str,
        *,
        quantity: int = 1,
        color: LightColor = LightColor.GREEN,
        led_mode: LightMode = LightMode.ON,
        buz_mode: Optional[LightMode] = None,
        label: Optional[str] = None,
    ) -> None:
        """Turn on a single light module."""
        cmd: dict = {
            "type":     "light_on",
            "address":  address,
            "quantity": quantity,
            "color":    color.value,
            "ledMode":  led_mode.value,
        }
        if buz_mode is not None:
            cmd["buzMode"] = buz_mode.value
        if label:
            cmd["label"] = label
        self._publish(cmd)

    def light_off(self, address: str) -> None:
        """Turn off a single light module."""
        self._publish({"type": "light_off", "address": address})

    def batch_start(
        self,
        items: list[BatchItem],
        *,
        led_mode: LightMode = LightMode.ON,
        buz_mode: Optional[LightMode] = None,
    ) -> None:
        """Start a picking batch (lights up all items simultaneously)."""
        cmd: dict = {
            "type":    "batch_start",
            "ledMode": led_mode.value,
            "items":   [i.to_dict() for i in items],
        }
        if buz_mode is not None:
            cmd["buzMode"] = buz_mode.value
        self._publish(cmd)

    def batch_finish(self, block_no: str = "00") -> None:
        """Terminate the current batch."""
        self._publish({"type": "batch_finish", "blockNo": block_no})

    def terminate_all(self) -> None:
        """Emergency: turn off all modules immediately."""
        self._publish({"type": "terminate_all"})
