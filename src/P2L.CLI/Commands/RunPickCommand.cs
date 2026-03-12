using P2L.Core;
using P2L.Core.Services;
using P2L.Hardware;
using P2L.Mqtt;
using P2L.Shared.Enums;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace P2L.CLI.Commands;

/// <summary>
/// Runs a CSV pick-task file sequentially.
/// Each row is lit, waits for operator confirmation, then deleted from the CSV.
/// Real-time MQTT commands take strict priority over task advancement.
///
/// CSV format (header optional, # = comment):
///   address,quantity[,color][,label]
///   0001,5,Red,Part-A
///   0002,3,,Part-B
/// </summary>
public sealed class RunPickCommand : AsyncCommand<RunPickCommand.Settings>
{
    private readonly AioiGateway          _gateway;
    private readonly HeartbeatService     _heartbeat;
    private readonly WatchdogService      _watchdog;
    private readonly CsvPickTaskRunner    _runner;
    private readonly MqttBridgeService?   _mqtt;
    private readonly OfflineBufferService? _offlineBuffer;

    public RunPickCommand(AioiGateway gateway, HeartbeatService heartbeat,
                          WatchdogService watchdog, CsvPickTaskRunner runner,
                          MqttBridgeService? mqtt = null,
                          OfflineBufferService? offlineBuffer = null)
    {
        _gateway       = gateway;
        _heartbeat     = heartbeat;
        _watchdog      = watchdog;
        _runner        = runner;
        _mqtt          = mqtt;
        _offlineBuffer = offlineBuffer;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<file>")]
        [Description("Path to the pick-task CSV file")]
        public string File { get; init; } = "";

        [CommandOption("-c|--color")]
        [Description("Default color for entries that omit color: Off Red Green Blue Yellow Cyan Magenta White")]
        [DefaultValue(LightColor.Green)]
        public LightColor Color { get; init; } = LightColor.Green;

        [CommandOption("-b|--block")]
        [Description("Block number used for batch grouping (default: 00)")]
        [DefaultValue("00")]
        public string BlockNo { get; init; } = "00";

        public override ValidationResult Validate()
        {
            if (!System.IO.File.Exists(File))
                return ValidationResult.Error($"File not found: {File}");
            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings s)
    {
        using var cts     = new CancellationTokenSource();
        using var sigInt  = PosixSignalRegistration.Create(PosixSignal.SIGINT,  sig => { sig.Cancel = true; cts.Cancel(); });
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, sig => { sig.Cancel = true; cts.Cancel(); });

        // ── Start infrastructure ──────────────────────────────────────────
        await _gateway.StartAsync(cts.Token);
        _heartbeat.Start();
        _watchdog.Start();
        await _gateway.EnsureInitializedAsync(cts.Token);

        if (_mqtt is not null)
            await _mqtt.StartAsync(cts.Token);
        else
            _offlineBuffer?.Start();

        // ── Start runner ──────────────────────────────────────────────────
        AnsiConsole.MarkupLine($"[bold yellow]CSV Pick Run:[/] [grey]{s.File}[/]  " +
                               $"default color=[bold]{s.Color}[/]");

        _runner.Start(s.File, s.Color, s.BlockNo);

        // ── Wait for completion or cancellation ───────────────────────────
        var done = new TaskCompletionSource();
        _runner.TaskEvent += e =>
        {
            if (e.EventType == "csv_completed")
                done.TrySetResult();
            else if (e.EventType == "task_started")
                AnsiConsole.MarkupLine($"  [cyan]→[/] addr=[bold]{e.Row!.Address}[/] " +
                                       $"qty=[bold]{e.Row.Quantity}[/] " +
                                       $"color=[bold]{e.Row.Color}[/]" +
                                       (e.Row.Label is not null ? $" [grey]{e.Row.Label}[/]" : ""));
            else if (e.EventType == "task_confirmed")
                AnsiConsole.MarkupLine($"  [green]✓[/] addr=[bold]{e.Row!.Address}[/] confirmed");
            else if (e.EventType == "task_error")
                AnsiConsole.MarkupLine($"  [red]✗[/] addr=[bold]{e.Row!.Address}[/] error");
        };

        try
        {
            await done.Task.WaitAsync(cts.Token);
            AnsiConsole.MarkupLine("[green]All pick tasks completed.[/]");
            return 0;
        }
        catch (OperationCanceledException)
        {
            await _runner.CancelAsync();
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            return 130;
        }
    }
}
