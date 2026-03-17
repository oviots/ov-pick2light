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
    /// <summary>Parsed integer quantity after Fn/+- key adjustment. Null when no adjustment was made.</summary>
    public int?              AdjustedQuantity { get; init; }
    /// <summary>Raw 5-char display string from the t-interrupt packet (e.g. "00002" or "    2").</summary>
    public string?           AdjustedDisplay  { get; init; }
    public string?           CorrelationId    { get; init; }
}

// ─── light_changed ─────────────────────────────────────────────────────────

/// <summary>Published when a module's state changes (lit, extinguished, configured, confirmed).</summary>
public sealed record LightChangedEvt : MqttEvent
{
    public required string  Address           { get; init; }
    public ModuleStatus     ModuleStatus      { get; init; }
    public LightColor       Color             { get; init; }
    public LightState       LedMode           { get; init; }
    public LightState       SegMode           { get; init; }
    public LightState       BuzMode           { get; init; }
    public string?          DisplayData       { get; init; }
    public string?          BlockNo           { get; init; }
    public int?             AdjustedQuantity  { get; init; }
    public DateTime?        LastInstructionAt { get; init; }
    public DateTime?        LastConfirmedAt   { get; init; }
    /// <summary>
    /// Human-readable label for the device, looked up from the client device database.
    /// Empty string when no label is configured.
    /// </summary>
    public string           Label             { get; init; } = "";
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

// ─── task_event ────────────────────────────────────────────────────────────

/// <summary>
/// Published for each CSV pick-task lifecycle step:
///   task_started, task_confirmed, task_error, csv_completed.
/// </summary>
public sealed record TaskEvt : MqttEvent
{
    public string     Address  { get; init; } = "";
    public int        Quantity { get; init; }
    public LightColor Color    { get; init; }
    public string?    Label    { get; init; }
}
