"""Application configuration."""
import json
from dataclasses import dataclass, field
from pathlib import Path

CONFIG_PATH = Path(__file__).parent.parent / "p2l_config.json"


@dataclass
class TlsConfig:
    enabled: bool = True
    ca_cert_path: str | None = None
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
class AppConfig:
    mqtt: MqttConfig = field(default_factory=MqttConfig)
    block_no: str = "00"        # Default block sent with batch_finish / terminate_all
    auto_connect: bool = False  # Auto-connect to MQTT broker on startup

    def save(self) -> None:
        tls = self.mqtt.tls
        data = {
            "mqtt": {
                "enabled": self.mqtt.enabled,
                "host": self.mqtt.host,
                "port": self.mqtt.port,
                "client_id": self.mqtt.client_id,
                "username": self.mqtt.username,
                "password": self.mqtt.password,
                "command_topic": self.mqtt.command_topic,
                "event_topic": self.mqtt.event_topic,
                "devices_topic": self.mqtt.devices_topic,
                "status_topic": self.mqtt.status_topic,
                "tls": {
                    "enabled": tls.enabled,
                    "ca_cert_path": tls.ca_cert_path,
                    "client_cert_path": tls.client_cert_path,
                    "client_key_path": tls.client_key_path,
                    "cert_password": tls.cert_password,
                    "allow_untrusted_certs": tls.allow_untrusted_certs,
                },
            },
            "block_no": self.block_no,
            "auto_connect": self.auto_connect,
        }
        CONFIG_PATH.write_text(json.dumps(data, indent=2))

    @classmethod
    def load(cls) -> "AppConfig":
        if not CONFIG_PATH.exists():
            # Bootstrap from main project appsettings.json if present
            project_settings = Path(__file__).parent.parent.parent / "appsettings.json"
            if project_settings.exists():
                try:
                    data = json.loads(project_settings.read_text())
                    mq = data.get("Mqtt", {})
                    mq_tls = mq.get("Tls", {})
                    return cls(
                        mqtt=MqttConfig(
                            enabled=mq.get("Enabled", False),
                            host=mq.get("Host", "localhost"),
                            port=mq.get("Port", 8883),
                            client_id=mq.get("ClientId", "p2l-webclient"),
                            username=mq.get("Username"),
                            password=mq.get("Password"),
                            command_topic=mq.get("CommandTopic", "p2l/commands"),
                            event_topic=mq.get("EventTopic", "p2l/events"),
                            devices_topic=mq.get("DevicesTopic", "p2l/devices"),
                            status_topic=mq.get("StatusTopic", "p2l/status"),
                            tls=TlsConfig(
                                enabled=mq_tls.get("Enabled", True),
                                ca_cert_path=mq_tls.get("CaCertPath"),
                                client_cert_path=mq_tls.get("ClientCertPath"),
                                client_key_path=mq_tls.get("ClientKeyPath"),
                                cert_password=mq_tls.get("CertPassword"),
                                allow_untrusted_certs=mq_tls.get("AllowUntrustedCerts", False),
                            ),
                        ),
                        block_no=data.get("Gateway", {}).get("BlockNo", "00"),
                    )
                except Exception:
                    pass
            return cls()

        try:
            data = json.loads(CONFIG_PATH.read_text())
            mq = data.get("mqtt", {})
            mq_tls = mq.get("tls", {})
            return cls(
                mqtt=MqttConfig(
                    enabled=mq.get("enabled", False),
                    host=mq.get("host", "localhost"),
                    port=mq.get("port", 8883),
                    client_id=mq.get("client_id", "p2l-webclient"),
                    username=mq.get("username"),
                    password=mq.get("password"),
                    command_topic=mq.get("command_topic", "p2l/commands"),
                    event_topic=mq.get("event_topic", "p2l/events"),
                    devices_topic=mq.get("devices_topic", "p2l/devices"),
                    status_topic=mq.get("status_topic", "p2l/status"),
                    tls=TlsConfig(
                        enabled=mq_tls.get("enabled", True),
                        ca_cert_path=mq_tls.get("ca_cert_path"),
                        client_cert_path=mq_tls.get("client_cert_path"),
                        client_key_path=mq_tls.get("client_key_path"),
                        cert_password=mq_tls.get("cert_password"),
                        allow_untrusted_certs=mq_tls.get("allow_untrusted_certs", False),
                    ),
                ),
                block_no=data.get("block_no", "00"),
                auto_connect=data.get("auto_connect", False),
            )
        except Exception:
            return cls()

    def to_dict(self) -> dict:
        tls = self.mqtt.tls
        return {
            "mqtt": {
                "enabled": self.mqtt.enabled,
                "host": self.mqtt.host,
                "port": self.mqtt.port,
                "client_id": self.mqtt.client_id,
                "username": self.mqtt.username,
                "password": self.mqtt.password,  # note: excluded in API response
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
            "block_no": self.block_no,
            "auto_connect": self.auto_connect,
        }
