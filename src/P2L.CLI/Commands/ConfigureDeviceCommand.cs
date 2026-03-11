using P2L.Core;
using P2L.Shared.Enums;
using P2L.Shared.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace P2L.CLI.Commands;

/// <summary>
/// Adjusts the lighting and display parameters of an active (or standby) module in-place.
///
/// Usage:
///   p2l light set 0001                             show current parameters (read-only)
///   p2l light set 0001 --color Red --mode Flash    change colour and LED mode
///   p2l light set 0001 --display 42                update displayed quantity only
///   p2l light set 0001 --buz On --after-confirm Off --buz-confirm On
///
/// Any option not supplied retains its current value from the registry.
/// When the module is not yet tracked a sensible default is used.
/// </summary>
public sealed class ConfigureDeviceCommand : AsyncCommand<ConfigureDeviceCommand.Settings>
{
    private readonly PickingEngine       _engine;
    private readonly LightModuleRegistry _registry;

    public ConfigureDeviceCommand(PickingEngine engine, LightModuleRegistry registry)
    {
        _engine   = engine;
        _registry = registry;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<address>")]
        [Description("4-digit module address, e.g. 0001")]
        public string Address { get; init; } = "";

        [CommandOption("--color")]
        [Description("LED colour: Off Red Green Blue Yellow Cyan Magenta White")]
        public string? Color { get; init; }

        [CommandOption("-m|--mode")]
        [Description("LED mode during instruction: On Flash HighFlash")]
        public string? Mode { get; init; }

        [CommandOption("--seg")]
        [Description("7-segment display mode: On Off Flash")]
        public string? Seg { get; init; }

        [CommandOption("--buz")]
        [Description("Buzzer during instruction: Off On Flash")]
        public string? Buz { get; init; }

        [CommandOption("--after-confirm")]
        [Description("LED behaviour after CONFIRM press: Off On Flash NoChange")]
        public string? AfterConfirm { get; init; }

        [CommandOption("--buz-confirm")]
        [Description("Buzzer after CONFIRM press: Off On Flash")]
        public string? BuzConfirm { get; init; }

        [CommandOption("-d|--display")]
        [Description("New quantity to show on 7-segment display")]
        public int? Display { get; init; }

        /// <summary>True when at least one parameter change was requested.</summary>
        public bool HasChanges =>
            Color is not null || Mode is not null || Seg is not null ||
            Buz is not null || AfterConfirm is not null || BuzConfirm is not null ||
            Display.HasValue;

        public override ValidationResult Validate()
        {
            var norm = Address.Trim().PadLeft(4, '0');
            if (norm.Length != 4 || !norm.All(char.IsAsciiDigit))
                return ValidationResult.Error("Address must be a 1–4 digit number.");

            if (Color is not null && ParseLightColor(Color) is null)
                return ValidationResult.Error(
                    $"Unknown colour '{Color}'. Valid: Off Red Green Blue Yellow Cyan Magenta White");

            foreach (var (opt, name) in new[] {
                (Mode, "--mode"), (Seg, "--seg"), (Buz, "--buz"),
                (AfterConfirm, "--after-confirm"), (BuzConfirm, "--buz-confirm") })
            {
                if (opt is not null && ParseLightState(opt) is null)
                    return ValidationResult.Error(
                        $"Unknown value '{opt}' for {name}. Valid: Off On Flash HighFlash NoChange");
            }

            return ValidationResult.Success();
        }
    }

    // ─── Execution ────────────────────────────────────────────────────────

    public override async Task<int> ExecuteAsync(CommandContext ctx, Settings s)
    {
        var addr    = s.Address.Trim().PadLeft(4, '0');
        var current = _registry.Get(addr);

        // ── Read-only display when no changes requested ────────────────────
        if (!s.HasChanges)
        {
            RenderCurrentParameters(addr, current);
            return 0;
        }

        // ── Derive baseline from registry (or defaults) ────────────────────
        var baseline = current is null
            ? new ModuleParameters()
            : new ModuleParameters
            {
                Color           = current.Color,
                LedMode         = current.LedMode,
                SegMode         = current.SegMode,
                BuzMode         = current.BuzMode,
                AfterConfirm    = LightState.Off,    // not stored; default to Off
                BuzAfterConfirm = LightState.Off,
            };

        // ── Merge command-line overrides ───────────────────────────────────
        var updated = baseline.With(
            color:           s.Color        is null ? null : ParseLightColor(s.Color),
            ledMode:         s.Mode         is null ? null : ParseLightState(s.Mode),
            segMode:         s.Seg          is null ? null : ParseLightState(s.Seg),
            buzMode:         s.Buz          is null ? null : ParseLightState(s.Buz),
            afterConfirm:    s.AfterConfirm is null ? null : ParseLightState(s.AfterConfirm),
            buzAfterConfirm: s.BuzConfirm   is null ? null : ParseLightState(s.BuzConfirm));

        // ── Show before / after summary ────────────────────────────────────
        RenderDiff(addr, baseline, updated, current?.DisplayData, s.Display);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            await _engine.ConfigureModuleAsync(addr, s.Display, updated, cts.Token);
            AnsiConsole.MarkupLine($"\n[green]Module [bold]{addr}[/] reconfigured.[/]");
            return 0;
        }
        catch (TimeoutException ex)
        {
            AnsiConsole.MarkupLine($"[red]Timeout:[/] {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    // ─── Rendering ────────────────────────────────────────────────────────

    private static void RenderCurrentParameters(string addr, LightModuleState? state)
    {
        if (state is null)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Module [bold]{addr}[/] is not in the registry.[/] " +
                $"Run [grey]p2l devices[/] or use [grey]p2l light on[/] first.");
            return;
        }

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]Parameter[/]",     "[grey]Value[/]");
        grid.AddRow("Address",    $"[bold]{state.Address}[/]");
        grid.AddRow("Status",     StatusMarkup(state.Status));
        grid.AddRow("Color",      ColorMarkup(state.Color));
        grid.AddRow("LED mode",   ModeMarkup(state.LedMode));
        grid.AddRow("SEG mode",   ModeMarkup(state.SegMode));
        grid.AddRow("Buzzer",     ModeMarkup(state.BuzMode));
        grid.AddRow("Display",    $"[white]{state.DisplayData.TrimStart()}[/]");
        if (state.LastInstructionAt.HasValue)
            grid.AddRow("Last set", $"[grey]{state.LastInstructionAt.Value:HH:mm:ss UTC}[/]");

