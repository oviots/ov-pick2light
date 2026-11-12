namespace P2L.Shared;

/// <summary>Optional diagnostics sink. Implementations must never throw or wait for network I/O.</summary>
public interface IP2LTelemetry
{
    void Record(string kind, string? topic, string payload);
}
