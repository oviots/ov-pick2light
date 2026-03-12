using P2L.Core;
using P2L.Core.Services;
using P2L.Hardware;
using P2L.Shared.Enums;
using P2L.Shared.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace P2L.CLI.Commands;

/// <summary>
/// Scans a range of module addresses and displays a formatted device list.
///
/// Usage:
///   p2l devices                         scan addresses 0001–0099
///   p2l devices --from 1 --to 200       custom range
///   p2l devices --from 1 --to 50 -c 8  eight concurrent queries
/// </summary>
public sealed class ListDevicesCommand : AsyncCommand<ListDevicesCommand.Settings>
{
    private readonly AioiGateway        _gateway;
    private readonly DeviceScanService  _scanner;

    public ListDevicesCommand(AioiGateway gateway, DeviceScanService scanner)
    {
        _gateway = gateway;
        _scanner = scanner;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--from")]
        [Description("First module address to probe (1–9999, default 1)")]
        [DefaultValue(1)]
        public int From { get; init; } = 1;

        [CommandOption("--to")]
        [Description("Last module address to probe (inclusive, default 99)")]
        [DefaultValue(99)]
        public int To { get; init; } = 99;

        [CommandOption("-c|--concurrency")]
        [Description("Number of parallel Az queries (default 4, max 16)")]
        [DefaultValue(4)]
        public int Concurrency { get; init; } = 4;

        public override ValidationResult Validate()
        {
            if (From < 1 || From > 9999)
                return ValidationResult.Error("--from must be between 1 and 9999.");
            if (To < From || To > 9999)
                return ValidationResult.Error("--to must be ≥ --from and ≤ 9999.");
            if (Concurrency < 1 || Concurrency > 16)
                return ValidationResult.Error("--concurrency must be between 1 and 16.");
            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings s)
    {
        using var cts     = new CancellationTokenSource();
        using var sigInt  = PosixSignalRegistration.Create(PosixSignal.SIGINT,  sig => { sig.Cancel = true; cts.Cancel(); });
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, sig => { sig.Cancel = true; cts.Cancel(); });

        if (!_gateway.IsConnected)
        {
            AnsiConsole.MarkupLine("[yellow]Gateway not connected — starting transport...[/]");
            await _gateway.StartAsync(cts.Token);
        }

        int total = s.To - s.From + 1;
        IReadOnlyList<LightModuleState>? results = null;

        AnsiConsole.MarkupLine(
            $"[bold]Scanning[/] addresses [cyan]{s.From:D4}[/]–[cyan]{s.To:D4}[/]  " +
            $"([grey]{total} probes, concurrency={s.Concurrency}[/])");
        AnsiConsole.WriteLine();

        int probed = 0;
        var progress = new Progress<int>(_ =>
        {
            var n = Interlocked.Increment(ref probed);
            if (n % 10 == 0 || n == total)
                Console.Write($"\r  Probing {n} / {total}...   ");
        });
        results = await _scanner.ScanAsync(
            startAddr:      s.From,
            endAddr:        s.To,
            maxConcurrency: s.Concurrency,
            progress:       progress,
            ct:             cts.Token);
        Console.WriteLine();

        AnsiConsole.WriteLine();
        RenderTable(results ?? []);
        return 0;
    }

    // ─── Rendering ────────────────────────────────────────────────────────

    private static void RenderTable(IReadOnlyList<LightModuleState> modules)
    {
        if (modules.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No modules found in the scanned range.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title($"[bold yellow]Light Modules[/] [grey]({modules.Count} found)[/]")
            .AddColumn(new TableColumn("[bold]Address[/]").Centered())
            .AddColumn(new TableColumn("[bold]Status[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Color[/]").Centered())
            .AddColumn(new TableColumn("[bold]Mode[/]").Centered())
            .AddColumn(new TableColumn("[bold]Display[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Last Instruction[/]").RightAligned());

        foreach (var m in modules)
            table.AddRow(
                $"[bold]{m.Address}[/]",
                StatusMarkup(m.Status),
                ColorMarkup(m.Color),
                ModeMarkup(m.LedMode),
                $"[grey]{m.DisplayData.TrimStart()}[/]",
                m.LastInstructionAt.HasValue
                    ? $"[grey]{m.LastInstructionAt.Value:HH:mm:ss}[/]"
                    : "[grey]—[/]");

        AnsiConsole.Write(table);

        // Summary footer
        var byStatus = modules
            .GroupBy(m => m.Status)
            .OrderBy(g => g.Key.ToString())
            .Select(g => $"{StatusMarkup(g.Key)} [grey]×{g.Count()}[/]");
        AnsiConsole.MarkupLine("  " + string.Join("   ", byStatus));
        AnsiConsole.WriteLine();
    }

    private static string StatusMarkup(ModuleStatus s) => s switch
    {
        ModuleStatus.PowerOn     => "[grey]PowerOn[/]",
        ModuleStatus.Standby     => "[green]Standby[/]",
        ModuleStatus.Operating   => "[yellow]⚡ Active[/]",
        ModuleStatus.Operating2  => "[yellow]⚡ Active2[/]",
        ModuleStatus.Completed   => "[cyan]✓ Done[/]",
        ModuleStatus.Shortage    => "[darkorange]⚠ Shortage[/]",
        ModuleStatus.Maintenance => "[blue]Maintenance[/]",
        ModuleStatus.Malfunction => "[red]✗ Fault[/]",
        _                        => "[grey]Unknown[/]",
    };

    private static string ColorMarkup(LightColor c) => c switch
    {
        LightColor.Red     => "[red]■ Red[/]",
        LightColor.Green   => "[green]■ Green[/]",
        LightColor.Blue    => "[blue]■ Blue[/]",
        LightColor.Yellow  => "[yellow]■ Yellow[/]",
        LightColor.Cyan    => "[cyan]■ Cyan[/]",
        LightColor.Magenta => "[magenta]■ Magenta[/]",
        LightColor.White   => "[white]■ White[/]",
        _                  => "[grey]Off[/]",
    };

    private static string ModeMarkup(LightState m) => m switch
    {
        LightState.On        => "[green]On[/]",
        LightState.Flash     => "[yellow]Flash[/]",
        LightState.HighFlash => "[yellow]HiFlash[/]",
        _                    => "[grey]Off[/]",
    };
}
