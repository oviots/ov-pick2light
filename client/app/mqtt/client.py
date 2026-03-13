"""Async MQTT client for the P2L Web Dashboard.

When MQTT mode is active, all pick commands are published to the command topic
instead of being sent directly over TCP to the Aioi gateway.  Incoming events
(confirmations, state changes, bridge status) are received from the broker and
forwarded to the WebSocket layer.

JSON naming convention matches the P2L.Mqtt C# bridge:
  - camelCase field names
  - PascalCase enum strings  (e.g. "Green", "On", "HighFlash", "Normal")
"""
import asyncio
import json
import logging
import ssl
from typing import Callable, Optional

from ..protocol.enums import (
    CompletionStatus,
    LightColor,
    LightState,
    ModuleStatus,
)

logger = logging.getLogger(__name__)

EventCallback = Callable[[str, dict], None]   # (topic, payload) → None
StateCallback = Callable[[bool], None]         # (connected) → None


# ── Enum name maps (Python ↔ MQTT/C# PascalCase) ──────────────────────────

_COLOR_TO_MQTT: dict[LightColor, str] = {
    LightColor.OFF:     "Off",
    LightColor.RED:     "Red",
    LightColor.GREEN:   "Green",
    LightColor.BLUE:    "Blue",
    LightColor.YELLOW:  "Yellow",
    LightColor.CYAN:    "Cyan",
    LightColor.MAGENTA: "Magenta",
    LightColor.WHITE:   "White",
}
_MQTT_TO_COLOR: dict[str, LightColor] = {v: k for k, v in _COLOR_TO_MQTT.items()}

_STATE_TO_MQTT: dict[LightState, str] = {
    LightState.OFF:        "Off",
    LightState.ON:         "On",
    LightState.FLASH:      "Flash",
    LightState.HIGH_FLASH: "HighFlash",
    LightState.NO_CHANGE:  "NoChange",
}
_MQTT_TO_STATE: dict[str, LightState] = {v: k for k, v in _STATE_TO_MQTT.items()}

_MQTT_TO_MODULE_STATUS: dict[str, ModuleStatus] = {
    "PowerOn":     ModuleStatus.POWER_ON,
    "Standby":     ModuleStatus.STANDBY,
    "Operating":   ModuleStatus.OPERATING,
    "Operating2":  ModuleStatus.OPERATING2,
    "Completed":   ModuleStatus.COMPLETED,
    "Shortage":    ModuleStatus.SHORTAGE,
    "Maintenance": ModuleStatus.MAINTENANCE,
    "Malfunction": ModuleStatus.MALFUNCTION,
}

_MQTT_TO_COMPLETION_STATUS: dict[str, CompletionStatus] = {
    "Normal":   CompletionStatus.NORMAL,
    "Abnormal": CompletionStatus.ABNORMAL,
    "Shortage": CompletionStatus.SHORTAGE,
}


def color_to_mqtt(c: LightColor) -> str:
    return _COLOR_TO_MQTT.get(c, "Green")


def state_to_mqtt(s: LightState) -> str:
    return _STATE_TO_MQTT.get(s, "On")


def mqtt_to_color(s: str) -> LightColor:
    return _MQTT_TO_COLOR.get(s, LightColor.GREEN)


def mqtt_to_state(s: str) -> LightState:
    return _MQTT_TO_STATE.get(s, LightState.ON)


def mqtt_to_module_status(s: str) -> ModuleStatus:
    return _MQTT_TO_MODULE_STATUS.get(s, ModuleStatus.STANDBY)


def mqtt_to_completion_status(s: str) -> CompletionStatus:
    return _MQTT_TO_COMPLETION_STATUS.get(s, CompletionStatus.NORMAL)


# ── MQTT Client ────────────────────────────────────────────────────────────

