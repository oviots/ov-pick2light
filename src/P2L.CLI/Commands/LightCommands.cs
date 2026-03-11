using P2L.Core;
using P2L.Shared.Enums;
using P2L.Shared.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace P2L.CLI.Commands;

// ─── Light ON command ──────────────────────────────────────────────────────

public sealed class LightOnCommand : AsyncCommand<LightOnCommand.Settings>
{
    private readonly PickingEngine _engine;

    public LightOnCommand(PickingEngine engine) => _engine = engine;

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

        [CommandOption("-f|--flash")]
        [Description("Use flashing mode instead of steady on")]
        public bool Flash { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings s)
    {
        var mode = s.Flash ? LightState.Flash : LightState.On;
        await _engine.QuickLightAsync(s.Address, s.Quantity, s.Color);
        AnsiConsole.MarkupLine($"[green]Light ON:[/] addr=[bold]{s.Address}[/] qty=[bold]{s.Quantity}[/] color=[bold]{s.Color}[/]");
        return 0;
    }
}

// ─── Light OFF command ─────────────────────────────────────────────────────

public sealed class LightOffCommand : AsyncCommand<LightOffCommand.Settings>
{
    private readonly PickingEngine _engine;

    public LightOffCommand(PickingEngine engine) => _engine = engine;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<address>")]
        [Description("4-digit module address to turn off")]
        public string Address { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings s)
    {
        await _engine.ExtinguishAsync(s.Address);
        AnsiConsole.MarkupLine($"[grey]Light OFF:[/] addr=[bold]{s.Address}[/]");
        return 0;
    }
}

// ─── Terminate all command ─────────────────────────────────────────────────

public sealed class TerminateAllCommand : AsyncCommand<TerminateAllCommand.Settings>
{
    private readonly PickingEngine _engine;

    public TerminateAllCommand(PickingEngine engine) => _engine = engine;

    public sealed class Settings : CommandSettings
    {
        [CommandOption("-b|--block")]
        [Description("Block number to terminate (default: 00 = all)")]
        [DefaultValue("00")]
        public string BlockNo { get; init; } = "00";
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings s)
    {
        await _engine.FinishBatchAsync(s.BlockNo);
        AnsiConsole.MarkupLine("[green]All lights terminated.[/]");
        return 0;
    }
}

// ─── Batch pick command ────────────────────────────────────────────────────

public sealed class BatchPickCommand : AsyncCommand<BatchPickCommand.Settings>
{
    private readonly PickingEngine _engine;

    public BatchPickCommand(PickingEngine engine) => _engine = engine;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<instructions>")]
        [Description("Comma-separated addr:qty pairs, e.g. 0001:5,0002:3,0003:10")]
        public string Instructions { get; init; } = "";

        [CommandOption("-c|--color")]
        [DefaultValue(LightColor.Green)]
        public LightColor Color { get; init; } = LightColor.Green;

        [CommandOption("-b|--block")]
        [DefaultValue("00")]
        public string BlockNo { get; init; } = "00";
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings s)
    {
        var instructions = ParseInstructions(s.Instructions, s.Color, s.BlockNo);
        if (instructions.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No valid instructions parsed. Format: 0001:5,0002:3[/]");
            return 1;
        }

        await _engine.StartBatchAsync(instructions);
        AnsiConsole.MarkupLine($"[green]Batch started:[/] {instructions.Count} modules lit");
        return 0;
    }

    private static IReadOnlyList<PickingInstruction> ParseInstructions(
        string raw, LightColor color, string blockNo)
    {
        var result = new List<PickingInstruction>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = part.Trim().Split(':');
            if (tokens.Length != 2) continue;
            if (!int.TryParse(tokens[1], out int qty)) continue;
            result.Add(new PickingInstruction
            {
                Address = tokens[0].Trim().PadLeft(4, '0'),
                Quantity = qty,
                Color   = color,
                BlockNo = blockNo,
            });
        }
        return result;
    }
}
