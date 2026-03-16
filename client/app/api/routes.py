"""REST API and WebSocket endpoints.

All commands are forwarded to the P2L service via MQTT.
No direct Aioi hardware communication happens in this process.
"""
import logging
from typing import Optional

from fastapi import APIRouter, HTTPException, Request, WebSocket, WebSocketDisconnect
from fastapi.responses import HTMLResponse
from fastapi.templating import Jinja2Templates
from pydantic import BaseModel, Field

from ..protocol.enums import LightColor, LightState
from ..mqtt.client import _COLOR_TO_MQTT, _STATE_TO_MQTT

logger = logging.getLogger(__name__)

router = APIRouter()


def _get_templates(request: Request) -> Jinja2Templates:
    import os
    base = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    return Jinja2Templates(directory=os.path.join(base, "templates"))


# ─── Accessors ────────────────────────────────────────────────────────────────

def _mqtt(request: Request):
    from ..main import get_mqtt_client
    return get_mqtt_client()

def _registry(request: Request):
    return request.app.state.registry

def _config(request: Request):
    return request.app.state.config


# ─── Schemas ──────────────────────────────────────────────────────────────────

class LightRequest(BaseModel):
    quantity: int = Field(default=1, ge=0, le=99999)
    color: str = Field(default="GREEN")
    led_mode: str = Field(default="ON")
    buzz: bool = Field(default=True)
    block_no: str = Field(default="00")
    label: str = Field(default="")


class BatchItem(BaseModel):
    address: str
    quantity: int = Field(ge=0, le=99999)
    color: str = Field(default="GREEN")
    led_mode: str = Field(default="ON")
    label: str = Field(default="")


class BatchRequest(BaseModel):
    block_no: str = Field(default="00")
    buzz: bool = Field(default=True)
    items: list[BatchItem]


class ConfigRequest(BaseModel):
    block_no: Optional[str] = None
    auto_connect: Optional[bool] = None
    mqtt_enabled: Optional[bool] = None
    mqtt_host: Optional[str] = None
    mqtt_port: Optional[int] = None
    mqtt_client_id: Optional[str] = None
    mqtt_username: Optional[str] = None
    mqtt_password: Optional[str] = None
    mqtt_command_topic: Optional[str] = None
    mqtt_event_topic: Optional[str] = None
    mqtt_devices_topic: Optional[str] = None
    mqtt_status_topic: Optional[str] = None
    mqtt_tls_enabled: Optional[bool] = None
    mqtt_tls_allow_untrusted: Optional[bool] = None
    mqtt_tls_ca_cert_path: Optional[str] = None
    service_binary_path: Optional[str] = None
    service_args: Optional[list] = None
    service_working_dir: Optional[str] = None
    service_auto_start: Optional[bool] = None


class AddDeviceRequest(BaseModel):
    address: str
    label: str = ""
    order_no: int = 0


class UpdateDeviceRequest(BaseModel):
    label: Optional[str] = None
    order_no: Optional[int] = None


# ─── Helpers ──────────────────────────────────────────────────────────────────

def _parse_color(name: str) -> LightColor:
    try:
        return LightColor[name.upper()]
    except KeyError:
        return LightColor.GREEN


def _parse_led_mode(name: str) -> LightState:
    try:
        return LightState[name.upper()]
    except KeyError:
        return LightState.ON


async def _publish(request: Request, command: dict) -> None:
    """Publish a command to the P2L service via MQTT."""
    mc = _mqtt(request)
    if mc is None or not mc.is_connected:
        raise HTTPException(status_code=503, detail="Not connected to MQTT broker")
    try:
        await mc.publish_command(command)
    except Exception as exc:
        raise HTTPException(status_code=500, detail=str(exc))


# ─── Page ──────────────────────────────────────────────────────────────────────

@router.get("/", response_class=HTMLResponse)
async def index(request: Request):
    return _get_templates(request).TemplateResponse("index.html", {"request": request})


# ─── WebSocket ────────────────────────────────────────────────────────────────

@router.websocket("/ws")
async def websocket_endpoint(websocket: WebSocket):
    from ..main import get_mqtt_client
    ws_mgr = websocket.app.state.ws_manager
    registry = websocket.app.state.registry
    cfg = websocket.app.state.config

    await ws_mgr.connect(websocket)
    try:
        from .. import service as svc
        mc = get_mqtt_client()
        binary = cfg.service.binary_path
        service_running = svc.is_running(binary) if binary else False
        await websocket.send_json({
            "type": "init",
            "data": {
                "connected": service_running,
                "mqtt_connected": mc.is_connected if mc else False,
                "host": cfg.mqtt.host,
                "port": cfg.mqtt.port,
                "devices": [m.to_dict() for m in registry.all],
            },
        })
        while True:
            data = await websocket.receive_text()
            if data == "ping":
                await websocket.send_text("pong")
    except WebSocketDisconnect:
        pass
    except Exception as exc:
        logger.debug("WebSocket error: %s", exc)
    finally:
        ws_mgr.disconnect(websocket)


