"""Application configuration."""
import json
from dataclasses import dataclass, field

from ._paths import app_data_dir

CONFIG_PATH = app_data_dir() / "config" / "p2l_local_settings.json"


@dataclass
class TlsConfig:
    enabled: bool = True
    ca_cert_path: str | None = None
    ca_cert_pem: str | None = None
    client_cert_path: str | None = None
    client_key_path: str | None = None
    cert_password: str | None = None
    allow_untrusted_certs: bool = False


@dataclass
class MqttConfig:
    enabled: bool = False
    host: str = "localhost"
    port: int = 8883
    client_id: str = "p2l-webclient"
    username: str | None = None
    password: str | None = None
    command_topic: str = "p2l/commands"
    event_topic: str = "p2l/events"
    devices_topic: str = "p2l/devices"
    status_topic: str = "p2l/status"
    tls: TlsConfig = field(default_factory=TlsConfig)


@dataclass
class ServiceConfig:
    binary_path: str = ""        # Absolute path to the p2l binary
    args: list = field(default_factory=list)   # Extra CLI arguments (e.g. ["run", "tasks.csv"])
    working_dir: str = ""        # Working directory; defaults to binary's parent dir
    auto_start: bool = True      # Start the service automatically before connecting MQTT


@dataclass
class AppConfig:
    mqtt: MqttConfig = field(default_factory=MqttConfig)
    service: ServiceConfig = field(default_factory=ServiceConfig)
    block_no: str = "00"        # Default block sent with batch_finish / terminate_all
    auto_connect: bool = False  # Auto-connect to MQTT broker on startup

    def save(self) -> None:
        # Only harmless machine-local service preferences are persisted.
        data = {"service": {"binary_path": self.service.binary_path,
                "args": self.service.args, "working_dir": self.service.working_dir,
                "auto_start": self.service.auto_start}}
        CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
        CONFIG_PATH.write_text(json.dumps(data, indent=2))

    @classmethod
    def load(cls) -> "AppConfig":
        # Legacy p2l_client_config.json and project MQTT config are intentionally ignored.
        if not CONFIG_PATH.exists():
            return cls()
        data = json.loads(CONFIG_PATH.read_text())
        service = data.get("service", {})
        return cls(service=ServiceConfig(binary_path=service.get("binary_path", ""),
            args=service.get("args", ["serve"]), working_dir=service.get("working_dir", ""),
            auto_start=service.get("auto_start", True)))

    def to_dict(self) -> dict:
        tls = self.mqtt.tls
        return {
            "mqtt": {
                "enabled": self.mqtt.enabled,
                "host": self.mqtt.host,
                "port": self.mqtt.port,
                "client_id": self.mqtt.client_id,
                "username": self.mqtt.username,
                "command_topic": self.mqtt.command_topic,
                "event_topic": self.mqtt.event_topic,
                "devices_topic": self.mqtt.devices_topic,
                "status_topic": self.mqtt.status_topic,
                "tls": {
                    "enabled": tls.enabled,
                    "ca_cert_path": tls.ca_cert_path,
                    "client_cert_path": tls.client_cert_path,
                    "client_key_path": tls.client_key_path,
                    "allow_untrusted_certs": tls.allow_untrusted_certs,
                    # cert_password intentionally omitted
                },
            },
            "service": {
                "binary_path": self.service.binary_path,
                "args": self.service.args,
                "working_dir": self.service.working_dir,
                "auto_start": self.service.auto_start,
            },
            "block_no": self.block_no,
            "auto_connect": self.auto_connect,
        }
