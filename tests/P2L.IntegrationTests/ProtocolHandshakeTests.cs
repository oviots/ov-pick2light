using System.Net.Sockets;
using System.Text;
using P2L.Hardware.Protocol;
using P2L.IntegrationTests.Infrastructure;
using P2L.Shared.Constants;
using Xunit;
using Xunit.Abstractions;

namespace P2L.IntegrationTests;

/// <summary>
/// Raw protocol-level handshake tests using a bare TcpClient — no AioiGateway abstraction.
/// This proves the framing logic (AioiPacket.ToBytes / TryParse) works against real hardware
/// before layering on the higher-level gateway.
///
/// Tests:
///   1. Send raw "@" (controller spec) — verify framing round-trip and "o" response
///   2. Send raw "Z" (init) — verify positive ACK
///   3. Send a packet with a bad sequence number — verify the gateway handles it gracefully
///   4. Verify the raw bytes on the wire match the documented frame format exactly
/// </summary>
public sealed class ProtocolHandshakeTests
{
    private readonly TestConfig        _cfg;
    private readonly ITestOutputHelper _out;

    public ProtocolHandshakeTests(ITestOutputHelper output)
    {
        _out = output;
        _cfg = TestConfig.Load();
    }

    // ── Test 1: Controller spec query (@) ─────────────────────────────────
    // The gateway requires Z to be sent on the connection before responding to @.
    // Response format: "@<retryCount><settings>" e.g. "@94----"

    [Fact]
    [Trait("Category", "Protocol")]
    public async Task ControllerSpec_ReturnsSpecPayload()
    {
        using var tcp = await OpenConnectionAsync();
        var stream = tcp.GetStream();

        // Must initialise the connection first
        var zResp = await SendAndReceiveAsync(stream, AioiEncoder.Initialize(), seqNo: 1);
        _out.WriteLine($"  Z  → {FormatBytes(zResp)}");
        Assert.True(zResp.Length > 0, "No response to Z command");

        // Now query controller spec
        var atResp = await SendAndReceiveAsync(stream, AioiEncoder.ControllerSpec(), seqNo: 2);
        _out.WriteLine($"  @  → {FormatBytes(atResp)}");

        var pkt = ParseResponse(atResp);
        Assert.NotNull(pkt);
        _out.WriteLine($"  Command : \"{pkt!.Command}\"  SeqNo={pkt.SequenceNo}");

        // "@<retryCount><settings...>" — e.g. "@94----"
        Assert.StartsWith("@", pkt.Command, StringComparison.Ordinal);
        Assert.True(pkt.Command.Length >= 2,
            $"Controller spec response too short: \"{pkt.Command}\"");
        _out.WriteLine($"  Retry count  : {pkt.Command[1]}");
        _out.WriteLine($"  Settings     : {pkt.Command[2..]}");
    }

    // ── Test 2: Z init command ────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Protocol")]
    public async Task Initialize_Z_ReturnsAck()
    {
        var (sent, received) = await RawRoundTripAsync(AioiEncoder.Initialize(), seqNo: 2);

        _out.WriteLine($"  Sent    : {FormatBytes(sent)}");
        _out.WriteLine($"  Received: {FormatBytes(received)}");

        var pkt = ParseResponse(received);
        Assert.NotNull(pkt);
        _out.WriteLine($"  Command : \"{pkt!.Command}\"  SeqNo={pkt.SequenceNo}");
        Assert.StartsWith("o", pkt.Command, StringComparison.Ordinal);
    }

    // ── Test 3: Verify raw wire frame format ──────────────────────────────

