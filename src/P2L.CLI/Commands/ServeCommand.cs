using System.Diagnostics;
using P2L.CLI.Web;
using Microsoft.Extensions.Logging;
using P2L.Core.Services;
using P2L.Hardware;
using P2L.Mqtt;
using Spectre.Console.Cli;

namespace P2L.CLI.Commands;

/// <summary>Runs the Bridge and its embedded browser UI. Service hosts can use --no-ui or --no-browser.</summary>
public sealed class ServeCommand : AsyncCommand<ServeCommand.Settings>
{
    private readonly IServiceProvider _services;
    private readonly AioiGateway           _gateway;
    private readonly HeartbeatService      _heartbeat;
    private readonly WatchdogService       _watchdog;
    private readonly CancellationTokenSource _cts;
    private readonly ILogger<ServeCommand> _logger;
    private readonly MqttBridgeService?    _mqtt;
    private readonly OfflineBufferService? _offlineBuffer;

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--no-ui")]
        public bool NoUi { get; init; }
        [CommandOption("--no-browser")]
        public bool NoBrowser { get; init; }
        [CommandOption("--ui-port")]
        public int UiPort { get; init; } = 8765;
    }

    public ServeCommand(
        IServiceProvider services,
        AioiGateway gateway,
        HeartbeatService heartbeat,
        WatchdogService watchdog,
        CancellationTokenSource cts,
        ILogger<ServeCommand> logger,
        MqttBridgeService? mqtt = null,
        OfflineBufferService? offlineBuffer = null)
    {
        _services = services;
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
        _logger.LogInformation("P2L service starting.");
        if (!settings.NoUi)
        {
            if (settings.UiPort is < 1 or > 65535) { _logger.LogError("UI port must be 1–65535"); return 1; }
            WebClientService ui;
            try
            {
                ui = (WebClientService)_services.GetService(typeof(WebClientService))!;
                await ui.StartAsync(settings.UiPort, _cts.Token);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return 0; }
            catch (Exception) { _logger.LogError("Cannot start local UI on port {Port}. Check port availability and device database permissions, or use --no-ui.", settings.UiPort); return 1; }
            if (!settings.NoBrowser && Environment.UserInteractive && !Console.IsInputRedirected)
            {
                try
                {
                    if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(ui.Url) {UseShellExecute = true});
                    else
                    {
                        var browser = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") {UseShellExecute = false};
                        browser.ArgumentList.Add(ui.Url); Process.Start(browser);
                    }
                }
                catch (Exception) { _logger.LogInformation("Open {Url} in your browser.", ui.Url); }
            }
        }

        try
        {
            await _gateway.StartAsync(_cts.Token);
            _logger.LogInformation("Gateway connected.");
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return 0; }
        catch (Exception ex)
        {
            // Don't abort: gateway has built-in auto-reconnect; service keeps running.
            _logger.LogWarning(ex, "Gateway connection failed on startup; auto-reconnect active.");
        }

        if (_cts.IsCancellationRequested) return 0;
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
