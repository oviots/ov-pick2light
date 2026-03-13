using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using P2L.Hardware.Protocol;
using P2L.Hardware.Transport;
using P2L.Shared.Constants;
using P2L.Shared.Enums;
using P2L.Shared.Models;

namespace P2L.Hardware;

/// <summary>
/// High-level interface to one Aioi L-PICK gateway controller.
///
/// Responsibilities:
///   • Manages TCP transport lifecycle.
///   • Routes incoming packets to pending command completions (request/response).
///   • Raises <see cref="Confirmed"/> events for "t" interrupt packets.
///   • Provides clean, domain-level methods (Instruct, Terminate, …).
///
/// Thread-safe: all public methods may be called concurrently.
/// </summary>
public sealed class AioiGateway : IAsyncDisposable, IDisposable
{
    private readonly TcpTransport _transport;
    private readonly ILogger      _log;

    // Pending command completions keyed by sequence number
    private readonly ConcurrentDictionary<int, TaskCompletionSource<AioiDecoder.ParsedResponse>>
        _pending = new();

    private int _initialized; // 0 = no, 1 = yes (Interlocked flag)

    /// <summary>True after a successful Z initialization command has been sent.</summary>
    public bool IsInitialized => _initialized == 1;

    /// <summary>Fires each time the controller reports a button-press confirmation.</summary>
    public event Action<ConfirmationEvent>? Confirmed;

    /// <summary>Fires when the gateway connects or disconnects.</summary>
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _transport.IsConnected;

    public AioiGateway(string host, int port, ILogger logger)
    {
        _log       = logger;
        _transport = new TcpTransport(host, port, logger);
        _transport.PacketReceived         += OnPacketReceived;
        _transport.ConnectionStateChanged += connected => ConnectionChanged?.Invoke(connected);
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _transport.StartAsync(ct);
    }

    // ─── Initialisation ───────────────────────────────────────────────────

