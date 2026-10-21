using P2L.Shared.Enums;

namespace P2L.Shared.Models;

/// <summary>An observation requiring an exact-owner decision, never proof of picking.</summary>
public sealed class ConfirmationMissingEvent
{
    public string EventId { get; set; } = "";
    public string Address { get; set; } = "";
    public string BlockNo { get; set; } = "00";
    public string? CorrelationId { get; set; }
    public string? CommandItemId { get; set; }
    public string? PickingDetailId { get; set; }
    public ModuleStatus ObservedStatus { get; set; }
    public DateTime DetectedAt { get; set; }
    public DateTime LastInstructionAt { get; set; }
}
