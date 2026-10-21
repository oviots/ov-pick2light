namespace P2L.Shared.Models;

/// <summary>Stable local completion intent, replayed until a durable sink accepts it.</summary>
public sealed class BatchCompletionEvent
{
    public string EventId { get; set; } = "";
    public string BlockNo { get; set; } = "00";
    public string? CorrelationId { get; set; }
    public DateTime CompletedAt { get; set; }
}
