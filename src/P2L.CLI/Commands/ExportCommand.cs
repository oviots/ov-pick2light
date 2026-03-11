using P2L.CLI.Configuration;
using P2L.CLI.Infrastructure;
using P2L.Core;
using P2L.Core.Services;
using P2L.Hardware;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace P2L.CLI.Commands;

/// <summary>
/// Exports the current device list to a CSV file for offline editing.
///
/// Usage:
///   p2l export                                    export registry, default filename
///   p2l export --output shelf-a.csv               explicit output path
///   p2l export --scan                             scan 0001-0099 before exporting
///   p2l export --scan --from 1 --to 200           scan custom range then export
/// </summary>
public sealed class ExportCommand : AsyncCommand<ExportCommand.Settings>
{
    private readonly LightModuleRegistry  _registry;
    private readonly DeviceConfigCsvService _csv;
    private readonly DeviceScanService    _scanner;
    private readonly AioiGateway          _gateway;
    private readonly AppSettings          _appSettings;

    public ExportCommand(LightModuleRegistry     registry,
                         DeviceConfigCsvService  csv,
                         DeviceScanService       scanner,
                         AioiGateway             gateway,
                         AppSettings             appSettings)
    {
        _registry    = registry;
        _csv         = csv;
        _scanner     = scanner;
        _gateway     = gateway;
        _appSettings = appSettings;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("-o|--output")]
        [Description("Output CSV file path (default: devices_YYYYMMDD_HHmmss.csv)")]
        public string? Output { get; init; }

        [CommandOption("--scan")]
        [Description("Scan for connected modules before exporting")]
        public bool Scan { get; init; }

        [CommandOption("--from")]
        [Description("First address to scan (default 1, requires --scan)")]
        [DefaultValue(1)]
        public int From { get; init; } = 1;

        [CommandOption("--to")]
        [Description("Last address to scan (inclusive, default 99, requires --scan)")]
        [DefaultValue(99)]
        public int To { get; init; } = 99;

        [CommandOption("-c|--concurrency")]
        [Description("Concurrent scan queries (default 4, requires --scan)")]
        [DefaultValue(4)]
        public int Concurrency { get; init; } = 4;

        public override ValidationResult Validate()
        {
            if (Scan)
            {
                if (From < 1 || From > 9999)
                    return ValidationResult.Error("--from must be 1–9999.");
                if (To < From || To > 9999)
                    return ValidationResult.Error("--to must be ≥ --from and ≤ 9999.");
                if (Concurrency < 1 || Concurrency > 16)
                    return ValidationResult.Error("--concurrency must be 1–16.");
            }
            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings s)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // ── Optional: scan before export ──────────────────────────────────
        if (s.Scan)
        {
            AnsiConsole.MarkupLine("[bold]Scanning for modules before export...[/]");
            await _gateway.StartAsync(cts.Token);

            int total = s.To - s.From + 1;
            await AnsiConsole.Progress()
                .AutoRefresh(true)
                .HideCompleted(false)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(),
                         new PercentageColumn(), new RemainingTimeColumn(), new SpinnerColumn())
                .StartAsync(async ctx2 =>
                {
                    var task     = ctx2.AddTask("Scanning", maxValue: total);
                    var progress = new Progress<int>(_ => task.Increment(1));
                    await _scanner.ScanAsync(s.From, s.To, s.Concurrency, progress, cts.Token);
                });

            AnsiConsole.WriteLine();
        }

        // ── Determine output path ──────────────────────────────────────────
        var outputPath = s.Output
            ?? $"devices_{DateTime.Now:yyyyMMdd_HHmmss}.csv";

        // ── Export ────────────────────────────────────────────────────────
        var modules = _registry.All;

        if (modules.Count == 0)
            AnsiConsole.MarkupLine(
                "[yellow]Registry is empty.[/] " +
                "Run [grey]p2l devices[/] or use [grey]--scan[/] to discover modules first.");

        var gwDesc = $"{_appSettings.Gateway.Host}:{_appSettings.Gateway.Port}";
        _csv.Export(modules, outputPath, gwDesc);

        AnsiConsole.MarkupLine(
            $"[green]Exported[/] [bold]{modules.Count}[/] module(s) → " +
            $"[cyan]{Path.GetFullPath(outputPath)}[/]");

        if (modules.Count > 0)
            AnsiConsole.MarkupLine(
                "[grey]Edit the file, then run:[/] p2l import " +
                $"{Path.GetFileName(outputPath)}");

        return 0;
    }
}
