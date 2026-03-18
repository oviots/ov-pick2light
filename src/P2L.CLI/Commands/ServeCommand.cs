using Microsoft.Extensions.Logging;
using P2L.Core.Services;
using P2L.Hardware;
using P2L.Mqtt;
using Spectre.Console.Cli;

namespace P2L.CLI.Commands;

/// <summary>
/// Headless daemon mode — the entry point for the Windows Service and systemd unit.
///
/// Starts the Aioi gateway connection, heartbeat, watchdog, and (if configured)
/// the MQTT bridge, then blocks until the process is stopped by SCM / SIGTERM.
///
/// Unlike <see cref="RootCommand"/>, this command requires no interactive console
/// and produces no ANSI UI output — all status is written to the rotating log file.
/// </summary>
public sealed class ServeCommand : AsyncCommand<ServeCommand.Settings>
{
    private readonly AioiGateway           _gateway;
    private readonly HeartbeatService      _heartbeat;
    private readonly WatchdogService       _watchdog;
    private readonly CancellationTokenSource _cts;
    private readonly ILogger<ServeCommand> _logger;
    private readonly MqttBridgeService?    _mqtt;
    private readonly OfflineBufferService? _offlineBuffer;

    public sealed class Settings : CommandSettings { }

    public ServeCommand(
        AioiGateway gateway,
        HeartbeatService heartbeat,
        WatchdogService watchdog,
        CancellationTokenSource cts,
        ILogger<ServeCommand> logger,
        MqttBridgeService? mqtt = null,
        OfflineBufferService? offlineBuffer = null)
    {
        _gateway       = gateway;
        _heartbeat     = heartbeat;
        _watchdog      = watchdog;
        _cts           = cts;
        _logger        = logger;
        _mqtt          = mqtt;
        _offlineBuffer = offlineBuffer;
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings settings)
    {
        _logger.LogInformation("P2L service starting (daemon mode).");

        try
        {
            await _gateway.StartAsync(_cts.Token);
            _logger.LogInformation("Gateway connected.");
        }
        catch (Exception ex)
        {
            // Don't abort: gateway has built-in auto-reconnect; service keeps running.
            _logger.LogWarning(ex, "Gateway connection failed on startup; auto-reconnect active.");
        }

        _heartbeat.Start();
        _watchdog.Start();

        if (_mqtt is not null)
        {
            await _mqtt.StartAsync(_cts.Token);
            _logger.LogInformation("MQTT bridge started.");
        }
        else if (_offlineBuffer is not null)
        {
            _offlineBuffer.Start();
            _logger.LogInformation("MQTT offline buffer active (broker not configured).");
        }

        _logger.LogInformation("P2L service running. Awaiting stop signal...");

        try
        {
            // Block until SCM Stop / SIGTERM / Ctrl-C cancels the shared CTS.
            await Task.Delay(Timeout.Infinite, _cts.Token);
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("P2L service stopping.");
        return 0;
    }
}
