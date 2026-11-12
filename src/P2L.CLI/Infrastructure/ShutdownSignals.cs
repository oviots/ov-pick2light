using System.Runtime.InteropServices;

namespace P2L.CLI.Infrastructure;

/// <summary>Routes interactive and service stop signals through the same shutdown path.</summary>
internal sealed class ShutdownSignals : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly PosixSignalRegistration? _termination;

    public ShutdownSignals(CancellationTokenSource cts)
    {
        _cts = cts;
        Console.CancelKeyPress += OnCancel;
        if (!OperatingSystem.IsWindows())
            _termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                RequestStop();
            });
    }

    private void OnCancel(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        RequestStop();
    }

    private void RequestStop()
    {
        SelfHealHandler.BeginShutdown();
        _cts.Cancel();
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancel;
        _termination?.Dispose();
    }
}
