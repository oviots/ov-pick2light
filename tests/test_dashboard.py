import asyncio
import base64
import contextlib
import json
import sys
from pathlib import Path
from types import SimpleNamespace

import httpx
import pytest

sys.path.insert(0, str(Path(__file__).parents[1] / "client"))
from app import config as config_module
from app import main, service
from app.config import MqttConfig
from app.mqtt.client import MqttClient
from app.security import DashboardAccess


def request(peer, path="/api/status", headers=None, method="GET"):
    async def run():
        transport = httpx.ASGITransport(app=main.app, client=(peer, 50000))
        async with httpx.AsyncClient(transport=transport, base_url="http://localhost") as client:
            return await client.request(method, path, headers=headers)
    return asyncio.run(run())


def test_local_dashboard_works_but_remote_and_cross_origin_commands_are_blocked(monkeypatch):
    monkeypatch.delenv("P2L_CLIENT_TOKEN", raising=False)
    assert request("127.0.0.1").status_code == 200
    assert request("10.1.1.1", "/api/terminate-all", method="POST").status_code == 403
    assert request("127.0.0.1", "/api/terminate-all", {"Origin": "https://untrusted.invalid"}, "POST").status_code == 403
    assert request("127.0.0.1", headers={"Host": "untrusted.invalid"}).status_code == 403


def test_configured_token_protects_remote_http_and_supports_browser_basic_auth(monkeypatch):
    monkeypatch.setenv("P2L_CLIENT_TOKEN", "test-token")
    assert request("10.1.1.1").status_code == 401
    assert request("10.1.1.1", headers={"Authorization": "Bearer wrong"}).status_code == 401
    basic = base64.b64encode(b"p2l:test-token").decode()
    assert request("10.1.1.1", headers={"Authorization": "Basic " + basic}).status_code == 200


def test_websocket_uses_the_same_access_rules(monkeypatch):
    monkeypatch.setenv("P2L_CLIENT_TOKEN", "test-token")
    async def run(authorization):
        sent = []
        async def inner(scope, receive, send):
            await send({"type": "websocket.accept"})
        async def send(message):
            sent.append(message)
        scope = {"type": "websocket", "client": ("10.1.1.1", 1000),
                 "headers": [(b"host", b"localhost"), (b"authorization", authorization)]}
        await DashboardAccess(inner)(scope, None, send)
        return sent
    assert asyncio.run(run(b"")) == [{"type": "websocket.close", "code": 1008}]
    assert asyncio.run(run(b"Bearer test-token")) == [{"type": "websocket.accept"}]


def test_bootstrap_client_has_a_distinct_mqtt_identity(tmp_path, monkeypatch):
    (tmp_path / "config").mkdir()
    (tmp_path / "config/p2l_config.json").write_text(json.dumps({"Mqtt": {"ClientId": "bridge-01"}}))
    monkeypatch.setattr(config_module, "__file__", str(tmp_path / "client/app/config.py"))
    monkeypatch.setattr(config_module, "CONFIG_PATH", tmp_path / "missing.json")
    cfg = config_module.AppConfig.load()
    assert cfg.mqtt.client_id == "bridge-01-webclient"


@pytest.mark.parametrize("binary,args", [("sh", ["-c", "unsafe"]), ("p2l", ["run", "tasks.csv"]), ("p2l", ["serve", "unsafe"])])
def test_dashboard_cannot_launch_arbitrary_processes(tmp_path, monkeypatch, binary, args):
    monkeypatch.setattr(service, "is_running", lambda _: False)
    async def forbidden(*a, **kw):
        pytest.fail("A process must not be created")
    monkeypatch.setattr(asyncio, "create_subprocess_exec", forbidden)
    with pytest.raises(ValueError):
        asyncio.run(service.start(str(tmp_path / binary), args, str(tmp_path)))


def test_failed_publish_reaches_the_caller_instead_of_silent_dequeue():
    async def run():
        client = MqttClient(MqttConfig())
        client._is_connected = True
        pending = asyncio.create_task(client.publish_command({"type": "light_on", "address": "0001"}))
        await asyncio.sleep(0)
        class FailedBroker:
            async def publish(self, *a, **kw):
                raise ConnectionError("injected disconnect")
        with pytest.raises(ConnectionError):
            await client._pub_loop(FailedBroker())
        with pytest.raises(ConnectionError, match="uncertain"):
            await pending
        assert client._publish_queue.empty()
        assert not client._pending
    asyncio.run(run())


