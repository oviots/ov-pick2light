namespace P2L.Mqtt.Configuration;

public sealed class MqttSettings
{
    /// <summary>Set to true to enable the MQTT bridge on startup.</summary>
    public bool    Enabled      { get; init; } = false;
    public string  Host         { get; init; } = "localhost";
    public int     Port         { get; init; } = 8883;
    public string  ClientId     { get; init; } = "p2l-bridge";
    public string? Username     { get; init; }
    public string? Password     { get; init; }

    /// <summary>Topic to subscribe for incoming light control commands (QoS 1).</summary>
    public string CommandTopic  { get; init; } = "p2l/commands";
    /// <summary>Topic to publish light operation events (confirmations, batch_completed).</summary>
    public string EventTopic    { get; init; } = "p2l/events";
    /// <summary>Topic prefix for per-device state. Each module publishes to {DevicesTopic}/{address} with retain=true.</summary>
    public string DevicesTopic  { get; init; } = "p2l/devices";
    /// <summary>Topic for bridge health status and Last Will Testament.</summary>
    public string StatusTopic   { get; init; } = "p2l/status";

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
