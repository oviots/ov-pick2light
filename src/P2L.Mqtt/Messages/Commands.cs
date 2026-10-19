using P2L.Shared.Enums;

namespace P2L.Mqtt.Messages;

// ─── Base ──────────────────────────────────────────────────────────────────

public abstract record MqttCommand
{
    public required string  Type          { get; init; }
    public string?          CorrelationId { get; init; }
    public string?          CommandId     { get; init; }
    public DateTimeOffset?  ExpiresAt     { get; init; }
}

// ─── light_on ──────────────────────────────────────────────────────────────

/// <summary>Light up a single module.</summary>
public sealed record LightOnCmd : MqttCommand
{
    public string?          CommandItemId { get; init; }
    public string?          PickingDetailId { get; init; }
    public required string  Address  { get; init; }
    public required int     Quantity { get; init; }
    public string           BlockNo  { get; init; } = "00";
    public LightColor?      Color    { get; init; }
    public LightState?      LedMode  { get; init; }
    /// <summary>Buzzer mode. Null = mirrors LedMode (beeps when flashing).</summary>
    public LightState?      BuzMode  { get; init; }
    public string?          Label    { get; init; }
}

// ─── light_off ─────────────────────────────────────────────────────────────

/// <summary>Extinguish a single module.</summary>
public sealed record LightOffCmd : MqttCommand
{
    public required string Address { get; init; }
}

// ─── batch_start ───────────────────────────────────────────────────────────

/// <summary>Start a picking batch across multiple modules.</summary>
public sealed record BatchStartCmd : MqttCommand
{
    public bool                       Relight { get; init; }
    public string                     BlockNo { get; init; } = "00";
    public LightColor?                Color   { get; init; }
    public LightState?                LedMode { get; init; }
    /// <summary>Buzzer mode. Null = mirrors LedMode (beeps when flashing).</summary>
    public LightState?                BuzMode { get; init; }
    public required List<BatchItemDto> Items  { get; init; }
}

public sealed record BatchItemDto
{
    public string?     CommandItemId { get; init; }
    public string?     PickingDetailId { get; init; }
    /// <summary>
    /// 4-digit hardware address of the target module.
    /// If omitted, <see cref="Label"/> is used to resolve the address from the
    /// client device database (<c>ClientDbPath</c>). The entire command is rejected
    /// if any item cannot be resolved.
    /// </summary>
    public string?     Address  { get; init; }
    public required int Quantity { get; init; }
    public LightColor?  Color    { get; init; }   // null → use batch-level color
    public string?      Label    { get; init; }
    /// <summary>Per-item LED mode. Null = fall back to batch-level LedMode.</summary>
    public LightState?  LedMode  { get; init; }
    /// <summary>Per-item buzzer mode. Null = fall back to batch-level BuzMode (or mirrors LedMode).</summary>
    public LightState?  BuzMode  { get; init; }
}

// ─── batch_finish ──────────────────────────────────────────────────────────

/// <summary>Terminate a block (turns off all lights in the block).</summary>
public sealed record BatchFinishCmd : MqttCommand
{
    public string BlockNo { get; init; } = "00";
}

// ─── terminate_all ─────────────────────────────────────────────────────────

/// <summary>Emergency: terminate all modules immediately.</summary>
public sealed record TerminateAllCmd : MqttCommand { }

/// <summary>Triggers a gateway scan to rediscover all connected light modules.</summary>
public sealed record ScanDevicesCmd : MqttCommand
{
    /// <summary>First address to probe (1–9999). Defaults to 1.</summary>
    public int From { get; init; } = 1;
    /// <summary>Last address to probe inclusive (1–9999). Defaults to 99.</summary>
    public int To   { get; init; } = 99;
}

// ─── get_snapshot ───────────────────────────────────────────────────────────

/// <summary>
/// Request a full state snapshot of all known modules.
/// P2L responds by publishing one <c>light_changed</c> event per tracked module.
/// </summary>
public sealed record GetSnapshotCmd : MqttCommand { }

// ─── csv_run ───────────────────────────────────────────────────────────────

/// <summary>Start processing a CSV pick-task file sequentially.</summary>
public sealed record CsvRunCmd : MqttCommand
{
    public required string File  { get; init; }
    public LightColor      Color { get; init; } = LightColor.Green;
}

// ─── csv_control ───────────────────────────────────────────────────────────

/// <summary>Control a running CSV task: action = pause | resume | cancel</summary>
public sealed record CsvControlCmd : MqttCommand
{
    public required string Action { get; init; }  // "pause" | "resume" | "cancel"
}