@pytest.mark.parametrize("status", ["applied", "rejected", "uncertain"])
def test_http_command_waits_for_bridge_execution_result(status):
    async def run():
        client = MqttClient(MqttConfig())
        client._is_connected = True
        commands = []
        class Broker:
            async def publish(self, topic, **kw):
                commands.append(json.loads(kw["payload"]))
        producer = asyncio.create_task(client.publish_command({"type": "light_on", "address": "0001"}))
        sender = asyncio.create_task(client._pub_loop(Broker()))
        for _ in range(20):
            if commands:
                break
            await asyncio.sleep(0.001)
        assert len(commands) == 1
        assert not producer.done(), "Broker PUBACK must not complete the command"
        async def messages():
            yield SimpleNamespace(topic="p2l/events", retain=False, payload=json.dumps({
                "type": "command_result", "commandId": commands[0]["commandId"], "status": status,
                "message": "injected hardware failure"}).encode())
        await client._recv_loop(SimpleNamespace(messages=messages()))
        if status == "applied":
            await producer
        else:
            with pytest.raises(RuntimeError, match="hardware failure"):
                await producer
        sender.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await sender
    asyncio.run(run())


def test_stop_fails_pending_requests_without_sending_them_later():
    async def run():
        client = MqttClient(MqttConfig())
        client._is_connected = True
        pending = asyncio.create_task(client.publish_command({"type": "light_on", "address": "0001"}))
        await asyncio.sleep(0)
        await client.stop()
        with pytest.raises(ConnectionError, match="stopped"):
            await pending
        assert not client._pending
    asyncio.run(run())


def test_unix_daemon_and_installer_configuration_paths_match_the_loader():
    root = Path(__file__).parents[1]
    assert 'Path.Combine("config", "p2l_config.json")' in (root / "src/P2L.CLI/Program.cs").read_text()
    for name in ("p2l.service", "install-linux.sh", "install-macos.sh", "package.sh"):
        text = (root / "deploy" / name).read_text()
        assert "p2l run " not in text
        assert "<string>run</string>" not in text
        if name != "p2l.service":
            assert "config/p2l_config.json" in text
            assert '"$INSTALL_DIR/appsettings.json"' not in text


def test_windows_package_and_service_use_the_same_configuration_and_daemon_command():
    root = Path(__file__).parents[1]
    installer = (root / "deploy/install-windows.ps1").read_text()
    package = (root / "deploy/package.ps1").read_text()
    assert '"config\\p2l_config.json"' in installer
    assert '"config\\p2l_config.json"' in package
    assert '"$p2lExe`" serve' in installer
    assert 'run `"$CsvTaskFile' not in installer
    assert "P2L_CLIENT_HOST=127.0.0.1" in installer
    assert "P2L_CLIENT_HOST=0.0.0.0" not in installer
    assert 'Set-Acl $DataDir $dataAcl' in installer


def test_runtime_data_uses_the_installers_writable_directory_on_unix():
    root = Path(__file__).parents[1]
    program = (root / "src/P2L.CLI/Program.cs").read_text()
    assert "var appDataBase = OperatingSystem.IsWindows()" in program
    assert 'Path.Combine(AppContext.BaseDirectory, "data")' in program
    assert program.index("Directory.CreateDirectory(appDataBase)") < program.index("var config =")
    embedded_installer = (root / "deploy/package.sh").read_text()
    assert embedded_installer.index('INSTALL_DIR="$HOME/Library/Application Support/P2L"') < embedded_installer.index('mkdir -p "$INSTALL_DIR/config"')


def test_packaged_template_has_canonical_sections_and_no_embedded_credentials():
    root = Path(__file__).parents[1]
    cfg = json.loads((root / "deploy/appsettings.template.json").read_text())
    assert isinstance(cfg["Gateway"]["Host"], str) and cfg["Gateway"]["Host"]
    assert 1 <= cfg["Gateway"]["Port"] <= 65535
    assert isinstance(cfg["Mqtt"]["Host"], str) and cfg["Mqtt"]["Host"]
    assert isinstance(cfg["Mqtt"]["Enabled"], bool)
    assert not cfg["Mqtt"]["Username"]
    assert not cfg["Mqtt"]["Password"]
    assert not cfg["Mqtt"]["Tls"].get("CertPassword")
    for topic in ("CommandTopic", "EventTopic", "DevicesTopic", "StatusTopic"):
        assert isinstance(cfg["Mqtt"][topic], str) and cfg["Mqtt"][topic]