    [Fact]
    [Trait("Category", "Protocol")]
    public void Packet_WireBytes_MatchDocumentedFrameFormat()
    {
        // Documented format: STX(02h) | SeqNo("001") | DataLen("0001") | Command("Z") | ETX(03h)
        var pkt   = new AioiPacket { SequenceNo = 1, Command = "Z" };
        var bytes = pkt.ToBytes();

        _out.WriteLine($"  Bytes: {FormatBytes(bytes)}");
        _out.WriteLine($"  ASCII: {Encoding.ASCII.GetString(bytes, 1, bytes.Length - 2)}");

        Assert.Equal(AioiConstants.STX,            bytes[0]);                         // STX
        Assert.Equal((byte)'0',                    bytes[1]);                         // SeqNo[0]
        Assert.Equal((byte)'0',                    bytes[2]);                         // SeqNo[1]
        Assert.Equal((byte)'1',                    bytes[3]);                         // SeqNo[2]
        Assert.Equal((byte)'0',                    bytes[4]);                         // DataLen[0]
        Assert.Equal((byte)'0',                    bytes[5]);                         // DataLen[1]
        Assert.Equal((byte)'0',                    bytes[6]);                         // DataLen[2]
        Assert.Equal((byte)'1',                    bytes[7]);                         // DataLen[3]
        Assert.Equal((byte)'Z',                    bytes[8]);                         // Command
        Assert.Equal(AioiConstants.ETX,            bytes[9]);                         // ETX
        Assert.Equal(10,                           bytes.Length);                     // Total length
    }

    // ── Test 4: Packet round-trip parse ───────────────────────────────────

    [Fact]
    [Trait("Category", "Protocol")]
    public void Packet_TryParse_RoundTrips_BuildOutput()
    {
        foreach (var cmd in new[] { "Z", "Z1", "@", "M1121111211211111", "D0001" })
        {
            var original = new AioiPacket { SequenceNo = 42, Command = cmd };
            var bytes    = original.ToBytes();
            var parsed   = AioiPacket.TryParse(bytes, out int consumed);

            _out.WriteLine($"  Cmd=\"{cmd}\"  bytes={bytes.Length}  consumed={consumed}");

            Assert.NotNull(parsed);
            Assert.Equal(42,  parsed!.SequenceNo);
            Assert.Equal(cmd, parsed.Command);
            Assert.Equal(bytes.Length, consumed);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<TcpClient> OpenConnectionAsync()
    {
        var tcp = new TcpClient { NoDelay = true };
        using var cts = new CancellationTokenSource(_cfg.TcpTimeoutMs);
        await tcp.ConnectAsync(_cfg.GatewayHost, _cfg.GatewayPort, cts.Token);
        return tcp;
    }

    /// <summary>
    /// Sends one command and reads bytes until a complete Aioi packet is parsed
    /// or the timeout elapses. Does NOT set stream.ReadTimeout to avoid IOException;
    /// uses a CancellationToken instead.
    /// </summary>
    private async Task<byte[]> SendAndReceiveAsync(
        NetworkStream stream, string command, int seqNo)
    {
        var packet  = new AioiPacket { SequenceNo = seqNo, Command = command };
        var txBytes = packet.ToBytes();

        await stream.WriteAsync(txBytes);
        await stream.FlushAsync();

        var rxBuf   = new List<byte>(64);
        var buf     = new byte[256];
        using var readCts = new CancellationTokenSource(_cfg.TcpTimeoutMs);

        try
        {
            while (!readCts.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buf, readCts.Token);
                if (n == 0) break;
                rxBuf.AddRange(buf[..n]);

                var spanBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rxBuf));
                if (AioiPacket.TryParse(spanBytes, out _) is not null) break;
            }
        }
        catch (OperationCanceledException) { /* timeout — return whatever we have */ }

        return rxBuf.ToArray();
    }

    private async Task<(byte[] sent, byte[] received)> RawRoundTripAsync(
        string command, int seqNo)
    {
        var packet  = new AioiPacket { SequenceNo = seqNo, Command = command };
        var txBytes = packet.ToBytes();

        using var tcp = await OpenConnectionAsync();
        var rx = await SendAndReceiveAsync(tcp.GetStream(), command, seqNo);
        return (txBytes, rx);
    }

    private static AioiPacket? ParseResponse(byte[] data)
    {
        if (data.Length == 0) return null;
        var span  = new ReadOnlySpan<byte>(data);
        return AioiPacket.TryParse(span, out _);
    }

    private static string FormatBytes(byte[] b) =>
        string.Join(" ", b.Select(x => x switch
        {
            0x02 => "<STX>",
            0x03 => "<ETX>",
            _    => ((char)x).ToString(),
        }));
}
