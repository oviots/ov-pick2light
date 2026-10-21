namespace P2L.Mqtt.Configuration;

public sealed class MqttSettings
{
    /// <summary>Set to true to enable the MQTT bridge on startup.</summary>
    public bool    Enabled      { get; init; } = false;
    public string  Host         { get; init; } = "localhost";
    public int     Port         { get; init; } = 8883;
    /// <summary>Optional absolute ws/wss MQTT endpoint. When set, overrides Host/Port; the URL scheme selects TLS.</summary>
    public string? WebSocketUrl { get; init; }
    public string  ClientId     { get; init; } = "p2l-bridge";
    public string? Username     { get; init; }
    public string? Password     { get; init; }
    public MqttSettings? BackupBroker { get; init; }
    /// <summary>
    /// Wait for the WMS application to acknowledge each durable business event,
    /// even with one broker. Requires a peer supporting transport probes/event_ack.
    /// Leave false for standalone legacy clients; backup mode always requires ACKs.
    /// </summary>
    public bool RequireApplicationAck { get; init; } = false;
    public int ProbeIntervalMs { get; init; } = 1000;
    public int ProbeTimeoutMs { get; init; } = 3000;
    public int FailbackStableMs { get; init; } = 10000;
    public int CommandQueueCapacity { get; init; } = 64;
    public int CommandExecutionTimeoutMs { get; init; } = 25000;
    public int DiagnosticTimeoutMs { get; init; } = 15000;
    /// <summary>MQTT 5 session retention after disconnect (1..86400 seconds).</summary>
    public uint SessionExpirySeconds { get; init; } = 600;
    /// <summary>Durable command receipts used to reject duplicate execution after restart.</summary>
    public string CommandReceiptPath { get; init; } = "p2l_commands.db";

    /// <summary>Topic to subscribe for incoming light control commands (QoS 1).</summary>
    public string CommandTopic  { get; init; } = "p2l/commands";
    /// <summary>Topic to publish light operation events (confirmations, batch_completed).</summary>
    public string EventTopic    { get; init; } = "p2l/events";
    /// <summary>Topic prefix for per-device state. Each module publishes to {DevicesTopic}/{address} with retain=true.</summary>
    public string DevicesTopic  { get; init; } = "p2l/devices";
    /// <summary>Topic for bridge health status and Last Will Testament.</summary>
    public string StatusTopic   { get; init; } = "p2l/status";

    /// <summary>
    /// Path to the offline event buffer file (JSONL).
    /// Business events are flushed here before MQTT delivery and replayed until
    /// application ACK in WMS/backup mode, or PUBACK in legacy standalone mode.
    /// Relative paths are resolved from the working directory.
    /// Must be non-empty while MQTT is enabled; confirmations require durable storage.
    /// </summary>
    public string OfflineBufferPath { get; init; } = "mqtt_offline_buffer.jsonl";

    /// <summary>
    /// Optional path to the Python web-client device database (p2l_devices.db).
    /// When set, every <c>light_changed</c> event published to <c>p2l/devices/#</c>
    /// is enriched with the <c>label</c> field from this SQLite file.
    /// Leave null or empty to omit the label.
    /// </summary>
    public string? ClientDbPath { get; init; }

    public MqttTlsSettings Tls  { get; init; } = new();
}

public sealed class MqttTlsSettings
{
    public bool    Enabled             { get; init; } = true;

    /// <summary>Path to PEM (.crt) or PFX file for custom CA / server-cert pinning.
    /// Leave null to use the OS trust store.</summary>
    public string? CaCertPath          { get; init; }

    /// <summary>Client certificate for mutual TLS. PFX or PEM bundle (cert + key).</summary>
    public string? ClientCertPath      { get; init; }

    /// <summary>Separate PEM private-key file (only needed when cert and key are split).</summary>
    public string? ClientKeyPath       { get; init; }

    /// <summary>Passphrase for an encrypted PFX or PEM key.</summary>
    public string? CertPassword        { get; init; }

    /// <summary>Disable server certificate validation. DEV/TEST ONLY — never use in production.</summary>
    public bool    AllowUntrustedCerts { get; init; } = false;
}
