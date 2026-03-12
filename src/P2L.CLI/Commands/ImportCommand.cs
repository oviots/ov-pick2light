using P2L.CLI.Infrastructure;
using P2L.Hardware;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace P2L.CLI.Commands;

/// <summary>
/// Imports a CSV file produced by <c>p2l export</c> (or hand-crafted) and applies
/// the specified parameters to the corresponding hardware modules.
///
/// Usage:
///   p2l import devices.csv                         apply with confirmation prompt
///   p2l import devices.csv --dry-run               preview changes, no hardware writes
///   p2l import devices.csv --yes                   apply without prompting
///   p2l import devices.csv --skip-invalid          skip bad rows instead of aborting
/// </summary>
public sealed class ImportCommand : AsyncCommand<ImportCommand.Settings>
{
    private readonly DeviceConfigCsvService _csv;
    private readonly AioiGateway            _gateway;

    public ImportCommand(DeviceConfigCsvService csv, AioiGateway gateway)
    {
        _csv     = csv;
        _gateway = gateway;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<file>")]
        [Description("Path to the CSV file to import")]
        public string File { get; init; } = "";

        [CommandOption("--dry-run")]
        [Description("Preview what would be applied without sending any commands")]
        public bool DryRun { get; init; }

        [CommandOption("-y|--yes")]
        [Description("Skip the confirmation prompt and apply immediately")]
        public bool Yes { get; init; }

        [CommandOption("--skip-invalid")]
        [Description("Skip rows with invalid parameters instead of aborting")]
        public bool SkipInvalid { get; init; }

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

        // ── Parse CSV ─────────────────────────────────────────────────────
        AnsiConsole.MarkupLine($"[grey]Reading[/] [bold]{Path.GetFileName(s.File)}[/]...");

        var (records, parseErrors) = _csv.ParseFile(s.File);

        // Report file-level parse errors
        if (parseErrors.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]{parseErrors.Count} parse issue(s):[/]");
            foreach (var e in parseErrors)
                AnsiConsole.MarkupLine($"  [grey]row {e.Row}[/] [red]{e.Message}[/]");
        }

        if (records.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No valid rows found in the file.[/]");
            return 1;
        }

        // ── Validate each record's field values ───────────────────────────
        var valid   = new List<CsvDeviceRecord>();
        var invalid = new List<(CsvDeviceRecord rec, string error)>();

        foreach (var rec in records)
        {
            // Validate by attempting a dry parse (null current state → uses defaults)
            if (!rec.TryToParameters(current: null, out _, out _, out var fieldError))
                invalid.Add((rec, fieldError));
            else
                valid.Add(rec);
        }

        if (invalid.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[red]{invalid.Count} row(s) have invalid parameters:[/]");
            foreach (var (rec, err) in invalid)
                AnsiConsole.MarkupLine($"  [grey]row {rec.SourceRow}[/] [bold]{rec.Address}[/]: [red]{err}[/]");

            if (!s.SkipInvalid)
            {
                AnsiConsole.MarkupLine(
                    "\n[yellow]Aborting.[/] Fix the errors above or use [grey]--skip-invalid[/] to skip bad rows.");
                return 1;
            }

            AnsiConsole.MarkupLine("[yellow]Skipping invalid rows (--skip-invalid).[/]");
        }

        if (valid.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No valid rows to apply after filtering.[/]");
            return 1;
        }

        // ── Preview table ─────────────────────────────────────────────────
        AnsiConsole.WriteLine();
        RenderPreview(valid);

        if (s.DryRun)
        {
            AnsiConsole.MarkupLine($"[grey]Dry run — no commands sent.[/] " +
                                   $"[bold]{valid.Count}[/] row(s) would be applied.");
            return 0;
        }

        // ── Confirmation ──────────────────────────────────────────────────
        if (!s.Yes)
        {
            var confirmed = AnsiConsole.Confirm(
                $"Apply [bold]{valid.Count}[/] configuration(s) to hardware?",
                defaultValue: false);

            if (!confirmed)
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                return 0;
            }
        }

        // ── Connect ───────────────────────────────────────────────────────
        if (!_gateway.IsConnected)
        {
            AnsiConsole.MarkupLine("[yellow]Connecting to gateway...[/]");
            await _gateway.StartAsync(cts.Token);
        }

        // ── Apply ─────────────────────────────────────────────────────────
        AnsiConsole.WriteLine();
        CsvApplyResult result = null!;

        int applied = 0;
        var applyProgress = new Progress<int>(_ =>
        {
            var n = Interlocked.Increment(ref applied);
            Console.Write($"\r  Applying {n} / {valid.Count}...   ");
        });
        result = await _csv.ApplyAsync(valid, applyProgress, cts.Token);
        Console.WriteLine();

        // ── Summary ───────────────────────────────────────────────────────
        AnsiConsole.WriteLine();

        if (result.Applied > 0)
            AnsiConsole.MarkupLine($"[green]Applied:[/] [bold]{result.Applied}[/] module(s)");
        if (result.Failed > 0)
        {
            AnsiConsole.MarkupLine($"[red]Failed:[/]  [bold]{result.Failed}[/] module(s)");
            foreach (var err in result.Errors)
                AnsiConsole.MarkupLine($"  [red]{err}[/]");
        }

        return result.Failed > 0 ? 1 : 0;
    }

    // ─── Preview table ────────────────────────────────────────────────────────

    private static void RenderPreview(IReadOnlyList<CsvDeviceRecord> records)
    {
        const int MaxPreviewRows = 50;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title($"[bold]Import Preview[/] [grey]({records.Count} row(s))[/]")
            .AddColumn(new TableColumn("[bold]Addr[/]").Centered())
            .AddColumn(new TableColumn("[bold]Display[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Color[/]"))
            .AddColumn(new TableColumn("[bold]LED[/]"))
            .AddColumn(new TableColumn("[bold]SEG[/]"))
            .AddColumn(new TableColumn("[bold]Buz[/]"))
            .AddColumn(new TableColumn("[bold]After✓[/]"))
            .AddColumn(new TableColumn("[bold]Buz✓[/]"))
            .AddColumn(new TableColumn("[bold]Label[/]"));

        int shown = 0;
        foreach (var r in records)
        {
            if (shown >= MaxPreviewRows) break;

            table.AddRow(
                $"[bold]{r.Address}[/]",
                r.Display        is null ? "[grey]—[/]" : $"[white]{r.Display}[/]",
                FieldMarkup(r.Color),
                FieldMarkup(r.LedMode),
                FieldMarkup(r.SegMode),
                FieldMarkup(r.BuzMode),
                FieldMarkup(r.AfterConfirm),
                FieldMarkup(r.BuzAfterConfirm),
                r.Label is null ? "[grey][/]" : $"[grey]{r.Label}[/]");

            shown++;
        }

        if (records.Count > MaxPreviewRows)
            table.AddRow(
                $"[grey]… {records.Count - MaxPreviewRows} more rows not shown[/]",
                "", "", "", "", "", "", "", "");

        AnsiConsole.Write(table);
    }

    private static string FieldMarkup(string? value)
        => value is null ? "[grey]—[/]" : $"[white]{value}[/]";
}
