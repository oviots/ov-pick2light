using P2L.Mqtt.Configuration;
using P2L.Shared.Constants;

namespace P2L.CLI.Configuration;

/// <summary>
/// Application configuration, populated from appsettings.json and/or environment variables.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Gateway network settings.</summary>
    public GatewaySettings Gateway { get; init; } = new();

    /// <summary>Rotating log file directory.</summary>
    public string LogDirectory { get; init; } = "logs";

    /// <summary>Maximum size (bytes) before rotating the TX/RX audit log.</summary>
    public long MaxLogFileSizeBytes { get; init; } = 10 * 1024 * 1024; // 10 MB

    /// <summary>MQTT bridge settings. Set Enabled=true to activate.</summary>
    public MqttSettings Mqtt { get; init; } = new();
}

public sealed class GatewaySettings
{
    public string Host    { get; init; } = AioiConstants.DefaultGatewayIp;
    public int    Port    { get; init; } = AioiConstants.DefaultTcpPort;
    public string BlockNo { get; init; } = "00";
}