# ─── Status ───────────────────────────────────────────────────────────────────

@router.get("/api/status")
async def get_status(request: Request):
    from .. import service as svc
    mc = _mqtt(request)
    reg = _registry(request)
    cfg = _config(request)
    binary = cfg.service.binary_path
    service_running = svc.is_running(binary) if binary else False
    return {
        "connected": service_running,
        "mqtt_connected": mc.is_connected if mc else False,
        "host": cfg.mqtt.host,
        "port": cfg.mqtt.port,
        "total_devices": reg.total_count,
        "active_devices": reg.active_count,
        "pending_confirm": reg.pending_confirm_count,
    }


# ─── Connection ────────────────────────────────────────────────────────────────

@router.post("/api/connect")
async def connect(request: Request):
    import asyncio
    from .. import service as svc
    cfg = _config(request)
    svc_cfg = cfg.service

    # ── 1. Optionally start the P2L .NET service ─────────────────────────────
    service_started = False
    if svc_cfg.auto_start and svc_cfg.binary_path:
        already = svc.is_running(svc_cfg.binary_path)
        if not already:
            try:
                await svc.start(
                    svc_cfg.binary_path,
                    svc_cfg.args,
                    svc_cfg.working_dir,
                )
                service_started = True
                # Short grace period so the service can connect to the broker
                await asyncio.sleep(2)
            except Exception as exc:
                raise HTTPException(status_code=503, detail=f"Failed to start P2L service: {exc}")

    # ── 2. Connect to MQTT broker ─────────────────────────────────────────────
    try:
        await request.app.state.do_connect()
    except Exception as exc:
        raise HTTPException(status_code=500, detail=str(exc))

    return {"status": "connecting", "service_started": service_started}


@router.post("/api/disconnect")
async def disconnect(request: Request):
    try:
        await request.app.state.do_disconnect()
    except Exception as exc:
        raise HTTPException(status_code=500, detail=str(exc))
    return {"status": "disconnected"}


# ─── Service management ────────────────────────────────────────────────────

@router.get("/api/service/status")
async def service_status(request: Request):
    from .. import service as svc
    cfg = _config(request)
    binary = cfg.service.binary_path
    running = svc.is_running(binary) if binary else False
    return {"running": running, "binary_path": binary}


@router.post("/api/service/start")
async def service_start(request: Request):
    import asyncio
    from .. import service as svc
    cfg = _config(request)
    svc_cfg = cfg.service
    if not svc_cfg.binary_path:
        raise HTTPException(status_code=400, detail="Service binary path not configured")
    if svc.is_running(svc_cfg.binary_path):
        return {"status": "already_running"}
    try:
        await svc.start(svc_cfg.binary_path, svc_cfg.args, svc_cfg.working_dir)
    except Exception as exc:
        raise HTTPException(status_code=503, detail=str(exc))
    return {"status": "started"}


@router.post("/api/service/stop")
async def service_stop(request: Request):
    from .. import service as svc
    try:
        await svc.stop()
    except Exception as exc:
        raise HTTPException(status_code=500, detail=str(exc))
    return {"status": "stopped"}


# ─── Device management ────────────────────────────────────────────────────────

@router.get("/api/devices")
async def list_devices(request: Request):
    return {"devices": [m.to_dict() for m in _registry(request).all]}


@router.post("/api/devices")
async def add_device(request: Request, body: AddDeviceRequest):
    from ..db import upsert_device
    module = _registry(request).add_device(body.address, body.label, body.order_no)
    upsert_device(module.address, module.label, module.order_no)
    return module.to_dict()


@router.delete("/api/devices")
async def clear_devices(request: Request):
    from ..db import clear_devices as db_clear
    _registry(request).clear()
    db_clear()
    return {"status": "cleared"}


@router.delete("/api/devices/{address}")
async def remove_device(request: Request, address: str):
    from ..db import remove_device as db_remove
    _registry(request).remove_device(address)
    db_remove(address)
    return {"status": "removed"}


@router.patch("/api/devices/{address}")
async def update_device(request: Request, address: str, body: UpdateDeviceRequest):
    from ..db import update_label, update_order, upsert_device
    reg = _registry(request)
    module = reg.get(address)
    if module is None:
        module = reg.add_device(
            address,
            body.label or "",
            body.order_no if body.order_no is not None else 0,
        )
        upsert_device(module.address, module.label, module.order_no)
    else:
        if body.label is not None:
            module.label = body.label
            update_label(module.address, body.label)
        if body.order_no is not None:
            module.order_no = body.order_no
            update_order(module.address, body.order_no)
        reg._notify()
    return module.to_dict()


@router.post("/api/devices/{address}/light")
async def light_on(request: Request, address: str, body: LightRequest):
    c = _parse_color(body.color)
    s = _parse_led_mode(body.led_mode)
    payload: dict = {
        "type":     "light_on",
        "address":  address.zfill(4)[-4:],
        "quantity": body.quantity,
        "color":    _COLOR_TO_MQTT.get(c, "Green"),
        "ledMode":  _STATE_TO_MQTT.get(s, "On"),
        "label":    body.label or None,
    }
    if not body.buzz:
        payload["buzMode"] = "Off"
    await _publish(request, payload)
    return {"status": "sent", "address": address}


