using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using P2L.Hardware;
using Xunit;

namespace P2L.IntegrationTests.Infrastructure;

/// <summary>
/// xUnit class fixture that owns one <see cref="AioiGateway"/> for the lifetime
/// of a test class. The gateway is started once, initialized (Z), then shared
/// across all tests in the class. Disposed after the last test.
///
/// Usage: implement IClassFixture&lt;GatewayFixture&gt; on your test class.
/// </summary>
public sealed class GatewayFixture : IAsyncLifetime
{
    public TestConfig     Config  { get; } = TestConfig.Load();
    public AioiGateway    Gateway { get; private set; } = null!;
    public bool           Ready   { get; private set; }

    public async Task InitializeAsync()
    {
        // NullLogger so the fixture itself doesn't spam; each test wires its own logger
        Gateway = new AioiGateway(Config.GatewayHost, Config.GatewayPort,
                                  NullLogger.Instance);
        try
        {
            using var cts = new CancellationTokenSource(Config.TcpTimeoutMs);
            await Gateway.StartAsync(cts.Token);

            // Give the TcpTransport a moment to establish the connection
            await Task.Delay(500);

            using var init = new CancellationTokenSource(Config.TcpTimeoutMs);
            await Gateway.InitializeAsync(init.Token);

            Ready = true;
        }
        catch
        {
            Ready = false;
            // Don't throw — let each test decide how to handle a missing gateway
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Ready)
        {
            try
            {
                using var cts = new CancellationTokenSource(3_000);
                await Gateway.TerminateBlockAsync("00", cts.Token);
            }
            catch { /* best-effort cleanup */ }
        }
        await Gateway.DisposeAsync();
    }

    // xUnit IAsyncLifetime requires this signature
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();
}