class MqttClient:
    """Manages the MQTT connection to the P2L broker.

    - Reconnects automatically with exponential backoff.
    - Commands are placed on an internal queue and sent by a background publish loop
      (safe to call from any async context, even during reconnects).
    - Incoming messages are dispatched to the registered event callback.
    """

    def __init__(self, config) -> None:
        from ..config import MqttConfig  # local import avoids circularity
        self._cfg: MqttConfig = config
        self._publish_queue: asyncio.Queue[dict] = asyncio.Queue(maxsize=128)
        self._is_connected = False
        self._on_event: Optional[EventCallback] = None
        self._on_state: Optional[StateCallback] = None
        self._stop = asyncio.Event()
        self._task: Optional[asyncio.Task] = None

    # ── Public API ─────────────────────────────────────────────────────────

    @property
    def is_connected(self) -> bool:
        return self._is_connected

    def set_event_callback(self, cb: EventCallback) -> None:
        self._on_event = cb

    def set_state_callback(self, cb: StateCallback) -> None:
        self._on_state = cb

    async def start(self) -> None:
        self._stop.clear()
        self._task = asyncio.create_task(self._connect_loop(), name="p2l-mqtt")

    async def stop(self) -> None:
        self._stop.set()
        if self._task and not self._task.done():
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass
        self._is_connected = False

    async def publish_command(self, command: dict) -> None:
        """Enqueue a command dict to be published on p2l/commands."""
        if not self._is_connected:
            raise ConnectionError("MQTT broker not connected")
        item = {
            "topic": self._cfg.command_topic,
            "payload": json.dumps(command, separators=(",", ":")),
            "qos": 1,
            "retain": False,
        }
        try:
            self._publish_queue.put_nowait(item)
        except asyncio.QueueFull:
            raise RuntimeError("MQTT publish queue full — broker too slow?")

    # ── Connection loop ────────────────────────────────────────────────────

    async def _connect_loop(self) -> None:
        import aiomqtt

        delay = 1.0
        while not self._stop.is_set():
            try:
                kwargs = self._build_kwargs()
                logger.info("MQTT connecting to %s:%d …", self._cfg.host, self._cfg.port)

                async with aiomqtt.Client(**kwargs) as client:
                    logger.info("MQTT connected to %s:%d", self._cfg.host, self._cfg.port)
                    self._is_connected = True
                    self._notify_state(True)
                    delay = 1.0  # reset backoff on success

                    await client.subscribe(self._cfg.event_topic, qos=1)
                    await client.subscribe(f"{self._cfg.devices_topic}/#", qos=0)
                    await client.subscribe(self._cfg.status_topic, qos=1)

                    recv_task = asyncio.create_task(
                        self._recv_loop(client), name="p2l-mqtt-recv"
                    )
                    pub_task = asyncio.create_task(
                        self._pub_loop(client), name="p2l-mqtt-pub"
                    )
                    try:
                        done, pending = await asyncio.wait(
                            [recv_task, pub_task],
                            return_when=asyncio.FIRST_EXCEPTION,
                        )
                        for t in pending:
                            t.cancel()
                        # Re-raise any real exception (not CancelledError)
                        for t in done:
                            if not t.cancelled():
                                exc = t.exception()
                                if exc:
                                    raise exc
                    except asyncio.CancelledError:
                        recv_task.cancel()
                        pub_task.cancel()
                        raise
                    finally:
                        recv_task.cancel()
                        pub_task.cancel()

            except asyncio.CancelledError:
                break
            except Exception as exc:
                logger.warning("MQTT error: %s", exc)

            if not self._stop.is_set():
                self._is_connected = False
                self._notify_state(False)
                logger.info("MQTT reconnecting in %.1fs …", delay)
                try:
                    await asyncio.wait_for(self._stop.wait(), timeout=delay)
                except asyncio.TimeoutError:
                    pass
                delay = min(delay * 2, 30.0)

        self._is_connected = False

    async def _recv_loop(self, client) -> None:
        async for message in client.messages:
            if self._stop.is_set():
                break
            try:
                payload = json.loads(message.payload.decode("utf-8", errors="replace"))
            except json.JSONDecodeError:
                continue
            if self._on_event:
                try:
                    await self._on_event(str(message.topic), payload)
                except Exception as exc:
                    logger.error("MQTT event callback error: %s", exc)

    async def _pub_loop(self, client) -> None:
        while not self._stop.is_set():
            try:
                item = await asyncio.wait_for(
                    self._publish_queue.get(), timeout=1.0
                )
            except asyncio.TimeoutError:
                continue
            try:
                await client.publish(
                    item["topic"],
                    payload=item["payload"],
                    qos=item.get("qos", 1),
                    retain=item.get("retain", False),
                )
                logger.debug("MQTT → %s : %s", item["topic"], item["payload"][:120])
            except Exception as exc:
                logger.error("MQTT publish error: %s", exc)
                raise  # Bubble up so connect_loop re-connects

    # ── Helpers ────────────────────────────────────────────────────────────

    def _build_kwargs(self) -> dict:
        kwargs: dict = {
            "hostname": self._cfg.host,
            "port":     self._cfg.port,
        }
        if self._cfg.client_id:
            kwargs["identifier"] = self._cfg.client_id
        if self._cfg.username:
            kwargs["username"] = self._cfg.username
        if self._cfg.password:
            kwargs["password"] = self._cfg.password

        tls = self._cfg.tls
        if tls.enabled:
            ctx = ssl.create_default_context()
            if tls.ca_cert_path:
                ctx.load_verify_locations(tls.ca_cert_path)
            if tls.client_cert_path and tls.client_key_path:
                ctx.load_cert_chain(tls.client_cert_path, tls.client_key_path,
                                    tls.cert_password)
            if tls.allow_untrusted_certs:
                ctx.check_hostname = False
                ctx.verify_mode = ssl.CERT_NONE
            kwargs["tls_context"] = ctx

        return kwargs

    def _notify_state(self, connected: bool) -> None:
        if self._on_state:
            try:
                self._on_state(connected)
            except Exception as exc:
                logger.error("MQTT state callback error: %s", exc)
