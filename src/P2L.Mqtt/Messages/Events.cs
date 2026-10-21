using P2L.Shared.Enums;

namespace P2L.Mqtt.Messages;

// ─── Base ──────────────────────────────────────────────────────────────────

public abstract record MqttEvent
{
    public required string  Type      { get; init; }
    public DateTime         Timestamp { get; init; } = DateTime.UtcNow;
    public string?          EventId   { get; init; }
}

// ─── confirmed ─────────────────────────────────────────────────────────────

/// <summary>Published when an operator presses CONFIRM on a module.</summary>
public sealed record ConfirmedEvt : MqttEvent
{
    public string?           Source           { get; init; }
    public string?           CommandItemId    { get; init; }
    public string?           PickingDetailId  { get; init; }
    public required string   Address          { get; init; }
    public CompletionStatus  Status           { get; init; }
    /// <summary>Parsed integer quantity after Fn/+- key adjustment. Null when no adjustment was made.</summary>
    public int?              AdjustedQuantity { get; init; }
    /// <summary>Raw 5-char display string from the t-interrupt packet (e.g. "00002" or "    2").</summary>
    public string?           AdjustedDisplay  { get; init; }
    public string?           CorrelationId    { get; init; }
    /// <summary>Current display value on the module at the time of confirmation.</summary>
    public string?           DisplayData      { get; init; }
    /// <summary>Human-readable label for the device, resolved from the client device database.</summary>
    public string?           Label            { get; init; }
}

// ─── light_changed ─────────────────────────────────────────────────────────

/// <summary>Published when a module's state changes (lit, extinguished, configured, confirmed).</summary>
public sealed record LightChangedEvt : MqttEvent
{
    public required string  Address           { get; init; }
    public string?          CorrelationId     { get; init; }
    public string?          CommandItemId     { get; init; }
    public string?          PickingDetailId   { get; init; }
    public ModuleStatus?    ObservedStatus    { get; init; }
    public DateTime?        ConfirmationMissingAt { get; init; }
    public string?          ConfirmationMissingEventId { get; init; }
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

/// <summary>Diagnostic requiring explicit resolution; never a picked-item event.</summary>
public sealed record ConfirmationMissingEvt : MqttEvent
{
    public required string Address { get; init; }
    public required string BlockNo { get; init; }
    public string? CorrelationId { get; init; }
    public string? CommandItemId { get; init; }
    public string? PickingDetailId { get; init; }
    public ModuleStatus ObservedStatus { get; init; }
    public DateTime? LastInstructionAt { get; init; }
}

// ─── batch_completed ───────────────────────────────────────────────────────

/// <summary>Published when every module in the active batch is confirmed.</summary>
public sealed record BatchCompletedEvt : MqttEvent
{
    public string? CorrelationId { get; init; }
}

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

/// <summary>Application execution outcome, independent of broker PUBACK.</summary>
public sealed record CommandResultEvt : MqttEvent
{
    public string? CommandId { get; init; }
    public string? CorrelationId { get; init; }
    public required string Status { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
}
