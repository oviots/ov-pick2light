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
import json
import logging
import ssl
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
    address:      str
    module_status: str
    color:        Optional[str] = None
    led_mode:     Optional[str] = None
    display_data: Optional[str] = None
    block_no:     Optional[str] = None

    @staticmethod
    def from_dict(d: dict) -> "ModuleState":
        return ModuleState(
            address=d["address"],
            module_status=d.get("moduleStatus", ""),
            color=d.get("color"),
            led_mode=d.get("ledMode"),
            display_data=d.get("displayData"),
            block_no=d.get("blockNo"),
        )


@dataclass
class BridgeStatus:
    online:            bool
    gateway_connected: bool


# ---------------------------------------------------------------------------
# Batch item helper
# ---------------------------------------------------------------------------

@dataclass
class BatchItem:
    address:  str
    quantity: int
    color:    LightColor = LightColor.GREEN
    label:    Optional[str] = None

    def to_dict(self) -> dict:
        d: dict = {"address": self.address, "quantity": self.quantity,
                   "color": self.color.value}
        if self.label:
            d["label"] = self.label
        return d


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

        # Callbacks registered by the user
        self._on_confirm:       Optional[Callable[[ConfirmEvent], None]] = None
        self._on_batch_complete: Optional[Callable[[], None]] = None
        self._on_state:         Optional[Callable[[ModuleState], None]] = None
        self._on_bridge_status: Optional[Callable[[BridgeStatus], None]] = None

        # Pending waiters: address -> Future[ConfirmEvent]
        self._confirm_waiters: dict[str, asyncio.Future[ConfirmEvent]] = {}
        # Batch-complete waiter
        self._batch_complete_waiter: Optional[asyncio.Future[None]] = None

        # paho client
        self._mqtt = mqtt.Client(
            client_id=client_id,
            protocol=mqtt.MQTTv5,
            callback_api_version=mqtt.CallbackAPIVersion.VERSION2,
        )
        if username:
            self._mqtt.username_pw_set(username, password)

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
        self._mqtt.connect_async(self._host, self._port, keepalive=60)
        self._mqtt.loop_start()
        try:
            await asyncio.wait_for(self._connected.wait(), timeout)
        except asyncio.TimeoutError:
            self._mqtt.loop_stop()
            raise TimeoutError(f"Could not connect to broker at {self._host}:{self._port}")

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
                self._loop.call_soon_threadsafe(self._connected.set)
        else:
            logger.error("MQTT connect failed: %s", reason_code)

    def _on_disconnect(self, client, userdata, disconnect_flags, reason_code, properties):
        if reason_code != 0:
            logger.warning("MQTT disconnected unexpectedly: %s", reason_code)
        if self._loop:
            self._loop.call_soon_threadsafe(self._connected.clear)

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
