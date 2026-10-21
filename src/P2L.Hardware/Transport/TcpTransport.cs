using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using P2L.Hardware.Protocol;
using P2L.Shared.Constants;

namespace P2L.Hardware.Transport;

/// <summary>
/// Manages a persistent TCP connection to one Aioi gateway controller.
///
/// Design principles:
///   • Auto-reconnects on disconnection (with configurable back-off).
///   • Commands are queued through a <see cref="Channel{T}"/> so the caller
///     never blocks and the hardware is never flooded.
///   • Incoming bytes are buffered and parsed incrementally; complete packets
///     are surfaced via the <see cref="PacketReceived"/> event.
///   • Sequence numbers are managed here (0–999 wrap-around).
/// </summary>
public sealed class TcpTransport : IAsyncDisposable
{
    private readonly string  _host;
    private readonly int     _port;
    private readonly ILogger _log;

    private TcpClient?    _client;
    private NetworkStream? _stream;
    private int           _seqNo;
    private long          _connectionGeneration;
    private readonly object _connectionLock = new();

    // Protects direct stream writes (used by SendImmediateAsync) from racing with SendLoopAsync
    private readonly SemaphoreSlim _streamLock = new(1, 1);

    // Channel caps at 512 pending commands to prevent runaway memory use
    private sealed record QueuedPacket(AioiPacket Packet, CancellationToken CancellationToken)
    {
        public Func<Func<string?, ValueTask>, ValueTask>? WriteCurrent { get; init; }
        public Action? Skipped { get; init; }
        public Action<Exception>? Failed { get; init; }
        public string? WrittenCommand { get; set; }
    }
    private readonly Channel<QueuedPacket> _sendQueue =
        Channel.CreateBounded<QueuedPacket>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            FullMode     = BoundedChannelFullMode.Wait,
        });

    private readonly CancellationTokenSource _cts = new();
    private Task? _sendLoop;
    private Task? _recvLoop;
    private Exception? _backgroundFailure;
    private int _started;
    public Exception? BackgroundFailure => Volatile.Read(ref _backgroundFailure);

    private int               _reconnectAttempts;
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);

    /// <summary>Fires when a complete packet arrives from the controller.</summary>
    public event Action<AioiPacket>? PacketReceived;

    /// <summary>Fires when the connection state changes.</summary>
    public event Action<bool>? ConnectionStateChanged;

    public bool IsConnected => _stream is not null && (_client?.Connected ?? false);

    /// <summary>Reconnects only the link; never resets modules or replays sent commands.</summary>
    public void ForceReconnect() => DropConnection();

    public TcpTransport(string host, int port, ILogger logger)
    {
        _host = host;
        _port = port;
        _log  = logger;
    }

    // ─── Public API ────────────────────────────────────────────────────────

    /// <summary>Starts background connect/send/receive loops.</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return Task.CompletedTask;
        _sendLoop = Task.Run(() => SuperviseAsync(SendLoopAsync, _cts.Token), _cts.Token);
        _recvLoop = Task.Run(() => SuperviseAsync(ReceiveLoopAsync, _cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    private async Task SuperviseAsync(Func<CancellationToken, Task> run, CancellationToken ct)
    {
        try
        {
            await run(ct);
            if (!ct.IsCancellationRequested)
                throw new IOException("TCP background worker ended unexpectedly.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _backgroundFailure, ex, null);
            _cts.Cancel();
            _sendQueue.Writer.TryComplete(ex);
            try { DropConnection(); } catch { }
            try { _log.LogCritical(ex, "TCP background worker stopped; restart is required"); }
            catch { try { Console.Error.WriteLine("P2L TCP worker stopped; restart is required."); } catch { } }
        }
    }

    /// <summary>
    /// Atomically allocates the next sequence number without enqueuing anything.
    /// Use this when a completion handler must be registered <em>before</em> the
    /// command hits the wire (avoids the ACK-before-register race in request/response flows).
    /// </summary>
    public int AllocateSeqNo() => NextSeqNo();

    /// <summary>
    /// Enqueues a command using a pre-allocated sequence number.
    /// Pair with <see cref="AllocateSeqNo"/> when ordering matters.
    /// </summary>
    public async ValueTask EnqueueAsync(string command, int seq, CancellationToken ct = default)
    {
        var packet = new AioiPacket { SequenceNo = seq, Command = command };
        _ = packet.ToBytes(); // Reject invalid frames before they enter the background loop.
        await _sendQueue.Writer.WriteAsync(new QueuedPacket(packet, ct), ct);
    }

    internal async ValueTask EnqueueCurrentAsync(string command, int seq,
        Func<Func<string?, ValueTask>, ValueTask> writeCurrent, Action skipped,
        Action<Exception> failed, CancellationToken ct)
    {
        var packet = new AioiPacket { SequenceNo = seq, Command = command };
        _ = packet.ToBytes();
        await _sendQueue.Writer.WriteAsync(new QueuedPacket(packet, ct)
        {
            WriteCurrent = writeCurrent,
            Skipped = skipped,
            Failed = failed
        }, ct);
    }

    // The caller's state fence covers payload selection AND write initiation only.
    // Neither the stream write await nor the controller ACK holds that fence.
    private async ValueTask WriteQueuedAsync(QueuedPacket queued, NetworkStream stream, CancellationToken ct)
    {
        ValueTask BeginWrite(string? command)
        {
            ct.ThrowIfCancellationRequested();
            if (command is null)
            {
                queued.Skipped?.Invoke();
                return ValueTask.CompletedTask;
            }
            var packet = new AioiPacket { SequenceNo = queued.Packet.SequenceNo, Command = command };
            queued.WrittenCommand = command;
            return stream.WriteAsync(packet.ToBytes(), ct);
        }
        if (queued.WriteCurrent is { } current)
            await current(BeginWrite);
        else
            await BeginWrite(queued.Packet.Command);
    }

    /// <summary>
    /// Convenience: allocates a sequence number, enqueues the command, and returns the seq.
    /// Use for fire-and-forget sends where no completion handler is needed.
    /// </summary>
    public async ValueTask<int> SendAsync(string command, CancellationToken ct = default)
    {
        int seq = AllocateSeqNo();
        await EnqueueAsync(command, seq, ct);
        return seq;
    }

    /// <summary>
    /// Writes a command directly to the TCP stream, bypassing the send queue.
    /// Use ONLY for highest-priority responses ("O" ACK) that must be sent immediately.
    /// Acquires <see cref="_streamLock"/> to avoid racing with the send loop.
    /// </summary>
    public async ValueTask SendImmediateAsync(string command, int seq, CancellationToken ct = default)
    {
        var bytes = new AioiPacket { SequenceNo = seq, Command = command }.ToBytes();
        await _streamLock.WaitAsync(ct);
        var stream = _stream;
        try
        {
            if (stream is null) return;
            await stream.WriteAsync(bytes, ct);
            _log.LogDebug("[TX-IMM] SeqNo={Seq} Cmd={Cmd}", seq, command);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Immediate send failed");
            DropConnection(stream);
        }
        finally
        {
            _streamLock.Release();
        }
    }

    // ─── Send loop ─────────────────────────────────────────────────────────

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var queued in _sendQueue.Reader.ReadAllAsync(ct))
            {
                if (queued.CancellationToken.IsCancellationRequested) continue;
                using var commandCts = CancellationTokenSource.CreateLinkedTokenSource(ct, queued.CancellationToken);
                bool lockTaken = false;
                NetworkStream? stream = null;
                try
                {
                    await EnsureConnectedAsync(commandCts.Token);
                    await _streamLock.WaitAsync(commandCts.Token);
                    lockTaken = true;
                    commandCts.Token.ThrowIfCancellationRequested();
                    stream = _stream;
                    if (stream is null) throw new IOException("Gateway disconnected before command write.");
                    await WriteQueuedAsync(queued, stream, commandCts.Token);
                    _log.LogDebug("[TX] SeqNo={Seq} Cmd={Cmd}", queued.Packet.SequenceNo, queued.WrittenCommand ?? "(completed batch skipped)");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (OperationCanceledException) when (queued.CancellationToken.IsCancellationRequested)
                {
                    // If a write was already attempted its outcome is uncertain; never replay it.
                    if (stream is not null) DropConnection(stream);
                }
                catch (Exception ex)
                {
                    queued.Failed?.Invoke(ex);
                    _log.LogWarning(ex, "Send failed; will reconnect");
                    if (stream is not null) DropConnection(stream);
                }
                finally
                {
                    if (lockTaken) _streamLock.Release();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    // ─── Receive loop ──────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer  = new byte[4096];
        var pending = new List<byte>();
        long generation = -1;

        while (!ct.IsCancellationRequested)
        {
            NetworkStream? stream = null;
            try
            {
                await EnsureConnectedAsync(ct);
                stream = _stream;
                if (stream is null) continue;
                var currentGeneration = Volatile.Read(ref _connectionGeneration);
                if (generation != currentGeneration)
                {
                    pending.Clear();
                    generation = currentGeneration;
                }
                int read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    // Graceful disconnect
                    DropConnection(stream);
                    pending.Clear();
                    continue;
                }
                if (!ReferenceEquals(stream, _stream)) { pending.Clear(); continue; }

                pending.AddRange(buffer[..read]);

                // Parse all complete packets from the pending buffer
                while (true)
                {
                    var span     = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending));
                    var pkt = AioiPacket.TryParse(span, out int consumed);
                    if (consumed > 0) pending.RemoveRange(0, consumed);
                    if (pkt is null) break;

                    _log.LogDebug("[RX] SeqNo={Seq} Cmd={Cmd}", pkt.SequenceNo, pkt.Command);
                    PacketReceived?.Invoke(pkt);
                }
                if (pending.Count > AioiConstants.MaxCommandLength + 9)
                    throw new InvalidDataException("Gateway receive buffer exceeded the maximum frame length.");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Log message only — full stack trace is noise for routine TCP drops
                _log.LogWarning("Receive error; will reconnect: {Err}", ex.Message);
                if (stream is not null) DropConnection(stream);
                pending.Clear();
            }
        }
    }

    // ─── Connection management ─────────────────────────────────────────────

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (IsConnected) return;

        // Serialize reconnect attempts: whichever loop reaches here first does the work;
        // the other waits and exits immediately once connected.
        await _reconnectLock.WaitAsync(ct);
        try
        {
            while (!IsConnected && !ct.IsCancellationRequested)
            {
                try
                {
                    var client = new TcpClient { NoDelay = true };
                    try { await client.ConnectAsync(_host, _port, ct); }
                    catch { client.Dispose(); throw; }
                    lock (_connectionLock)
                    {
                        if (ct.IsCancellationRequested) { client.Dispose(); ct.ThrowIfCancellationRequested(); }
                        _client = client;
                        _stream = client.GetStream();
                        Interlocked.Increment(ref _connectionGeneration);
                    }
                    _log.LogInformation("Connected to gateway {Host}:{Port}", _host, _port);
                    _reconnectAttempts = 0;
                    ConnectionStateChanged?.Invoke(true);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    int attempt = _reconnectAttempts++;
                    int delayMs = (int)Math.Min(
                        AioiConstants.ReconnectDelayMs * Math.Pow(2, attempt), 30_000);

                    if (attempt == 0)
                        _log.LogWarning(
                            "Cannot connect to {Host}:{Port} — retrying with exponential backoff (max 30 s)",
                            _host, _port);
                    else
                        _log.LogDebug(
                            "Cannot connect to {Host}:{Port} — attempt {Attempt}, next retry in {Ms} ms",
                            _host, _port, attempt + 1, delayMs);

                    await Task.Delay(delayMs, ct);
                }
            }
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    private void DropConnection(NetworkStream? expectedStream = null)
    {
        NetworkStream? stream;
        TcpClient? client;
        lock (_connectionLock)
        {
            // An error from an old stream must not tear down a newer connection.
            if (expectedStream is not null && !ReferenceEquals(expectedStream, _stream)) return;
            stream = _stream;
            client = _client;
            _stream = null;
            _client = null;
            Interlocked.Increment(ref _connectionGeneration);
            _reconnectAttempts = 0;
        }
        stream?.Dispose();
        client?.Dispose();
        if (stream is not null || client is not null) ConnectionStateChanged?.Invoke(false);
    }

    // ─── Sequence number ──────────────────────────────────────────────────

    private int NextSeqNo()
    {
        // Interlocked wrap-around 0–999
        int current;
        int next;
        do
        {
            current = _seqNo;
            next    = (current + 1) % (AioiConstants.SeqNoMax + 1);
        } while (Interlocked.CompareExchange(ref _seqNo, next, current) != current);
        return next;
    }

    // ─── Disposal ─────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _sendQueue.Writer.TryComplete();
        await _cts.CancelAsync();
        if (_sendLoop is not null) await _sendLoop.ConfigureAwait(false);
        if (_recvLoop is not null) await _recvLoop.ConfigureAwait(false);
        DropConnection();
        _cts.Dispose();
        _reconnectLock.Dispose();
    }
}
