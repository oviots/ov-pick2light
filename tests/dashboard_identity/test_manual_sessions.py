"""Actual route/schema/publish functions with inert state; no app bootstrap or sockets."""
import ast
import asyncio
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import types
from typing import Optional
import unittest
from uuid import uuid4

from fastapi import HTTPException
from pydantic import BaseModel, Field

ROOT = Path(__file__).resolve().parents[2]


def load_routes():
    # Load only the handlers under test, never app.main (which loads runtime config/DB).
    names = {
        "CommandIdentity", "LightRequest", "BatchItem", "BatchRequest", "_identity",
        "_resolve_block", "_manual_owner", "_manual_session", "_publish", "light_on", "start_batch",
    }
    tree = ast.parse((ROOT / "client/app/api/routes.py").read_text())
    selected = [node for node in tree.body if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)) and node.name in names]
    for node in selected:
        node.decorator_list = []
    ns = {
        "__name__": "manual_routes_under_test", "Request": object,
        "Optional": Optional, "BaseModel": BaseModel, "Field": Field,
        "HTTPException": HTTPException, "json": json,
        "_config": lambda request: request.app.state.config,
        "_mqtt": lambda request: request.app.state.publisher,
        "_parse_color": lambda value: value.upper(), "_parse_led_mode": lambda value: value.upper(),
        "_COLOR_TO_MQTT": {"GREEN": "Green"}, "_STATE_TO_MQTT": {"ON": "On"},
    }
    module = ast.Module(body=selected, type_ignores=[])
    ast.fix_missing_locations(module)
    exec(compile(module, "manual_routes_under_test.py", "exec"), ns)
    return ns


def load_publish():
    tree = ast.parse((ROOT / "client/app/mqtt/client.py").read_text())
    cls = next(node for node in tree.body if isinstance(node, ast.ClassDef) and node.name == "MqttClient")
    publish = next(node for node in cls.body if isinstance(node, ast.AsyncFunctionDef) and node.name == "publish_command")
    ns = {"asyncio": asyncio, "json": json, "uuid4": uuid4, "datetime": datetime, "timedelta": timedelta, "timezone": timezone}
    module = ast.Module(body=[publish], type_ignores=[])
    ast.fix_missing_locations(module)
    exec(compile(module, "mqtt_publish_under_test.py", "exec"), ns)
    return ns["publish_command"]


class Publisher:
    publish_command = load_publish()

    def __init__(self):
        self._is_connected = True
        self._publish_queue = asyncio.Queue(maxsize=128)
        self._pending = {}
        self._cfg = types.SimpleNamespace(command_topic="p2l/commands")

    @property
    def is_connected(self):
        return self._is_connected


def request(client_id="dashboard-one", block_no="01"):
    cfg = types.SimpleNamespace(mqtt=types.SimpleNamespace(client_id=client_id), block_no=block_no)
    return types.SimpleNamespace(app=types.SimpleNamespace(state=types.SimpleNamespace(config=cfg, publisher=Publisher())))


async def capture(req, calls):
    tasks = [asyncio.create_task(call) for call in calls]
    packets = []
    try:
        for _ in tasks:
            item = await asyncio.wait_for(req.app.state.publisher._publish_queue.get(), 1)
            packets.append(json.loads(item["payload"]))
            item["result"].set_result(None)  # Simulated applied response; no transport.
        await asyncio.gather(*tasks)
        return packets
    finally:
        for task in tasks:
            if not task.done():
                task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)


class ManualSessionTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.routes = load_routes()

    async def light(self, req, address="0014", **values):
        body = self.routes["LightRequest"](**values)
        return (await capture(req, [self.routes["light_on"](req, address, body)]))[0]

    async def test_missing_block_uses_config_and_explicit_global_remains_global(self):
        req = request()
        default = await self.light(req, quantity=1, color="GREEN", led_mode="ON")
        explicit = await self.light(req, block_no="00")
        self.assertEqual("01", default["blockNo"])
        self.assertEqual("00", explicit["blockNo"])
        self.assertNotEqual(default["correlationId"], explicit["correlationId"])

    async def test_sequential_lights_share_owner_but_not_command_or_item_identity(self):
        req = request()
        first = await self.light(req, "0014")
        second = await self.light(req, "0015")
        self.assertTrue(first["correlationId"].startswith("manual:"))
        self.assertEqual(first["correlationId"], second["correlationId"])
        self.assertNotEqual(first["commandId"], second["commandId"])
        self.assertNotEqual(first["commandItemId"], second["commandItemId"])

    async def test_concurrent_light_requests_share_one_owner_without_merging_intents(self):
        req = request()
        packets = await capture(req, [self.routes["light_on"](req, address, self.routes["LightRequest"]()) for address in ("0014", "0015", "0016")])
        self.assertEqual(1, len({packet["correlationId"] for packet in packets}))
        self.assertEqual(3, len({packet["commandId"] for packet in packets}))
        self.assertEqual(3, len({packet["commandItemId"] for packet in packets}))
        self.assertEqual({"0014", "0015", "0016"}, {packet["address"] for packet in packets})

    async def test_manual_owner_isolated_by_client_and_block(self):
        first = await self.light(request("client-a", "01"))
        other_client = await self.light(request("client-b", "01"))
        other_block = await self.light(request("client-a", "02"))
        self.assertEqual(3, len({first["correlationId"], other_client["correlationId"], other_block["correlationId"]}))

    async def test_manual_owner_survives_client_restart_without_adopting_registry_state(self):
        first = await self.light(request())
        self.routes = load_routes()  # Fresh route namespace, no in-memory session manager.
        restarted = await self.light(request())
        self.assertEqual(first["correlationId"], restarted["correlationId"])
        self.assertNotEqual(first["commandItemId"], restarted["commandItemId"])

    async def test_explicit_session_command_item_and_expiry_are_preserved(self):
        packet = await self.light(request(), correlation_id="caller-owned", command_id="intent-explicit",
                                  command_item_id="item-explicit", picking_detail_id="detail-explicit",
                                  expires_at="2099-01-01T00:00:00Z", block_no="02")
        self.assertEqual("caller-owned", packet["correlationId"])
        self.assertEqual("intent-explicit", packet["commandId"])
        self.assertEqual("item-explicit", packet["commandItemId"])
        self.assertEqual("detail-explicit", packet["pickingDetailId"])
        self.assertEqual("2099-01-01T00:00:00Z", packet["expiresAt"])
        self.assertEqual("02", packet["blockNo"])

    async def test_missing_client_id_requires_explicit_owner_before_publish(self):
        for value in (None, "", "  "):
            with self.subTest(client_id=value):
                req = request(value)
                with self.assertRaises(HTTPException) as failure:
                    await self.routes["light_on"](req, "0014", self.routes["LightRequest"]())
                self.assertEqual(422, failure.exception.status_code)
                self.assertIn("MQTT client ID", failure.exception.detail)
                self.assertTrue(req.app.state.publisher._publish_queue.empty())
        packet = await self.light(request(""), correlation_id="explicit-owner")
        self.assertEqual("explicit-owner", packet["correlationId"])

    async def test_batch_uses_config_block_without_sharing_manual_or_other_batch_owner(self):
        req = request()
        manual = await self.light(req)
        body = self.routes["BatchRequest"](items=[{"address": "0014", "quantity": 1}])
        first = (await capture(req, [self.routes["start_batch"](req, body)]))[0]
        second = (await capture(req, [self.routes["start_batch"](req, body)]))[0]
        self.assertEqual("01", first["blockNo"])
        self.assertEqual(3, len({manual["correlationId"], first["correlationId"], second["correlationId"]}))
        explicit = self.routes["BatchRequest"](block_no="00", correlation_id="explicit-batch", items=body.items)
        packet = (await capture(req, [self.routes["start_batch"](req, explicit)]))[0]
        self.assertEqual("00", packet["blockNo"])
        self.assertEqual("explicit-batch", packet["correlationId"])


if __name__ == "__main__":
    unittest.main()
