"""REST API and WebSocket endpoints.

All commands are forwarded to the P2L service via MQTT.
No direct Aioi hardware communication happens in this process.
"""
import json
import logging
from pathlib import Path
from typing import Any, Optional

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
    buz_mode: Optional[str] = Field(default=None)
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


@router.post("/api/self/restart")
async def self_restart():
    """Schedule a restart of the P2LClient service itself (cross-platform)."""
    import asyncio
    import sys

    async def _do_restart():
        await asyncio.sleep(1.5)   # let the HTTP response reach the browser first
        _restart_self()

    asyncio.get_event_loop().create_task(_do_restart())
    return {"status": "restarting"}


def _restart_self() -> None:
    """Restart the P2LClient process/service on any platform."""
    import subprocess
    import sys

    plat = sys.platform

    if plat == "win32":
        # Running as Windows SCM service → net stop/start; fallback to re-exec
        try:
            subprocess.run(["net", "stop", "P2LClient"], check=False,
                           capture_output=True, timeout=30)
            subprocess.run(["net", "start", "P2LClient"], check=False,
                           capture_output=True, timeout=30)
            return
        except Exception:
            pass

    elif plat == "darwin":
        try:
            subprocess.run(
                ["launchctl", "kickstart", "-k", "system/com.p2l.client"],
                check=False, capture_output=True, timeout=15,
            )
            return
        except Exception:
            pass

    else:  # Linux
        try:
            subprocess.run(
                ["systemctl", "restart", "p2l-client"],
                check=False, capture_output=True, timeout=30,
            )
            return
        except Exception:
            pass

    # Universal fallback: re-exec this process (works when run directly)
    import os
    os.execv(sys.executable, [sys.executable] + sys.argv)


# ─── P2L Bridge service control (cross-platform) ─────────────────────────────

def _p2l_svc_status() -> dict:
    """Return running state of the P2L bridge service on any platform."""
    import subprocess
    import sys

    plat = sys.platform
    try:
        if plat == "win32":
            r = subprocess.run(
                ["sc", "query", "P2L"], capture_output=True, text=True, timeout=5
            )
            running = "RUNNING" in r.stdout
        elif plat == "darwin":
            r = subprocess.run(
                ["launchctl", "list", "com.p2l.service"],
                capture_output=True, text=True, timeout=5,
            )
            running = r.returncode == 0
        else:
            r = subprocess.run(
                ["systemctl", "is-active", "--quiet", "p2l"],
                capture_output=True, timeout=5,
            )
            running = r.returncode == 0
    except Exception:
        running = False

    return {"running": running}


@router.get("/api/p2l-service/status")
async def p2l_service_status():
    return _p2l_svc_status()


@router.post("/api/p2l-service/start")
async def p2l_service_start(request: Request):
    import subprocess, sys
    plat = sys.platform
    cfg = _config(request)
    try:
        if plat == "win32":
            subprocess.run(["net", "start", "P2L"], check=False,
                           capture_output=True, timeout=30)
        elif plat == "darwin":
            subprocess.run(
                ["launchctl", "kickstart", "system/com.p2l.service"],
                check=False, capture_output=True, timeout=15,
            )
        else:
            subprocess.run(["systemctl", "start", "p2l"],
                           check=False, capture_output=True, timeout=30)
    except Exception as exc:
        # Fallback: launch binary directly if configured
        if cfg.service.binary_path:
            from .. import service as svc
            await svc.start(cfg.service.binary_path, cfg.service.args,
                            cfg.service.working_dir)
        else:
            raise HTTPException(status_code=500, detail=str(exc))
    return _p2l_svc_status()


@router.post("/api/p2l-service/stop")
async def p2l_service_stop():
    import subprocess, sys
    plat = sys.platform
    try:
        if plat == "win32":
            subprocess.run(["net", "stop", "P2L"], check=False,
                           capture_output=True, timeout=30)
        elif plat == "darwin":
            subprocess.run(
                ["launchctl", "stop", "com.p2l.service"],
                check=False, capture_output=True, timeout=15,
            )
        else:
            subprocess.run(["systemctl", "stop", "p2l"],
                           check=False, capture_output=True, timeout=30)
    except Exception as exc:
        raise HTTPException(status_code=500, detail=str(exc))
    return _p2l_svc_status()


