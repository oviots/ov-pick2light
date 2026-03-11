using P2L.Shared.Enums;

namespace P2L.Shared.Models;

/// <summary>
/// Fired when the controller sends a "t" interrupt packet,
/// indicating an operator pressed the CONFIRM (or +/-) button on a light module.
/// </summary>
public sealed class ConfirmationEvent
{
    /// <summary>4-digit BCD address of the confirming module.</summary>
    public required string Address { get; init; }

    /// <summary>Completion status: normal (00), abnormal (01), shortage (10).</summary>
    public CompletionStatus Status { get; init; }

    /// <summary>Adjusted display value when +/- key was used (shortage mode).</summary>
    public string? AdjustedDisplay { get; init; }

    /// <summary>UTC timestamp the event was received by the PC.</summary>
    public DateTime ReceivedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Raw sequence number from the interrupt packet (for ACK pairing).</summary>
    public int SequenceNo { get; init; }
}
