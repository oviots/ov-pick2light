using System.Net.NetworkInformation;
using System.Net.Sockets;
using P2L.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace P2L.IntegrationTests;

/// <summary>
/// Layer 3/4 connectivity checks — no Aioi protocol involved.
///
/// Tests:
///   1. ICMP ping reaches the gateway IP
///   2. TCP port 5003 accepts a connection within timeout
///   3. TCP connection stays open (gateway does not immediately close idle connections)
///
/// These pass without any light modules attached — they only need the gateway unit powered on.
/// </summary>
public sealed class NetworkConnectivityTests
{
    private readonly TestConfig        _cfg;
    private readonly ITestOutputHelper _out;

    public NetworkConnectivityTests(ITestOutputHelper output)
    {
        _out = output;
        _cfg = TestConfig.Load();
        _out.WriteLine($"Config: {_cfg}");
    }

    // ── Test 1: ICMP ping ──────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Network")]
    public async Task Gateway_RespondsTo_IcmpPing()
    {
        _out.WriteLine($"Pinging {_cfg.GatewayHost} (timeout {_cfg.PingTimeoutMs} ms)...");

        using var ping  = new Ping();
        var reply = await ping.SendPingAsync(_cfg.GatewayHost, _cfg.PingTimeoutMs);

        _out.WriteLine($"  Status  : {reply.Status}");
        _out.WriteLine($"  RTT     : {reply.RoundtripTime} ms");
        _out.WriteLine($"  Address : {reply.Address}");

        Assert.Equal(IPStatus.Success, reply.Status);
        Assert.True(reply.RoundtripTime < 500,
            $"RTT {reply.RoundtripTime} ms exceeds 500 ms — check cable / switch.");
    }

    // ── Test 2: TCP port open ──────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Network")]
    public async Task Gateway_TcpPort5003_IsOpen()
    {
        _out.WriteLine($"Connecting TCP to {_cfg.GatewayHost}:{_cfg.GatewayPort}...");

        using var tcp = new TcpClient();
        tcp.NoDelay = true;

        using var cts = new CancellationTokenSource(_cfg.TcpTimeoutMs);
        await tcp.ConnectAsync(_cfg.GatewayHost, _cfg.GatewayPort, cts.Token);

        _out.WriteLine($"  Connected : {tcp.Connected}");
        _out.WriteLine($"  Local EP  : {tcp.Client.LocalEndPoint}");
        _out.WriteLine($"  Remote EP : {tcp.Client.RemoteEndPoint}");

        Assert.True(tcp.Connected, "TCP connection failed.");
    }

    // ── Test 3: Connection stays alive ────────────────────────────────────

    [Fact]
    [Trait("Category", "Network")]
    public async Task Gateway_TcpConnection_RemainsOpenWhileIdle()
    {
        const int idleMs = 3_000;
        _out.WriteLine($"Opening TCP, holding idle for {idleMs} ms...");

        using var tcp = new TcpClient { NoDelay = true };
        using var cts = new CancellationTokenSource(_cfg.TcpTimeoutMs);
        await tcp.ConnectAsync(_cfg.GatewayHost, _cfg.GatewayPort, cts.Token);

        Assert.True(tcp.Connected, "Failed to connect initially.");
        _out.WriteLine("  Connected. Waiting...");

        await Task.Delay(idleMs);

        // Check if the socket is still live by polling the stream
        var stream = tcp.GetStream();
        bool stillConnected = tcp.Connected && stream.CanWrite;

        _out.WriteLine($"  Still connected after {idleMs} ms idle: {stillConnected}");
        Assert.True(stillConnected,
            "Gateway closed the connection during idle. " +
            "Consider sending a heartbeat more frequently than the gateway's idle timeout.");
    }
}
