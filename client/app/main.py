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

from ._paths import bundle_dir

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

# Tracks device addresses confirmed via MQTT retained messages since last connect.
# Used to reconcile the registry against stale SQLite-loaded entries.
_mqtt_seen_addrs: set[str] = set()

BASE_DIR = bundle_dir()
TEMPLATES_DIR = str(BASE_DIR / "templates")
STATIC_DIR = str(BASE_DIR / "static")


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
        from .service import is_running
        binary = config.service.binary_path
        service_running = is_running(binary) if binary else False
        asyncio.create_task(ws_manager.broadcast({
            "type": "connection_changed",
            "data": {
                "connected": service_running,
                "mqtt_connected": connected,
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
    global _mqtt_seen_addrs
    evt_type = payload.get("type")

    if evt_type == "confirmed":
        from .protocol.models import ConfirmationEvent
        event = ConfirmationEvent(
            address=payload.get("address", "0000"),
            status=mqtt_to_completion_status(payload.get("status", "Normal")),
            adjusted_display=payload.get("adjustedDisplay"),
            received_at=datetime.now(timezone.utc),
            seq_no=0,
            label=payload.get("label"),
            display_data=payload.get("displayData"),
        )
        registry.set_confirmed(event)
        await ws_manager.broadcast({"type": "confirmation", "data": event.to_dict()})

    elif evt_type == "light_changed":
        from .db import insert_if_new
        from .protocol.models import LightModuleState
        addr = payload.get("address", "0000")
        module = registry.get(addr) or LightModuleState(address=addr)
        stored = insert_if_new(LightModuleRegistry.normalize_address(addr))
        # Prefer label from MQTT event (enriched by .NET bridge) over DB fallback
        mqtt_label = payload.get("label")
        if mqtt_label:
            module.label = mqtt_label
        elif not module.label and stored["label"]:
            module.label = stored["label"]
        if not module.order_no:
            module.order_no = stored["order_no"]
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
        registry.update_module_state(module)
        _mqtt_seen_addrs.add(LightModuleRegistry.normalize_address(addr))

    elif evt_type == "batch_completed":
        await ws_manager.broadcast({"type": "batch_completed", "data": {}})

    elif evt_type == "bridge_status":
        online = payload.get("online", False)
        gw_connected = payload.get("gatewayConnected", False)
        if online and gw_connected and _mqtt_seen_addrs:
            # The server has completed its startup scan. Reconcile the local
            # registry: remove any entry that was loaded from the DB but was
            # not confirmed by an MQTT retained message (stale hardware entry).
            stale = [m.address for m in registry.all if m.address not in _mqtt_seen_addrs]
            for addr in stale:
                registry.remove_device(addr)
                logger.info("Removed stale device %s (not found in MQTT retained)", addr)
            _mqtt_seen_addrs.clear()
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
    """Connect to the MQTT broker and wait until the connection is established."""
    global mqtt_client
    if mqtt_client and mqtt_client.is_connected:
        return
    mc = MqttClient(config.mqtt)
    _setup_mqtt_client(mc)
    await mc.start()
    mqtt_client = mc
    await mc.wait_connected(timeout=15.0)


async def _do_disconnect() -> None:
    global mqtt_client, _mqtt_seen_addrs
    if mqtt_client:
        await mqtt_client.stop()
        mqtt_client = None
    _mqtt_seen_addrs.clear()  # reset so next connect tracks fresh retained messages


def _append_log(level: str, message: str) -> None:
    entry = {"time": datetime.now(timezone.utc).isoformat(), "level": level, "message": message}
    logs.append(entry)
    asyncio.create_task(ws_manager.broadcast({"type": "log", "data": entry}))


# ─── Lifespan ──────────────────────────────────────────────────────────────────

@asynccontextmanager
async def lifespan(app: FastAPI):
    from .db import init_db, load_devices
    init_db()
    for dev in load_devices():
        registry.add_device(dev["address"], dev["label"], dev.get("order_no", 0))
    _setup_registry_callbacks()
    logger.info("P2L Web Dashboard → http://127.0.0.1:8765")
    yield
    if mqtt_client:
        await mqtt_client.stop()


# ─── App ───────────────────────────────────────────────────────────────────────

app = FastAPI(title="P2L Web Dashboard", version="1.0.0", lifespan=lifespan)

app.state.do_connect    = _do_connect
app.state.do_disconnect = _do_disconnect

app.mount("/static", StaticFiles(directory=STATIC_DIR), name="static")

app.include_router(router)

app.state.config     = config
app.state.registry   = registry
app.state.ws_manager = ws_manager


def get_mqtt_client() -> Optional[MqttClient]:
    return mqtt_client
