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

    /// <summary>Self-healing / auto-restart settings.</summary>
    public SelfHealSettings SelfHeal { get; init; } = new();
}

public sealed class GatewaySettings
{
    public string Host    { get; init; } = AioiConstants.DefaultGatewayIp;
    public int    Port    { get; init; } = AioiConstants.DefaultTcpPort;
    public string BlockNo { get; init; } = "00";
}

/// <summary>
/// Controls automatic self-healing: crash detection, restart back-off, and
/// watchdog-based component hang detection.
/// </summary>
public sealed class SelfHealSettings
{
    /// <summary>Enable or disable the entire self-heal subsystem.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Maximum number of auto-restarts within RestartWindowMinutes before giving up.</summary>
    public int MaxRestarts { get; init; } = 5;

    /// <summary>Sliding window (minutes) used for crash-loop detection.</summary>
    public int RestartWindowMinutes { get; init; } = 10;

    /// <summary>Seconds to wait before restarting after a crash (back-off).</summary>
    public int BackOffSeconds { get; init; } = 3;

    /// <summary>
    /// Minutes the gateway may stay disconnected before the watchdog triggers a restart.
    /// Set to 0 to disable the gateway watchdog.
    /// </summary>
    public int WatchdogTimeoutMinutes { get; init; } = 5;
}
