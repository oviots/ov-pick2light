using P2L.Core;
using P2L.Hardware;
using P2L.Shared.Enums;
using P2L.Shared.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace P2L.CLI.Commands;

// ─── Light ON command ──────────────────────────────────────────────────────

public sealed class LightOnCommand : AsyncCommand<LightOnCommand.Settings>
{
    private readonly PickingEngine _engine;
    private readonly AioiGateway   _gateway;

    public LightOnCommand(PickingEngine engine, AioiGateway gateway)
    {
        _engine  = engine;
        _gateway = gateway;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<address>")]
        [Description("4-digit module address (e.g. 0001)")]
        public string Address { get; init; } = "";

        [CommandArgument(1, "<quantity>")]
        [Description("Quantity to display")]
        public int Quantity { get; init; }

        [CommandOption("-c|--color")]
        [Description("LED color: Off Red Green Blue Yellow Cyan Magenta White")]
        [DefaultValue(LightColor.Green)]
        public LightColor Color { get; init; } = LightColor.Green;

    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings s)
    {
        using var cts     = new CancellationTokenSource();
        using var sigInt  = PosixSignalRegistration.Create(PosixSignal.SIGINT,  sig => { sig.Cancel = true; cts.Cancel(); });
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, sig => { sig.Cancel = true; cts.Cancel(); });

        if (!_gateway.IsConnected)
            await _gateway.StartAsync(cts.Token);

        try
        {
            await _engine.QuickLightAsync(s.Address, s.Quantity, s.Color, ct: cts.Token);
            AnsiConsole.MarkupLine($"[green]Light ON:[/] addr=[bold]{s.Address}[/] qty=[bold]{s.Quantity}[/] color=[bold]{s.Color}[/]");
            return 0;
        }
        catch (TimeoutException ex)
        {
            AnsiConsole.MarkupLine($"[red]Timeout:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }
}

// ─── Light OFF command ─────────────────────────────────────────────────────

public sealed class LightOffCommand : AsyncCommand<LightOffCommand.Settings>
{
    private readonly PickingEngine _engine;
    private readonly AioiGateway   _gateway;

    public LightOffCommand(PickingEngine engine, AioiGateway gateway)
    {
        _engine  = engine;
        _gateway = gateway;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<address>")]
        [Description("4-digit module address to turn off")]
        public string Address { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings s)
    {
        using var cts     = new CancellationTokenSource();
        using var sigInt  = PosixSignalRegistration.Create(PosixSignal.SIGINT,  sig => { sig.Cancel = true; cts.Cancel(); });
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, sig => { sig.Cancel = true; cts.Cancel(); });

        if (!_gateway.IsConnected)
            await _gateway.StartAsync(cts.Token);

        try
        {
            await _engine.ExtinguishAsync(s.Address, cts.Token);
            AnsiConsole.MarkupLine($"[grey]Light OFF:[/] addr=[bold]{s.Address}[/]");
            return 0;
        }
        catch (TimeoutException ex)
        {
            AnsiConsole.MarkupLine($"[red]Timeout:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }
}

// ─── Terminate all command ─────────────────────────────────────────────────

public sealed class TerminateAllCommand : AsyncCommand<TerminateAllCommand.Settings>
{
    private readonly PickingEngine _engine;
    private readonly AioiGateway   _gateway;

    public TerminateAllCommand(PickingEngine engine, AioiGateway gateway)
    {
        _engine  = engine;
        _gateway = gateway;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("-b|--block")]
        [Description("Block number to terminate (default: 00 = all)")]
        [DefaultValue("00")]
        public string BlockNo { get; init; } = "00";
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings s)
    {
        using var cts     = new CancellationTokenSource();
        using var sigInt  = PosixSignalRegistration.Create(PosixSignal.SIGINT,  sig => { sig.Cancel = true; cts.Cancel(); });
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, sig => { sig.Cancel = true; cts.Cancel(); });

        if (!_gateway.IsConnected)
            await _gateway.StartAsync(cts.Token);

        try
        {
            await _engine.FinishBatchAsync(s.BlockNo, cts.Token);
            AnsiConsole.MarkupLine("[green]All lights terminated.[/]");
            return 0;
        }
        catch (TimeoutException ex)
        {
            AnsiConsole.MarkupLine($"[red]Timeout:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }
}

// ─── Batch pick command ────────────────────────────────────────────────────

public sealed class BatchPickCommand : AsyncCommand<BatchPickCommand.Settings>
{
    private readonly PickingEngine _engine;
    private readonly AioiGateway   _gateway;

    public BatchPickCommand(PickingEngine engine, AioiGateway gateway)
    {
        _engine  = engine;
        _gateway = gateway;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<instructions>")]
        [Description("Comma-separated addr:qty[[:color]] entries. Color is optional per entry. " +
                     "Example: 0001:5:Red,0002:3,0003:10:Blue  (0002 uses the global --color)")]
        public string Instructions { get; init; } = "";

        [CommandOption("-c|--color")]
        [Description("Default color applied to entries that do not specify their own color")]
        [DefaultValue(LightColor.Green)]
        public LightColor Color { get; init; } = LightColor.Green;

    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings s)
    {
        using var cts     = new CancellationTokenSource();
        using var sigInt  = PosixSignalRegistration.Create(PosixSignal.SIGINT,  sig => { sig.Cancel = true; cts.Cancel(); });
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, sig => { sig.Cancel = true; cts.Cancel(); });

        var instructions = ParseInstructions(s.Instructions, s.Color);
        if (instructions.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No valid instructions parsed. Format: 0001:5,0002:3[/]");
            return 1;
        }

        if (!_gateway.IsConnected)
            await _gateway.StartAsync(cts.Token);

        try
        {
            await _engine.StartBatchAsync(instructions, cts.Token);
            AnsiConsole.MarkupLine($"[green]Batch started:[/] {instructions.Count} modules lit");
            return 0;
        }
        catch (TimeoutException ex)
        {
            AnsiConsole.MarkupLine($"[red]Timeout:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static IReadOnlyList<PickingInstruction> ParseInstructions(
        string raw, LightColor defaultColor)
    {
        var result = new List<PickingInstruction>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = part.Trim().Split(':');
            if (tokens.Length < 2) continue;
            if (!int.TryParse(tokens[1], out int qty)) continue;

            // Optional 3rd token overrides the global default color for this entry
            var color = defaultColor;
            if (tokens.Length >= 3 && Enum.TryParse<LightColor>(tokens[2].Trim(), ignoreCase: true, out var entryColor))
                color = entryColor;

            result.Add(new PickingInstruction
            {
                Address  = tokens[0].Trim().PadLeft(4, '0'),
                Quantity = qty,
                Color    = color,
            });
        }
        return result;
    }
}