@router.post("/api/devices/{address}/off")
async def light_off(request: Request, address: str):
    await _publish(request, {
        "type":    "light_off",
        "address": address.zfill(4)[-4:],
    })
    return {"status": "sent", "address": address}


# ─── Batch ────────────────────────────────────────────────────────────────────

@router.post("/api/batch/start")
async def start_batch(request: Request, body: BatchRequest):
    if not body.items:
        raise HTTPException(status_code=400, detail="No items in batch")
    first_mode = _parse_led_mode(body.items[0].led_mode)
    items = [
        {
            "address":  item.address.zfill(4)[-4:],
            "quantity": item.quantity,
            "color":    _COLOR_TO_MQTT.get(_parse_color(item.color), "Green"),
            "label":    item.label or None,
        }
        for item in body.items
    ]
    payload: dict = {
        "type":    "batch_start",
        "ledMode": _STATE_TO_MQTT.get(first_mode, "On"),
        "items":   items,
    }
    if not body.buzz:
        payload["buzMode"] = "Off"
    await _publish(request, payload)
    return {"status": "sent", "count": len(items)}


@router.post("/api/batch/finish")
async def finish_batch(request: Request):
    cfg = _config(request)
    await _publish(request, {
        "type":    "batch_finish",
        "blockNo": cfg.block_no,
    })
    return {"status": "sent"}


@router.get("/api/batch/status")
async def batch_status(request: Request):
    reg = _registry(request)
    from ..protocol.enums import ModuleStatus
    active = [m.address for m in reg.all
              if m.status in (ModuleStatus.OPERATING, ModuleStatus.OPERATING2)]
    pending = [m.address for m in reg.all if m.status == ModuleStatus.COMPLETED]
    return {
        "active":   len(active) > 0,
        "pending":  pending,
        "complete": len(active) == 0 and len(pending) == 0,
    }


# ─── Terminate all ────────────────────────────────────────────────────────────

@router.post("/api/terminate-all")
async def terminate_all(request: Request):
    await _publish(request, {"type": "terminate_all"})
    return {"status": "sent"}


# ─── Configuration ────────────────────────────────────────────────────────────

@router.get("/api/config")
async def get_config(request: Request):
    cfg = _config(request).to_dict()
    # Strip password from API response
    cfg["mqtt"].pop("password", None)
    return cfg


@router.put("/api/config")
async def update_config(request: Request, body: ConfigRequest):
    cfg = _config(request)
    if body.block_no is not None:
        cfg.block_no = body.block_no
    if body.auto_connect is not None:
        cfg.auto_connect = body.auto_connect
    if body.mqtt_enabled is not None:
        cfg.mqtt.enabled = body.mqtt_enabled
    if body.mqtt_host is not None:
        cfg.mqtt.host = body.mqtt_host
    if body.mqtt_port is not None:
        cfg.mqtt.port = body.mqtt_port
    if body.mqtt_client_id is not None:
        cfg.mqtt.client_id = body.mqtt_client_id
    if body.mqtt_username is not None:
        cfg.mqtt.username = body.mqtt_username
    if body.mqtt_password is not None:
        cfg.mqtt.password = body.mqtt_password
    if body.mqtt_command_topic is not None:
        cfg.mqtt.command_topic = body.mqtt_command_topic
    if body.mqtt_event_topic is not None:
        cfg.mqtt.event_topic = body.mqtt_event_topic
    if body.mqtt_devices_topic is not None:
        cfg.mqtt.devices_topic = body.mqtt_devices_topic
    if body.mqtt_status_topic is not None:
        cfg.mqtt.status_topic = body.mqtt_status_topic
    if body.mqtt_tls_enabled is not None:
        cfg.mqtt.tls.enabled = body.mqtt_tls_enabled
    if body.mqtt_tls_allow_untrusted is not None:
        cfg.mqtt.tls.allow_untrusted_certs = body.mqtt_tls_allow_untrusted
    if body.mqtt_tls_ca_cert_path is not None:
        cfg.mqtt.tls.ca_cert_path = body.mqtt_tls_ca_cert_path
    if body.service_binary_path is not None:
        cfg.service.binary_path = body.service_binary_path
    if body.service_args is not None:
        cfg.service.args = body.service_args
    if body.service_working_dir is not None:
        cfg.service.working_dir = body.service_working_dir
    if body.service_auto_start is not None:
        cfg.service.auto_start = body.service_auto_start
    cfg.save()
    out = cfg.to_dict()
    out["mqtt"].pop("password", None)
    return out


# ─── Logs ─────────────────────────────────────────────────────────────────────

@router.get("/api/logs")
async def get_logs(request: Request):
    from ..main import logs
    return {"logs": list(logs)}


@router.delete("/api/logs")
async def clear_logs(request: Request):
    from ..main import logs
    logs.clear()
    return {"status": "cleared"}
