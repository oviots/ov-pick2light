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

    // Protects direct stream writes (used by SendImmediateAsync) from racing with SendLoopAsync
    private readonly SemaphoreSlim _streamLock = new(1, 1);

    // Channel caps at 512 pending commands to prevent runaway memory use
    private readonly Channel<AioiPacket> _sendQueue =
        Channel.CreateBounded<AioiPacket>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            FullMode     = BoundedChannelFullMode.Wait,
        });

    private readonly CancellationTokenSource _cts = new();
    private Task? _sendLoop;
    private Task? _recvLoop;

    private int               _reconnectAttempts;
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);

    /// <summary>Fires when a complete packet arrives from the controller.</summary>
    public event Action<AioiPacket>? PacketReceived;

    /// <summary>Fires when the connection state changes.</summary>
    public event Action<bool>? ConnectionStateChanged;

    public bool IsConnected => _client?.Connected ?? false;

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
        _sendLoop = Task.Run(() => SendLoopAsync(_cts.Token), _cts.Token);
        _recvLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
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
        await _sendQueue.Writer.WriteAsync(packet, ct);
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
        if (_stream is null) return;
        var bytes = new AioiPacket { SequenceNo = seq, Command = command }.ToBytes();
        await _streamLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(bytes, ct);
            _log.LogDebug("[TX-IMM] SeqNo={Seq} Cmd={Cmd}", seq, command);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Immediate send failed");
            DropConnection();
        }
        finally
        {
            _streamLock.Release();
        }
    }

    // ─── Send loop ─────────────────────────────────────────────────────────

    private async Task SendLoopAsync(CancellationToken ct)
    {
        await foreach (var packet in _sendQueue.Reader.ReadAllAsync(ct))
        {
            await EnsureConnectedAsync(ct);
            await _streamLock.WaitAsync(ct);
            try
            {
                var bytes = packet.ToBytes();
                await _stream!.WriteAsync(bytes, ct);
                _log.LogDebug("[TX] SeqNo={Seq} Cmd={Cmd}", packet.SequenceNo, packet.Command);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Send failed; will reconnect");
                DropConnection();
            }
            finally
            {
                _streamLock.Release();
            }
        }
    }

    // ─── Receive loop ──────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer  = new byte[4096];
        var pending = new List<byte>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnsureConnectedAsync(ct);
                int read = await _stream!.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    // Graceful disconnect
                    DropConnection();
                    continue;
                }

                pending.AddRange(buffer[..read]);

                // Parse all complete packets from the pending buffer
                while (true)
                {
                    var span     = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending));
                    var pkt = AioiPacket.TryParse(span, out int consumed);
                    if (pkt is null) break;

                    _log.LogDebug("[RX] SeqNo={Seq} Cmd={Cmd}", pkt.SequenceNo, pkt.Command);
                    PacketReceived?.Invoke(pkt);
                    pending.RemoveRange(0, consumed);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Log message only — full stack trace is noise for routine TCP drops
                _log.LogWarning("Receive error; will reconnect: {Err}", ex.Message);
                DropConnection();
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
                    _client?.Dispose();
                    _client = new TcpClient();
                    _client.NoDelay = true;  // Disable Nagle for low-latency command delivery
                    await _client.ConnectAsync(_host, _port, ct);
                    _stream = _client.GetStream();
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

    private void DropConnection()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        _reconnectAttempts = 0;
        ConnectionStateChanged?.Invoke(false);
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
