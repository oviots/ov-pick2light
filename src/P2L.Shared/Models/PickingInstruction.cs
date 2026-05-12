using P2L.Shared.Enums;

namespace P2L.Shared.Models;

/// <summary>
/// Represents a single pick task to be sent to a light module.
/// </summary>
public sealed class PickingInstruction
{
    /// <summary>4-digit BCD address of the target light module.</summary>
    public required string Address { get; init; }

    /// <summary>Quantity to display (will be formatted as 5-char right-aligned string).</summary>
    public required int Quantity { get; init; }

    /// <summary>LED color to illuminate during picking.</summary>
    public LightColor Color { get; init; } = LightColor.Green;

    /// <summary>LED active mode (steady on, flash, etc.).</summary>
    public LightState LedMode { get; init; } = LightState.On;

    /// <summary>Segment (7-segment display) mode.</summary>
    public LightState SegMode { get; init; } = LightState.On;

    /// <summary>Buzzer mode.</summary>
    public LightState BuzMode { get; init; } = LightState.Off;

    /// <summary>Block number for grouped control ("00" = global).</summary>
    public string BlockNo { get; init; } = "00";

    /// <summary>Optional SKU or location label for dashboard display.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// Optional caller-supplied trace ID carried through state and MQTT events.
    /// Reuse the same value for every command in one pick/batch session.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>Formats the quantity as the 5-character display data string.</summary>
    public string FormattedDisplay =>
        Quantity.ToString().PadLeft(AioiConstants.DisplayDataLength, '0');

    private static class AioiConstants
    {
        public const int DisplayDataLength = 5;
    }
}
