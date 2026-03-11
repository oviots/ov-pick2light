using P2L.Shared.Enums;

namespace P2L.Mqtt.Messages;

// ─── Base ──────────────────────────────────────────────────────────────────

public abstract record MqttEvent
{
    public required string  Type      { get; init; }
    public DateTime         Timestamp { get; init; } = DateTime.UtcNow;
}

// ─── confirmed ─────────────────────────────────────────────────────────────

/// <summary>Published when an operator presses CONFIRM on a module.</summary>
public sealed record ConfirmedEvt : MqttEvent
{
    public required string   Address          { get; init; }
    public CompletionStatus  Status           { get; init; }
    public string?           AdjustedDisplay  { get; init; }
    public string?           CorrelationId    { get; init; }
}

// ─── light_changed ─────────────────────────────────────────────────────────

/// <summary>Published when a module's state changes (lit, extinguished, configured).</summary>
public sealed record LightChangedEvt : MqttEvent
{
    public required string  Address      { get; init; }
    public ModuleStatus     ModuleStatus { get; init; }
    public LightColor       Color        { get; init; }
    public LightState       LedMode      { get; init; }
    public string?          DisplayData  { get; init; }
    public string?          BlockNo      { get; init; }
}

// ─── batch_completed ───────────────────────────────────────────────────────

/// <summary>Published when every module in the active batch is confirmed.</summary>
public sealed record BatchCompletedEvt : MqttEvent { }

// ─── bridge_status ─────────────────────────────────────────────────────────

/// <summary>
/// Health-check message. Also used as Last Will Testament (Online=false),
/// published automatically by the broker on ungraceful disconnect.
/// </summary>
public sealed record BridgeStatusEvt : MqttEvent
{
    public bool Online           { get; init; }
    public bool GatewayConnected { get; init; }
}