@router.post("/api/scan-devices")
async def scan_devices(request: Request, addr_from: int = 1, addr_to: int = 99):
    if addr_from < 1 or addr_to < addr_from or addr_to > 9999:
        raise HTTPException(status_code=400, detail="Address range must be 1–9999 with from ≤ to")
    await _publish(request, {"type": "scan_devices", "from": addr_from, "to": addr_to})
    return {"status": "scanning"}


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
    items = []
    for item in body.items:
        led = _parse_led_mode(item.led_mode)
        entry: dict = {
            "address":  item.address.zfill(4)[-4:],
            "quantity": item.quantity,
            "color":    _COLOR_TO_MQTT.get(_parse_color(item.color), "Green"),
            "ledMode":  _STATE_TO_MQTT.get(led, "On"),
            "label":    item.label or None,
        }
        if item.buz_mode:
            buz = _parse_led_mode(item.buz_mode)
            entry["buzMode"] = _STATE_TO_MQTT.get(buz, "On")
        elif not body.buzz:
            entry["buzMode"] = "Off"
        items.append(entry)
    payload: dict = {
        "type":  "batch_start",
        "items": items,
    }
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


# ─── appsettings.json (P2L .NET bridge config) ───────────────────────────────

def _appsettings_path(request: Request) -> Path:
    """Locate p2l_config.json: config/ subdir next to p2l binary, or in ProgramData/P2L/config/."""
    from .._paths import app_data_dir
    cfg = _config(request)
    if cfg.service.binary_path:
        candidate = Path(cfg.service.binary_path).parent / "config" / "p2l_config.json"
        if candidate.exists():
            return candidate
    return app_data_dir() / "config" / "p2l_config.json"


def _read_appsettings(path: Path) -> dict:
    if path.exists():
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except Exception:
            pass
    return {}


def _deep_merge(base: dict, overlay: dict) -> dict:
    """Recursively merge overlay into base (modifies base in-place)."""
    for k, v in overlay.items():
        if isinstance(v, dict) and isinstance(base.get(k), dict):
            _deep_merge(base[k], v)
        else:
            base[k] = v
    return base


class AppSettingsRequest(BaseModel):
    # Gateway
    gateway_host: Optional[str] = None
    gateway_port: Optional[int] = None
    gateway_block_no: Optional[str] = None
    # MQTT (bridge)
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
    mqtt_client_db_path: Optional[str] = None
    mqtt_tls_enabled: Optional[bool] = None
    mqtt_tls_allow_untrusted: Optional[bool] = None
    mqtt_tls_ca_cert_path: Optional[str] = None
    # SelfHeal
    selfheal_enabled: Optional[bool] = None
    selfheal_max_restarts: Optional[int] = None
    selfheal_restart_window_minutes: Optional[int] = None
    selfheal_backoff_seconds: Optional[int] = None
    # Misc
    enable_state_persistence: Optional[bool] = None
    log_level: Optional[str] = None


@router.get("/api/appsettings")
async def get_appsettings(request: Request):
    path = _appsettings_path(request)
    data = _read_appsettings(path)
    gw   = data.get("Gateway", {})
    mq   = data.get("Mqtt", {})
    tls  = mq.get("Tls", {})
    sh   = data.get("SelfHeal", {})
    log  = data.get("Logging", {}).get("LogLevel", {})
    return {
        "path": str(path),
        "exists": path.exists(),
        "gateway": {
            "host":     gw.get("Host", ""),
            "port":     gw.get("Port", 5003),
            "block_no": gw.get("BlockNo", "00"),
        },
        "mqtt": {
            "enabled":        mq.get("Enabled", True),
            "host":           mq.get("Host", ""),
            "port":           mq.get("Port", 8883),
            "client_id":      mq.get("ClientId", ""),
            "username":       mq.get("Username", ""),
            "command_topic":  mq.get("CommandTopic", "p2l/commands"),
            "event_topic":    mq.get("EventTopic", "p2l/events"),
            "devices_topic":  mq.get("DevicesTopic", "p2l/devices"),
            "status_topic":   mq.get("StatusTopic", "p2l/status"),
            "client_db_path": mq.get("ClientDbPath", ""),
            "tls": {
                "enabled":            tls.get("Enabled", True),
                "allow_untrusted":    tls.get("AllowUntrustedCerts", False),
                "ca_cert_path":       tls.get("CaCertPath") or "",
            },
        },
        "selfheal": {
            "enabled":                 sh.get("Enabled", True),
            "max_restarts":            sh.get("MaxRestarts", 5),
            "restart_window_minutes":  sh.get("RestartWindowMinutes", 10),
            "backoff_seconds":         sh.get("BackOffSeconds", 3),
        },
        "enable_state_persistence": data.get("EnableStatePersistence", False),
        "log_level": log.get("Default", "Information"),
    }


