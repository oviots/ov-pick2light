using P2L.CLI.Configuration;
using P2L.CLI.Infrastructure;
using P2L.Core;
using P2L.Core.Services;
using P2L.Hardware;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;

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
        using var cts     = new CancellationTokenSource();
        using var sigInt  = PosixSignalRegistration.Create(PosixSignal.SIGINT,  sig => { sig.Cancel = true; cts.Cancel(); });
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, sig => { sig.Cancel = true; cts.Cancel(); });

        // ── Scan: explicit flag, or auto-triggered when registry is empty ─────
        bool autoScan = !s.Scan && _registry.All.Count == 0;
        if (s.Scan || autoScan)
        {
            if (autoScan)
                AnsiConsole.MarkupLine("[yellow]Registry is empty — auto-scanning for modules...[/]");
            else
                AnsiConsole.MarkupLine("[bold]Scanning for modules before export...[/]");

            // ── Quick connectivity check before the full scan ──────────────
            if (!await GatewayReachableAsync(_appSettings.Gateway.Host, _appSettings.Gateway.Port, cts.Token))
            {
                AnsiConsole.MarkupLine(
                    $"[red]Cannot connect to gateway[/] [bold]{_appSettings.Gateway.Host}:{_appSettings.Gateway.Port}[/]");
                AnsiConsole.MarkupLine("[grey]  • Check the gateway is powered on and on the same subnet[/]");
                AnsiConsole.MarkupLine("[grey]  • Verify the address in [cyan]appsettings.json[/] → [bold]Gateway.Host / Gateway.Port[/][/]");
                return 1;
            }

            await _gateway.StartAsync(cts.Token);

            int total   = s.To - s.From + 1;
            int scanned = 0;
            var progress = new Progress<int>(_ =>
            {
                var n = Interlocked.Increment(ref scanned);
                if (n % 10 == 0 || n == total)
                    Console.Write($"\r  Scanning {n} / {total}...   ");
            });
            await _scanner.ScanAsync(s.From, s.To, s.Concurrency, progress, cts.Token);
            Console.WriteLine();

            AnsiConsole.WriteLine();
        }

        // ── Determine output path ──────────────────────────────────────────
        var outputPath = s.Output
            ?? $"devices_{DateTime.Now:yyyyMMdd_HHmmss}.csv";

        // ── Export ────────────────────────────────────────────────────────
        var modules = _registry.All;

        if (modules.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]No modules found.[/] Check gateway address/range or verify hardware is powered on.");
            return 1;
        }

        AnsiConsole.MarkupLine($"[grey]Writing {modules.Count} module(s)...[/]");

        var gwDesc   = $"{_appSettings.Gateway.Host}:{_appSettings.Gateway.Port}";
        var fullPath = Path.GetFullPath(outputPath);
        try
        {
            _csv.Export(modules, outputPath, gwDesc);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to write CSV:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"[green]Exported[/] [bold]{modules.Count}[/] module(s) → " +
            $"[cyan]{Markup.Escape(fullPath)}[/]");
        AnsiConsole.MarkupLine(
            $"[grey]Edit the file, then run:[/] p2l import {Markup.Escape(Path.GetFileName(outputPath))}");

        return 0;
    }

    /// <summary>
    /// Attempts a TCP connection with a 3-second timeout to verify the gateway is reachable
    /// before committing to a full scan (which can take minutes on an unreachable host).
    /// </summary>
    private static async Task<bool> GatewayReachableAsync(string host, int port, CancellationToken ct)
    {
        using var client  = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(3_000);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
