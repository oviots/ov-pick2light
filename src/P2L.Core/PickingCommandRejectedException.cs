namespace P2L.Core;

/// <summary>A command rejected before hardware execution.</summary>
public sealed class PickingCommandRejectedException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
