using P2L.Shared.Enums;

namespace P2L.Shared.Models;

/// <summary>
/// Tracks the live state of a single Aioi light module (address 0001–7999).
/// Thread-safe by design; store in a <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public sealed class LightModuleState
{
    /// <summary>4-digit BCD address string, e.g. "0001".</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>Current operational status reported by the controller.</summary>
    public ModuleStatus Status { get; set; } = ModuleStatus.Standby;

    /// <summary>Last maintenance inquiry result, separate from business confirmation eligibility.</summary>
    public ModuleStatus? ObservedStatus { get; set; }

    /// <summary>Active instruction observed without a corresponding hardware confirmation.</summary>
    public DateTime? ConfirmationMissingAt { get; set; }
    public string? ConfirmationMissingEventId { get; set; }

    /// <summary>Active display value (up to 5 chars, right-aligned).</summary>
    public string DisplayData { get; set; } = "     ";

    /// <summary>Currently lit color.</summary>
    public LightColor Color { get; set; } = LightColor.Off;

    /// <summary>Active LED display mode (steady, flash, etc.).</summary>
    public LightState LedMode { get; set; } = LightState.Off;

    /// <summary>Active 7-segment display mode.</summary>
    public LightState SegMode { get; set; } = LightState.On;

    /// <summary>Active buzzer mode.</summary>
    public LightState BuzMode { get; set; } = LightState.Off;

    public LightState? AfterConfirm { get; set; }
    public LightState? BuzAfterConfirm { get; set; }

    /// <summary>UTC time of last instruction sent to this module.</summary>
    public DateTime? LastInstructionAt { get; set; }

    /// <summary>UTC time the last CONFIRM button press was received.</summary>
    public DateTime? LastConfirmedAt { get; set; }

    /// <summary>Quantity after +/- key adjustment (shortage mode).</summary>
    public int? AdjustedQuantity { get; set; }

    /// <summary>Block number this module belongs to ("00"–"99").</summary>
    public string BlockNo { get; set; } = "00";

    /// <summary>
    /// Caller-supplied trace ID for the currently active pick/batch session.
    /// </summary>
    public string? CorrelationId { get; set; }

    public string? CommandItemId { get; set; }
    public string? PickingDetailId { get; set; }

    public override string ToString() =>
        $"[{Address}] {Status} | {Color}/{LedMode} | \"{DisplayData}\"";
}
