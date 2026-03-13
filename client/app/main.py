"""FastAPI application — P2L Web Dashboard.

The client communicates exclusively with the P2L .NET service via MQTT.
No direct TCP connection to Aioi hardware is made from this process.

Data flow:
  Browser  →  FastAPI REST  →  MQTT broker  →  P2L service  →  Aioi device
  Browser  ←  WebSocket     ←  MQTT events  ←  P2L service  ←  Aioi device
"""
import asyncio
import logging
import os
from collections import deque
from contextlib import asynccontextmanager
from datetime import datetime, timezone
from typing import Optional

from fastapi import FastAPI
from fastapi.staticfiles import StaticFiles
from fastapi.templating import Jinja2Templates

from .api.routes import router
from .api.ws_manager import WebSocketManager
from .config import AppConfig
from .core.registry import LightModuleRegistry
from .mqtt.client import (
    MqttClient,
    mqtt_to_color,
    mqtt_to_completion_status,
    mqtt_to_module_status,
    mqtt_to_state,
)

logger = logging.getLogger(__name__)

# ─── Global state ──────────────────────────────────────────────────────────────

config: AppConfig = AppConfig.load()
registry: LightModuleRegistry = LightModuleRegistry()
ws_manager: WebSocketManager = WebSocketManager()
mqtt_client: Optional[MqttClient] = None
logs: deque = deque(maxlen=500)

BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TEMPLATES_DIR = os.path.join(BASE_DIR, "templates")
STATIC_DIR = os.path.join(BASE_DIR, "static")


# ─── Registry → WebSocket ──────────────────────────────────────────────────────

def _setup_registry_callbacks() -> None:
    def on_state_change(modules):
        asyncio.create_task(ws_manager.broadcast({
            "type": "state_update",
            "data": [m.to_dict() for m in modules],
        }))
    registry.set_state_change_callback(on_state_change)


# ─── MQTT ──────────────────────────────────────────────────────────────────────

def _setup_mqtt_client(mc: MqttClient) -> None:
    def on_state(connected: bool):
        asyncio.create_task(ws_manager.broadcast({
            "type": "connection_changed",
            "data": {
                "connected": connected,
                "host": config.mqtt.host,
                "port": config.mqtt.port,
            },
        }))
        _append_log(
            "info" if connected else "warning",
            f"MQTT broker {'connected' if connected else 'disconnected'}: "
            f"{config.mqtt.host}:{config.mqtt.port}",
        )

    mc.set_state_callback(on_state)
    mc.set_event_callback(_on_mqtt_event)


async def _on_mqtt_event(topic: str, payload: dict) -> None:
    """Route an inbound MQTT message → registry + WebSocket broadcast."""
    evt_type = payload.get("type")

    if evt_type == "confirmed":
        from .protocol.models import ConfirmationEvent
        event = ConfirmationEvent(
            address=payload.get("address", "0000"),
            status=mqtt_to_completion_status(payload.get("status", "Normal")),
            adjusted_display=payload.get("adjustedDisplay"),
            received_at=datetime.now(timezone.utc),
            seq_no=0,
        )
        registry.set_confirmed(event)
        await ws_manager.broadcast({"type": "confirmation", "data": event.to_dict()})

    elif evt_type == "light_changed":
        from .protocol.models import LightModuleState
        addr = payload.get("address", "0000")
        module = registry.get(addr) or LightModuleState(address=addr)
        module.status = mqtt_to_module_status(payload.get("moduleStatus", "Standby"))
        module.color = mqtt_to_color(payload.get("color", "Off"))
        module.led_mode = mqtt_to_state(payload.get("ledMode", "Off"))
        module.display_data = payload.get("displayData") or "00000"
        module.block_no = payload.get("blockNo") or "00"
        if payload.get("lastInstructionAt"):
            try:
                module.last_instruction_at = datetime.fromisoformat(
                    payload["lastInstructionAt"].replace("Z", "+00:00")
                )
            except ValueError:
                pass
        if payload.get("lastConfirmedAt"):
            try:
                module.last_confirmed_at = datetime.fromisoformat(
                    payload["lastConfirmedAt"].replace("Z", "+00:00")
                )
            except ValueError:
                pass
        registry._modules[registry._normalize(addr)] = module
        registry._notify()

    elif evt_type == "batch_completed":
        await ws_manager.broadcast({"type": "batch_completed", "data": {}})

    elif evt_type == "bridge_status":
        online = payload.get("online", False)
        gw_connected = payload.get("gatewayConnected", False)
        await ws_manager.broadcast({
            "type": "bridge_status",
            "data": {"online": online, "gateway_connected": gw_connected},
        })
        _append_log(
            "info" if online else "warning",
            f"P2L bridge {'online' if online else 'offline'}, "
            f"gateway {'connected' if gw_connected else 'disconnected'}",
        )


async def _do_connect() -> None:
    """Connect to the MQTT broker."""
    global mqtt_client
    if mqtt_client and mqtt_client.is_connected:
        return
    mc = MqttClient(config.mqtt)
    _setup_mqtt_client(mc)
    await mc.start()
    mqtt_client = mc


async def _do_disconnect() -> None:
    global mqtt_client
    if mqtt_client:
        await mqtt_client.stop()
        mqtt_client = None


def _append_log(level: str, message: str) -> None:
    entry = {"time": datetime.now(timezone.utc).isoformat(), "level": level, "message": message}
    logs.append(entry)
    asyncio.create_task(ws_manager.broadcast({"type": "log", "data": entry}))


# ─── Lifespan ──────────────────────────────────────────────────────────────────

@asynccontextmanager
async def lifespan(app: FastAPI):
    _setup_registry_callbacks()
    logger.info("P2L Web Dashboard → http://127.0.0.1:8765")
    if config.auto_connect:
        try:
            await _do_connect()
        except Exception as exc:
            logger.warning("Auto-connect failed: %s", exc)
    yield
    if mqtt_client:
        await mqtt_client.stop()


# ─── App ───────────────────────────────────────────────────────────────────────

app = FastAPI(title="P2L Web Dashboard", version="1.0.0", lifespan=lifespan)

app.state.do_connect    = _do_connect
app.state.do_disconnect = _do_disconnect

app.mount("/static", StaticFiles(directory=STATIC_DIR), name="static")
Jinja2Templates(directory=TEMPLATES_DIR)

app.include_router(router)

app.state.config     = config
app.state.registry   = registry
app.state.ws_manager = ws_manager


def get_mqtt_client() -> Optional[MqttClient]:
    return mqtt_client