        AnsiConsole.Write(
            new Panel(grid)
                .Header($"[bold]Module {state.Address} — current parameters[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey));
    }

    private static void RenderDiff(
        string addr,
        ModuleParameters before,
        ModuleParameters after,
        string? currentDisplay,
        int? newDisplay)
    {
        string beforeQty = currentDisplay?.TrimStart() ?? "—";
        string afterQty  = newDisplay.HasValue ? newDisplay.Value.ToString() : beforeQty;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title($"[bold]Configuring module [cyan]{addr}[/][/]")
            .AddColumn(new TableColumn("[grey]Parameter[/]"))
            .AddColumn(new TableColumn("[grey]Before[/]").Centered())
            .AddColumn(new TableColumn("[grey]After[/]").Centered());

        AddDiffRow(table, "Color",        ColorMarkup(before.Color),           ColorMarkup(after.Color),
                   before.Color           != after.Color);
        AddDiffRow(table, "LED mode",     ModeMarkup(before.LedMode),          ModeMarkup(after.LedMode),
                   before.LedMode         != after.LedMode);
        AddDiffRow(table, "SEG mode",     ModeMarkup(before.SegMode),          ModeMarkup(after.SegMode),
                   before.SegMode         != after.SegMode);
        AddDiffRow(table, "Buzzer",       ModeMarkup(before.BuzMode),          ModeMarkup(after.BuzMode),
                   before.BuzMode         != after.BuzMode);
        AddDiffRow(table, "After-CONFIRM",ModeMarkup(before.AfterConfirm),     ModeMarkup(after.AfterConfirm),
                   before.AfterConfirm    != after.AfterConfirm);
        AddDiffRow(table, "Buz-CONFIRM",  ModeMarkup(before.BuzAfterConfirm),  ModeMarkup(after.BuzAfterConfirm),
                   before.BuzAfterConfirm != after.BuzAfterConfirm);
        AddDiffRow(table, "Display",
                   $"[grey]{beforeQty}[/]",
                   $"[white]{afterQty}[/]",
                   beforeQty != afterQty);

        AnsiConsole.Write(table);
    }

    private static void AddDiffRow(Table t, string param, string before, string after, bool changed)
    {
        var afterCell = changed ? $"[bold]{after}[/]" : $"[grey]{after}[/]";
        var arrow     = changed ? "[yellow]→[/]" : "[grey]·[/]";
        t.AddRow(param, before, $"{arrow} {afterCell}");
    }

    // ─── Parse helpers ────────────────────────────────────────────────────

    internal static LightColor? ParseLightColor(string? s) => s?.ToLowerInvariant() switch
    {
        "off"     => LightColor.Off,
        "red"     => LightColor.Red,
        "green"   => LightColor.Green,
        "blue"    => LightColor.Blue,
        "yellow"  => LightColor.Yellow,
        "cyan"    => LightColor.Cyan,
        "magenta" => LightColor.Magenta,
        "white"   => LightColor.White,
        _         => null,
    };

    internal static LightState? ParseLightState(string? s) => s?.ToLowerInvariant() switch
    {
        "off"                    => LightState.Off,
        "on"                     => LightState.On,
        "flash"                  => LightState.Flash,
        "highflash" or "hiflash" => LightState.HighFlash,
        "nochange" or "nc"       => LightState.NoChange,
        _                        => null,
    };

    // ─── Markup helpers (shared style with ListDevicesCommand) ────────────

    private static string StatusMarkup(ModuleStatus s) => s switch
    {
        ModuleStatus.Standby     => "[green]Standby[/]",
        ModuleStatus.Operating   => "[yellow]⚡ Active[/]",
        ModuleStatus.Completed   => "[cyan]✓ Done[/]",
        ModuleStatus.Shortage    => "[darkorange]⚠ Shortage[/]",
        ModuleStatus.Maintenance => "[blue]Maintenance[/]",
        ModuleStatus.Malfunction => "[red]✗ Fault[/]",
        _                        => "[grey]Unknown[/]",
    };

    private static string ColorMarkup(LightColor c) => c switch
    {
        LightColor.Red     => "[red]Red[/]",
        LightColor.Green   => "[green]Green[/]",
        LightColor.Blue    => "[blue]Blue[/]",
        LightColor.Yellow  => "[yellow]Yellow[/]",
        LightColor.Cyan    => "[cyan]Cyan[/]",
        LightColor.Magenta => "[magenta]Magenta[/]",
        LightColor.White   => "[white]White[/]",
        _                  => "[grey]Off[/]",
    };

    private static string ModeMarkup(LightState m) => m switch
    {
        LightState.On        => "[green]On[/]",
        LightState.Flash     => "[yellow]Flash[/]",
        LightState.HighFlash => "[yellow]HighFlash[/]",
        LightState.NoChange  => "[grey]NoChange[/]",
        _                    => "[grey]Off[/]",
    };
}
