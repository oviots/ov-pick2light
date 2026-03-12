using P2L.Core;
using P2L.Hardware;
using P2L.Shared.Enums;
using P2L.Shared.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace P2L.CLI.Commands;

/// <summary>
/// Reads a CSV pick-task file and lights all modules at once.
/// Operators confirm in any order. This command exits immediately after
/// sending the batch — identical to `pick` but sourced from a CSV file.
///
/// CSV format (header optional, # = comment):
///   address,quantity[,color][,label]
///   0001,5,Red,Part-A
///   0002,3,,Part-B
/// </summary>
public sealed class RunPickCommand : AsyncCommand<RunPickCommand.Settings>
{
    private readonly PickingEngine _engine;
    private readonly AioiGateway   _gateway;

    public RunPickCommand(PickingEngine engine, AioiGateway gateway)
    {
        _engine  = engine;
        _gateway = gateway;
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

        var instructions = ParseCsv(s.File, s.Color);
        if (instructions.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No valid instructions found in CSV.[/]");
            return 1;
        }

        if (!_gateway.IsConnected)
            await _gateway.StartAsync(cts.Token);

        try
        {
            await _engine.StartBatchAsync(instructions, cts.Token);
            AnsiConsole.MarkupLine($"[green]Batch started:[/] {instructions.Count} module(s) lit");
            foreach (var inst in instructions)
                AnsiConsole.MarkupLine($"  [cyan]●[/] addr=[bold]{inst.Address}[/] " +
                                       $"qty=[bold]{inst.Quantity}[/] color=[bold]{inst.Color}[/]");
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

    private static IReadOnlyList<PickingInstruction> ParseCsv(string path, LightColor defaultColor)
    {
        var result = new List<PickingInstruction>();
        var lines  = System.IO.File.ReadAllLines(path);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;

            var tokens = trimmed.Split(',');
            if (tokens.Length < 2) continue;

            var address = tokens[0].Trim().PadLeft(4, '0');
            if (address.Equals("addr",    StringComparison.OrdinalIgnoreCase) ||
                address.Equals("address", StringComparison.OrdinalIgnoreCase)) continue;

            if (!int.TryParse(tokens[1].Trim(), out int qty) || qty <= 0) continue;

            var color = defaultColor;
            if (tokens.Length >= 3 && !string.IsNullOrWhiteSpace(tokens[2]) &&
                Enum.TryParse<LightColor>(tokens[2].Trim(), ignoreCase: true, out var c))
                color = c;

            result.Add(new PickingInstruction
            {
                Address  = address,
                Quantity = qty,
                Color    = color,
            });
        }

        return result;
    }
}
