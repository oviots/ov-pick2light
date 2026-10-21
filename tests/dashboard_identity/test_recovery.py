"""Recovery route and telemetry functions with fake queues only; no app/bootstrap IO."""
import ast
import asyncio
from datetime import datetime, timezone
import json
from pathlib import Path
import sys
import types
from typing import Literal, Optional
import unittest
from unittest.mock import patch

from fastapi import HTTPException
from pydantic import BaseModel, Field, ValidationError
import test_backend as B

ROOT = Path(__file__).resolve().parents[2]


def load_routes():
    names = {"CommandIdentity", "RecoveryRequest", "_manual_owner", "_publish", "recover_confirmation"}
    tree = ast.parse((ROOT / "client/app/api/routes.py").read_text())
    selected = [n for n in tree.body if isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)) and n.name in names]
    for node in selected:
        node.decorator_list = []
    ns = {"__name__": "app.api.recovery", "__package__": "app.api", "Request": object,
          "Optional": Optional, "Literal": Literal, "BaseModel": BaseModel, "Field": Field,
          "HTTPException": HTTPException, "json": json,
          "_config": lambda req: req.app.state.config, "_mqtt": lambda req: req.app.state.publisher,
          "_registry": lambda req: req.app.state.registry}
    module = ast.Module(body=selected, type_ignores=[])
    ast.fix_missing_locations(module)
    exec(compile(module, "recovery_routes.py", "exec"), ns)
    return ns


class RecoveryTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.routes = load_routes()

    def fixture(self, manual=True):
        registry = B.R.LightModuleRegistry()
        config = types.SimpleNamespace(mqtt=types.SimpleNamespace(client_id="dashboard"))
        publisher = B.C.MqttClient(types.SimpleNamespace(command_topic="commands", event_topic="events"))
        publisher._is_connected = True
        owner = self.routes["_manual_owner"]("dashboard", "01") if manual else "wms-session"
        module = B.M.LightModuleState("0001", status=B.E.ModuleStatus.OPERATING, display_data="00005",
            block_no="01", correlation_id=owner, command_item_id="item-1", picking_detail_id=None if manual else "detail-1",
            confirmation_missing_at=datetime.now(timezone.utc), confirmation_missing_event_id="missing-1")
        registry.update_module_state(module)
        request = types.SimpleNamespace(app=types.SimpleNamespace(state=types.SimpleNamespace(
            registry=registry, publisher=publisher, config=config)))
        values = dict(block_no="01", correlation_id=owner, command_item_id="item-1", picking_detail_id=module.picking_detail_id,
                      missing_event_id="missing-1", action="confirm", actual_quantity=0,
                      command_id="recovery-intent", expires_at="2099-01-01T00:00:00Z")
        return request, module, values

    async def enqueue(self, request, values):
        task = asyncio.create_task(self.routes["recover_confirmation"](request, "0001", self.routes["RecoveryRequest"](**values)))
        await asyncio.sleep(0)
        if task.done():
            await task  # Surface validation/route failure rather than hiding it behind queue timeout.
        item = request.app.state.publisher._publish_queue.get_nowait()
        self.addAsyncCleanup(self.cancel_task, task)
        return task, item, json.loads(item["payload"])

    async def cancel_task(self, task):
        if not task.done():
            task.cancel()
        await asyncio.gather(task, return_exceptions=True)

    async def outcome(self, request, status="applied"):
        payload = {"type": "command_result", "commandId": "recovery-intent", "status": status, "message": "result"}
        await request.app.state.publisher._recv_loop(types.SimpleNamespace(messages=B.FakeMessages([(payload, False)])))

    async def test_manual_confirm_zero_keeps_exact_identity_and_waits_for_application(self):
        req, module, values = self.fixture()
        task, item, payload = await self.enqueue(req, values)
        self.assertEqual("commands", item["topic"])
        self.assertEqual("resolve_confirmation", payload["type"])
        self.assertEqual(0, payload["actualQuantity"])
        self.assertEqual("recovery-intent", payload["commandId"])
        self.assertEqual("missing-1", payload["missingEventId"])
        self.assertEqual(values["correlation_id"], payload["correlationId"])
        self.assertEqual("item-1", payload["commandItemId"])
        self.assertEqual("2099-01-01T00:00:00Z", payload["expiresAt"])
        self.assertFalse(task.done())
        await self.outcome(req)
        self.assertEqual("applied", (await task)["status"])
        self.assertEqual(B.E.ModuleStatus.OPERATING, module.status)
        self.assertEqual("missing-1", module.confirmation_missing_event_id)

    async def test_wms_confirm_uses_event_ingress_and_keeps_original_detail(self):
        req, module, values = self.fixture(manual=False)
        values["actual_quantity"] = 2
        task, item, payload = await self.enqueue(req, values)
        self.assertEqual("events", item["topic"])
        self.assertEqual("recovery_requested", payload["type"])
        self.assertEqual("detail-1", payload["pickingDetailId"])
        self.assertEqual(2, payload["actualQuantity"])
        self.assertFalse(item["retain"])
        self.assertNotIn("eventId", payload)
        await self.outcome(req)
        await task
        self.assertIsNotNone(module.confirmation_missing_at)

    async def test_retry_has_no_implicit_quantity_and_cancel_is_manual_only(self):
        for manual in (True, False):
            req, _, values = self.fixture(manual)
            values.update(action="retry", actual_quantity=None)
            task, item, payload = await self.enqueue(req, values)
            self.assertNotIn("actualQuantity", payload)
            await self.outcome(req)
            await task
        req, _, values = self.fixture(manual=False)
        values.update(action="cancel", actual_quantity=None)
        with self.assertRaises(HTTPException) as error:
            await self.routes["recover_confirmation"](req, "0001", self.routes["RecoveryRequest"](**values))
        self.assertEqual(403, error.exception.status_code)
        self.assertTrue(req.app.state.publisher._publish_queue.empty())
        req, module, values = self.fixture()
        values.update(action="cancel", actual_quantity=None)
        task, _, payload = await self.enqueue(req, values)
        self.assertEqual("cancel", payload["action"])
        await self.outcome(req)
        await task
        self.assertEqual(B.E.ModuleStatus.OPERATING, module.status)

    async def test_stale_owner_block_item_detail_or_warning_never_publishes(self):
        for field, value in (("block_no", "02"), ("correlation_id", "other"), ("command_item_id", "next"),
                             ("picking_detail_id", "next-detail"), ("missing_event_id", "old-warning")):
            req, _, values = self.fixture(manual=False)
            values[field] = value
            with self.subTest(field=field), self.assertRaises(HTTPException) as error:
                await self.routes["recover_confirmation"](req, "0001", self.routes["RecoveryRequest"](**values))
            self.assertEqual(409, error.exception.status_code)
            self.assertTrue(req.app.state.publisher._publish_queue.empty())
        req, module, values = self.fixture()
        module.confirmation_missing_at = None
        with self.assertRaises(HTTPException):
            await self.routes["recover_confirmation"](req, "0001", self.routes["RecoveryRequest"](**values))

    async def test_confirm_requires_explicit_bounded_quantity_and_all_identity_fields(self):
        for quantity in (None, 6):
            req, _, values = self.fixture()
            values["actual_quantity"] = quantity
            with self.assertRaises(HTTPException) as error:
                await self.routes["recover_confirmation"](req, "0001", self.routes["RecoveryRequest"](**values))
            self.assertEqual(422, error.exception.status_code)
            self.assertTrue(req.app.state.publisher._publish_queue.empty())
        _, _, values = self.fixture()
        for field in ("block_no", "correlation_id", "command_item_id", "missing_event_id", "command_id", "expires_at"):
            with self.subTest(field=field), self.assertRaises(ValidationError):
                self.routes["RecoveryRequest"](**{**values, field: ""})
            missing = {key: value for key, value in values.items() if key != field}
            with self.subTest(missing=field), self.assertRaises(ValidationError):
                self.routes["RecoveryRequest"](**missing)
        for quantity in (-1, True, 100000, 1.5):
            with self.subTest(quantity=quantity), self.assertRaises(ValidationError):
                self.routes["RecoveryRequest"](**{**values, "actual_quantity": quantity})

    async def test_recovery_rejects_raw_address_aliases_before_registry_normalization(self):
        req, _, values = self.fixture()
        for address in ("10001", "-1", "", "00a1", "０００１", "0000"):
            with self.subTest(address=address), self.assertRaises(HTTPException) as error:
                await self.routes["recover_confirmation"](req, address, self.routes["RecoveryRequest"](**values))
            self.assertEqual(422, error.exception.status_code)
        self.assertTrue(req.app.state.publisher._publish_queue.empty())

    async def test_another_manual_client_cannot_directly_resolve_or_cancel(self):
        req, _, values = self.fixture()
        req.app.state.config.mqtt.client_id = "another-dashboard"
        with self.assertRaises(HTTPException) as error:
            await self.routes["recover_confirmation"](req, "0001", self.routes["RecoveryRequest"](**values))
        self.assertEqual(409, error.exception.status_code)  # No WMS detail/provenance to request approval.
        self.assertTrue(req.app.state.publisher._publish_queue.empty())

    async def test_actual_confirmation_event_clears_warning_and_keeps_manual_recovery_source(self):
        req, module, values = self.fixture()
        event = B.M.ConfirmationEvent("0001", B.E.CompletionStatus.NORMAL, "00000", datetime.now(timezone.utc), 0,
            correlation_id=module.correlation_id, command_item_id="item-1", event_id="recovered-event", source="manual_recovery")
        self.assertTrue(req.app.state.registry.set_confirmed(event))
        self.assertEqual(B.E.ModuleStatus.COMPLETED, module.status)
        self.assertIsNone(module.confirmation_missing_at)
        self.assertIsNone(module.confirmation_missing_event_id)
        self.assertEqual("00000", module.adjusted_quantity)
        self.assertEqual("manual_recovery", event.to_dict()["source"])

    async def test_rejection_and_timeout_preserve_warning_and_clear_only_pending_future(self):
        req, module, values = self.fixture(manual=False)
        task, _, _ = await self.enqueue(req, values)
        await self.outcome(req, "rejected")
        with self.assertRaises(HTTPException) as error:
            await task
        self.assertEqual(409, error.exception.status_code)
        self.assertEqual("rejected", error.exception.detail["execution_status"])
        self.assertEqual("missing-1", module.confirmation_missing_event_id)
        real_wait_for = asyncio.wait_for
        deadlines = []
        async def bounded(awaitable, timeout):
            deadlines.append(timeout)
            return await real_wait_for(awaitable, 0.01)
        req, module, values = self.fixture()
        with patch.object(B.C.asyncio, "wait_for", bounded), self.assertRaises(HTTPException):
            await self.routes["recover_confirmation"](req, "0001", self.routes["RecoveryRequest"](**values))
        self.assertEqual([30], deadlines)
        self.assertEqual({}, req.app.state.publisher._pending)
        self.assertIsNotNone(module.confirmation_missing_at)

    async def test_full_telemetry_maps_identity_and_absent_fields_clear_old_warning(self):
        req, module, _ = self.fixture()
        db = types.ModuleType("app.db")
        db.insert_if_new = lambda _: {"label": "", "order_no": 0}
        sys.modules["app.db"] = db
        tree = ast.parse((ROOT / "client/app/main.py").read_text())
        function = next(n for n in tree.body if isinstance(n, ast.AsyncFunctionDef) and n.name == "_on_mqtt_event")
        ns = {"__name__": "app.telemetry", "__package__": "app", "registry": req.app.state.registry,
              "config": req.app.state.config, "_mqtt_seen_addrs": set(), "LightModuleRegistry": B.R.LightModuleRegistry,
              "datetime": datetime, "timezone": timezone, "_manual_owner": self.routes["_manual_owner"],
              "mqtt_to_module_status": B.C.mqtt_to_module_status, "mqtt_to_color": B.C.mqtt_to_color, "mqtt_to_state": B.C.mqtt_to_state}
        code = ast.Module(body=[function], type_ignores=[])
        ast.fix_missing_locations(code)
        exec(compile(code, "telemetry.py", "exec"), ns)
        payload = dict(type="light_changed", address="0001", moduleStatus="Operating", blockNo="01", correlationId=module.correlation_id,
                       commandItemId="item-1", pickingDetailId=None, displayData="00005", observedStatus="Standby",
                       confirmationMissingAt="2026-10-21T01:00:00Z", confirmationMissingEventId="missing-1")
        await ns["_on_mqtt_event"]("devices", payload)
        data = module.to_dict()
        self.assertEqual("item-1", data["command_item_id"])
        self.assertEqual("STANDBY", data["observed_status"])
        self.assertEqual("missing-1", data["confirmation_missing_event_id"])
        self.assertTrue(data["can_cancel_test"])
        await ns["_on_mqtt_event"]("devices", dict(type="light_changed", address="0001", moduleStatus="Standby", blockNo="01"))
        self.assertIsNone(module.confirmation_missing_at)
        self.assertIsNone(module.confirmation_missing_event_id)
        self.assertIsNone(module.command_item_id)
        self.assertIsNone(module.picking_detail_id)
        self.assertFalse(module.can_cancel_test)


if __name__ == "__main__":
    unittest.main()