@router.put("/api/appsettings")
async def update_appsettings(request: Request, body: AppSettingsRequest):
    path = _appsettings_path(request)
    data = _read_appsettings(path)

    # Build overlay from non-None fields
    overlay: dict[str, Any] = {}

    gw_patch: dict = {}
    if body.gateway_host       is not None: gw_patch["Host"]    = body.gateway_host
    if body.gateway_port       is not None: gw_patch["Port"]    = body.gateway_port
    if body.gateway_block_no   is not None: gw_patch["BlockNo"] = body.gateway_block_no
    if gw_patch: overlay["Gateway"] = gw_patch

    tls_patch: dict = {}
    if body.mqtt_tls_enabled        is not None: tls_patch["Enabled"]           = body.mqtt_tls_enabled
    if body.mqtt_tls_allow_untrusted is not None: tls_patch["AllowUntrustedCerts"] = body.mqtt_tls_allow_untrusted
    if body.mqtt_tls_ca_cert_path   is not None: tls_patch["CaCertPath"]        = body.mqtt_tls_ca_cert_path or None

    mq_patch: dict = {}
    if body.mqtt_enabled        is not None: mq_patch["Enabled"]      = body.mqtt_enabled
    if body.mqtt_host           is not None: mq_patch["Host"]          = body.mqtt_host
    if body.mqtt_port           is not None: mq_patch["Port"]          = body.mqtt_port
    if body.mqtt_client_id      is not None: mq_patch["ClientId"]      = body.mqtt_client_id
    if body.mqtt_username       is not None: mq_patch["Username"]      = body.mqtt_username or None
    if body.mqtt_password       is not None: mq_patch["Password"]      = body.mqtt_password or None
    if body.mqtt_command_topic  is not None: mq_patch["CommandTopic"]  = body.mqtt_command_topic
    if body.mqtt_event_topic    is not None: mq_patch["EventTopic"]    = body.mqtt_event_topic
    if body.mqtt_devices_topic  is not None: mq_patch["DevicesTopic"]  = body.mqtt_devices_topic
    if body.mqtt_status_topic   is not None: mq_patch["StatusTopic"]   = body.mqtt_status_topic
    if body.mqtt_client_db_path is not None: mq_patch["ClientDbPath"]  = body.mqtt_client_db_path
    if tls_patch:                            mq_patch["Tls"]           = tls_patch
    if mq_patch: overlay["Mqtt"] = mq_patch

    sh_patch: dict = {}
    if body.selfheal_enabled                is not None: sh_patch["Enabled"]              = body.selfheal_enabled
    if body.selfheal_max_restarts           is not None: sh_patch["MaxRestarts"]          = body.selfheal_max_restarts
    if body.selfheal_restart_window_minutes is not None: sh_patch["RestartWindowMinutes"] = body.selfheal_restart_window_minutes
    if body.selfheal_backoff_seconds        is not None: sh_patch["BackOffSeconds"]       = body.selfheal_backoff_seconds
    if sh_patch: overlay["SelfHeal"] = sh_patch

    if body.enable_state_persistence is not None:
        overlay["EnableStatePersistence"] = body.enable_state_persistence
    if body.log_level is not None:
        overlay.setdefault("Logging", {}).setdefault("LogLevel", {})["Default"] = body.log_level

    _deep_merge(data, overlay)
    try:
        path.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
    except OSError as e:
        raise HTTPException(status_code=500, detail=f"Cannot write {path}: {e}")

    return {"status": "saved", "path": str(path)}


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
