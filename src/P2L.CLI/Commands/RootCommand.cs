using P2L.CLI.Dashboard;
using P2L.Core;
using P2L.Core.Services;
using P2L.Hardware;
using P2L.Mqtt;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace P2L.CLI.Commands;

/// <summary>
/// Root command: launches the interactive live dashboard.
/// Running `p2l` with no sub-command opens the full warehouse operator view.
/// </summary>
public sealed class RootCommand : AsyncCommand<RootCommand.Settings>
{
    private readonly AioiGateway         _gateway;
    private readonly LightModuleRegistry _registry;
    private readonly HeartbeatService    _heartbeat;
    private readonly MqttBridgeService?  _mqtt;

    public RootCommand(AioiGateway gateway, LightModuleRegistry registry,
                       HeartbeatService heartbeat, MqttBridgeService? mqtt = null)
    {
        _gateway   = gateway;
        _registry  = registry;
        _heartbeat = heartbeat;
        _mqtt      = mqtt;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--no-init")]
        [Description("Skip sending the Z initialization command on startup")]
        public bool NoInit { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings settings)
    {
        using var cts     = new CancellationTokenSource();
        using var sigInt  = PosixSignalRegistration.Create(PosixSignal.SIGINT,  sig => { sig.Cancel = true; cts.Cancel(); });
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, sig => { sig.Cancel = true; cts.Cancel(); });

        AnsiConsole.MarkupLine("[bold yellow]Pick2Light[/] starting...");

        await _gateway.StartAsync(cts.Token);
        _heartbeat.Start();

        if (_mqtt is not null)
        {
            await _mqtt.StartAsync(cts.Token);
            AnsiConsole.MarkupLine("[green]MQTT bridge started.[/]");
        }

        if (!settings.NoInit)
        {
            AnsiConsole.Status()
                .Start("Initializing gateway...", _ => { });
            try
            {
                await _gateway.InitializeAsync(cts.Token);
                AnsiConsole.MarkupLine("[green]Gateway initialized.[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Init failed:[/] {ex.Message}");
                AnsiConsole.MarkupLine("[grey]Dashboard will retry connection automatically.[/]");
            }
        }

        var dashboard = new WarehouseDashboard(_gateway, _registry);
        await dashboard.RunAsync(cts.Token);

        AnsiConsole.MarkupLine("[grey]Shutting down...[/]");
        await _gateway.TerminateBlockAsync(ct: CancellationToken.None);
        return 0;
    }
}