    /// <summary>
    /// Sends the Z (full initialization) command and waits for the "o" ACK.
    /// Call this once after connecting before sending any other commands.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await SendAndWaitAsync(AioiEncoder.Initialize(), ct);
        Interlocked.Exchange(ref _initialized, 1);
    }

    /// <summary>
    /// Ensures Z initialization has been sent. Safe to call multiple times — only
    /// sends Z once per gateway instance. Call before any M/L/PP1/D commands.
    /// </summary>
    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) == 0)
        {
            _log.LogInformation("Auto-initializing gateway (Z command)");
            await SendAndWaitAsync(AioiEncoder.Initialize(), ct);
        }
    }

    /// <summary>
    /// Sends Z1 (hardware reset) and waits for ACK.
    /// Note: a 1.5 s hardware settling delay is required by the Aioi spec after this call.
    /// </summary>
    public async Task HardwareResetAsync(CancellationToken ct = default)
    {
        await SendAndWaitAsync(AioiEncoder.HardwareReset(), ct);
        // Aioi spec: wait ≥1.5 s after Z1 before next command
        await Task.Delay(1_500, ct);
    }

    // ─── Mode configuration (reserved for future use) ─────────────────────

    /// <summary>
    /// Sends the global M (mode array) command. Sets the same color for every module
    /// that does not have a per-address Am1 override. Useful for diagnostics or
    /// single-color batches; normal operation uses Am1 per address inside
    /// InstructBatchAsync / OverwriteBatchAsync.
    /// </summary>
    public Task SetModeForColorAsync(LightColor color,
                                     LightState  ledMode = LightState.On,
                                     CancellationToken ct = default)
        => SendAndWaitAsync(AioiEncoder.SetModeForColor(color, ledMode), ct);

    // ─── Pick operations ──────────────────────────────────────────────────

    /// <summary>
    /// Lights up a batch of modules using P1 (steady) or FP1 (flash).
    /// Sends Am1 per address before P1 so each module uses the correct color.
    /// </summary>
    public async Task InstructBatchAsync(IReadOnlyList<PickingInstruction> instructions,
                                         CancellationToken ct = default)
    {
        if (instructions.Count == 0) return;

        // Am1 sets per-address color AND mode (On/Flash/HighFlash nibbles).
        // Am1's state nibbles are the authoritative "display status during instruction" —
        // they control whether the LED is steady or flashing. P1 just triggers the instruction.
        foreach (var inst in instructions)
        {
            var (led2, led1, led0) = inst.Color.ToLedStates(inst.LedMode);
            await SendAndWaitAsync(
                AioiEncoder.SetModeAm1(inst.Address, led2, led1, led0, LightState.On, inst.BuzMode), ct);
        }

        var ledMode = instructions[0].LedMode;
        var blockNo = instructions[0].BlockNo;
        await SendAndWaitAsync(AioiEncoder.BlockInstruct(blockNo, instructions, ledMode), ct);

        _log.LogInformation("Instructed {Count} modules (block {Block} mode {Mode})",
            instructions.Count, blockNo, ledMode);
    }

    /// <summary>
    /// Overwrites an in-progress batch using P5 (steady) or FP5 (flash).
    /// Sends Am1 per address before P5 so color is applied correctly.
    /// </summary>
    public async Task OverwriteBatchAsync(IReadOnlyList<PickingInstruction> instructions,
                                          CancellationToken ct = default)
    {
        if (instructions.Count == 0) return;

        foreach (var inst in instructions)
        {
            var (led2, led1, led0) = inst.Color.ToLedStates(inst.LedMode);
            await SendAndWaitAsync(
                AioiEncoder.SetModeAm1(inst.Address, led2, led1, led0, LightState.On, inst.BuzMode), ct);
        }

        var ledMode = instructions[0].LedMode;
        var blockNo = instructions[0].BlockNo;
        await SendAndWaitAsync(AioiEncoder.BlockInstructOverwrite(blockNo, instructions, ledMode), ct);
    }

    /// <summary>
    /// Lights a single module using M (mode/color) + P1 (block instruct with one item).
    /// WARNING: P1 replaces the entire block — all other modules in the same block turn off.
    /// </summary>
    public Task InstructSingleAsync(PickingInstruction instruction,
                                    CancellationToken ct = default)
        => InstructBatchAsync([instruction], ct);


    // ─── Termination ──────────────────────────────────────────────────────

    /// <summary>Turns off a single light module.</summary>
    public Task TerminateAsync(string address, CancellationToken ct = default)
        => SendAndWaitAsync(AioiEncoder.Terminate(address), ct);

    /// <summary>Turns off all modules in a block ("00" = all).</summary>
    public Task TerminateBlockAsync(string blockNo = "00", CancellationToken ct = default)
        => SendAndWaitAsync(AioiEncoder.TerminateBlock(blockNo), ct);

    // ─── Signal light ─────────────────────────────────────────────────────

    public Task SetSignalLightAsync(string address, bool on, CancellationToken ct = default)
        => SendAndWaitAsync(AioiEncoder.SignalLight(address, on), ct);

    // ─── Maintenance ──────────────────────────────────────────────────────

    public Task<AioiDecoder.ParsedResponse> QueryModuleStatusAsync(string address,
                                                                    CancellationToken ct = default)
        => SendAndWaitAsync(AioiEncoder.ModuleStatusInquiry(address), ct);

    /// <summary>
    /// Reconfigures an active module's display in-place using an L command.
    /// Note: M (mode array) command is skipped — gateway uses built-in default mode.
    /// </summary>
    public async Task ConfigureModuleAsync(string address,
                                           string displayData,
                                           ModuleParameters parameters,
                                           CancellationToken ct = default)
    {
        await SendAndWaitAsync(AioiEncoder.SingleInstruct(address, displayData), ct);

        _log.LogInformation("[CONFIG] addr={Addr} color={C} mode={M}",
            address, parameters.Color, parameters.LedMode);
    }

    // ─── Internal ─────────────────────────────────────────────────────────

    private async Task<AioiDecoder.ParsedResponse> SendAndWaitAsync(string command,
                                                                      CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<AioiDecoder.ParsedResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Allocate seq and register the completion handler BEFORE enqueuing the command.
        // If registration happens after SendAsync returns, a fast gateway ACK can arrive
        // in the receive loop before _pending[seq] is set → "Unmatched response" → 5 s timeout.
        int seq = _transport.AllocateSeqNo();
        _pending[seq] = tcs;
        await _transport.EnqueueAsync(command, seq, ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AioiConstants.EthernetResponseTimeoutMs);

        try
        {
            var response = await tcs.Task.WaitAsync(timeout.Token);

            if (response.Kind == AioiDecoder.ResponseKind.Nack)
            {
                _log.LogError("[NACK] Gateway rejected command: {Cmd}", command);
                throw new AioiNackException(command);
            }

            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _pending.TryRemove(seq, out _);
            _log.LogError("Timeout waiting for ACK on command: {Cmd}", command);
            throw new TimeoutException($"Gateway did not respond to '{command}' within {AioiConstants.EthernetResponseTimeoutMs} ms");
        }
        finally
        {
            _pending.TryRemove(seq, out _);
        }
    }

    private void OnPacketReceived(AioiPacket packet)
    {
        var response = AioiDecoder.Parse(packet.Command, packet.SequenceNo);

        switch (response.Kind)
        {
            case AioiDecoder.ResponseKind.Interrupt:
                // "t" packet: ACK must echo the same SeqNo as the interrupt (always 0)
                // and must be sent immediately — bypass the send queue so it never waits
                // behind a queued heartbeat or P1 command.
                _ = _transport.SendImmediateAsync(AioiEncoder.Ack(), packet.SequenceNo);

                if (response.Event is not null)
                {
                    _log.LogInformation("[CONFIRM] Address={Addr} Status={Status}",
                        response.Event.Address, response.Event.Status);
                    Confirmed?.Invoke(response.Event);
                }
                break;

            case AioiDecoder.ResponseKind.Ack:
            case AioiDecoder.ResponseKind.Nack:
            case AioiDecoder.ResponseKind.DuplicateSeq:
            case AioiDecoder.ResponseKind.ModuleStatus:
            case AioiDecoder.ResponseKind.Unknown:
                // Route to the pending command completion (matched by SeqNo)
                if (_pending.TryRemove(packet.SequenceNo, out var tcs))
                    tcs.TrySetResult(response);
                else
                    _log.LogDebug("Unmatched response SeqNo={Seq} Kind={Kind}",
                        packet.SequenceNo, response.Kind);
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
    }

    /// <summary>
    /// Synchronous disposal for DI containers that call <see cref="IDisposable.Dispose"/>.
    /// Fire-and-forget: the OS will reclaim the TCP socket at process exit.
    /// </summary>
    public void Dispose() => _ = DisposeAsync().AsTask();
}
