using P2L.Shared.Enums;

namespace P2L.Mqtt.Messages;

// ─── Base ──────────────────────────────────────────────────────────────────

public abstract record MqttCommand
{
    public required string  Type          { get; init; }
    public string?          CorrelationId { get; init; }
}

// ─── light_on ──────────────────────────────────────────────────────────────

/// <summary>Light up a single module.</summary>
public sealed record LightOnCmd : MqttCommand
{
    public required string  Address  { get; init; }
    public required int     Quantity { get; init; }
    public LightColor       Color    { get; init; } = LightColor.Green;
    public LightState       LedMode  { get; init; } = LightState.On;
    public string           BlockNo  { get; init; } = "00";
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
    public string                     BlockNo { get; init; } = "00";
    public LightColor                 Color   { get; init; } = LightColor.Green;
    public LightState                 LedMode { get; init; } = LightState.On;
    public required List<BatchItemDto> Items  { get; init; }
}

public sealed record BatchItemDto
{
    public required string  Address  { get; init; }
    public required int     Quantity { get; init; }
    public string?          Label    { get; init; }
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
    public required string File    { get; init; }
    public LightColor      Color   { get; init; } = LightColor.Green;
    public string          BlockNo { get; init; } = "00";
}

// ─── csv_control ───────────────────────────────────────────────────────────

/// <summary>Control a running CSV task: action = pause | resume | cancel</summary>
public sealed record CsvControlCmd : MqttCommand
{
    public required string Action { get; init; }  // "pause" | "resume" | "cancel"
}
