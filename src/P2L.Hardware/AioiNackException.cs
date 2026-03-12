namespace P2L.Hardware;

/// <summary>
/// Thrown when the gateway returns an "n" (NACK) response to a command.
/// Derives from <see cref="InvalidOperationException"/> so existing catch-all
/// handlers are unaffected, but callers can catch this type specifically to
/// implement retry logic (e.g. fall back from P1 to P5).
/// </summary>
public sealed class AioiNackException : InvalidOperationException
{
    public string Command { get; }

    public AioiNackException(string command)
        : base($"Gateway rejected command '{command}' with 'n' — check command format/address.")
    {
        Command = command;
    }
}
