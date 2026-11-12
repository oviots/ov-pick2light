using P2L.Core;
using P2L.Hardware;
using P2L.Shared.Enums;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace P2L.CLI.Dashboard;

/// <summary>
/// Live Spectre.Console warehouse operator dashboard.
///
/// Displays:
///   • Gateway connection status header
///   • Light modules table with block / correlation ID / address / display / status
///   • Quick-help footer
/// </summary>
public sealed class WarehouseDashboard
{
    private readonly AioiGateway        _gateway;
    private readonly LightModuleRegistry _registry;

    private bool _running;

    public WarehouseDashboard(AioiGateway gateway, LightModuleRegistry registry)
    {
        _gateway  = gateway;
        _registry = registry;
    }

    // ─── Public ───────────────────────────────────────────────────────────

    public async Task RunAsync(CancellationToken ct)
    {
        _running = true;
        AnsiConsole.Clear();

        try
        {
            await AnsiConsole.Live(BuildLayout())
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .StartAsync(async ctx =>
                {
                    while (!ct.IsCancellationRequested && _running)
                    {
                        ctx.UpdateTarget(BuildLayout());
                        try { await Task.Delay(250, ct); }
                        catch (OperationCanceledException) { break; }
                    }
                });
        }
        catch (OperationCanceledException) { /* Ctrl+C — exit cleanly */ }
    }

    public void Stop() => _running = false;

    // ─── Layout construction ──────────────────────────────────────────────

    private IRenderable BuildLayout()
    {
        var grid = new Grid();
        grid.AddColumn();
        grid.AddRow(BuildHeader());
        grid.AddRow(BuildModulesTable());
        grid.AddRow(BuildFooter());
        return grid;
    }

    private IRenderable BuildHeader()
    {
        var connected = _gateway.IsConnected;
        var status = connected
            ? "[green bold]● CONNECTED[/]"
            : "[red bold]○ DISCONNECTED[/]";

        var active  = _registry.ActiveCount;
        var pending = _registry.PendingConfirmCount;

        return new Panel(
            new Markup($"  {status}  |  [yellow]Active:[/] {active}  |  [cyan]Awaiting terminate:[/] {pending}  |  {DateTime.Now:HH:mm:ss}"))
        {
            Header = new PanelHeader("[bold]Pick2Light (P2L) — Warehouse Control System[/]"),
            Border = BoxBorder.Heavy,
            BorderStyle = Style.Parse(connected ? "green" : "red"),
        };
    }

    private IRenderable BuildModulesTable()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[yellow bold]Light Modules[/]")
            .AddColumn(new TableColumn("[grey]Address[/]").Centered())
            .AddColumn(new TableColumn("[grey]Block No[/]").Centered())
            .AddColumn(new TableColumn("[grey]Correlation ID[/]"))
            .AddColumn(new TableColumn("[grey]Display[/]").Centered())
            .AddColumn(new TableColumn("[grey]Color[/]").Centered())
            .AddColumn(new TableColumn("[grey]Mode[/]").Centered())
            .AddColumn(new TableColumn("[grey]Status[/]").Centered())
            .AddColumn(new TableColumn("[grey]Last Confirmed[/]").RightAligned());

        var modules = _registry.All;
        if (modules.Count == 0)
        {
            // Supply one cell per column, including the session routing columns.
            table.AddRow(
                new Markup("[grey italic]No active modules[/]"),
                Text.Empty, Text.Empty, Text.Empty, Text.Empty, Text.Empty, Text.Empty, Text.Empty);
            return table;
        }

        foreach (var m in modules)
        {
            table.AddRow(
                new Markup($"[bold]{m.Address}[/]"),
                new Text(m.BlockNo),
                new Text(string.IsNullOrWhiteSpace(m.CorrelationId) ? "-" : m.CorrelationId),
                new Markup($"[white]{m.DisplayData.Trim()}[/]"),
                ColorMarkup(m.Color),
                new Markup($"[grey]{m.LedMode}[/]"),
                StatusMarkup(m.Status),
                new Markup(m.LastConfirmedAt.HasValue
                    ? $"[grey]{m.LastConfirmedAt.Value.ToLocalTime():HH:mm:ss}[/]"
                    : "[grey]-[/]"));
        }

        return table;
    }

    private static IRenderable BuildFooter() =>
        new Markup("[grey]  Ctrl+C[/] Quit  [grey]p[/] Start Picking  [grey]t[/] Terminate All  [grey]s[/] Status Inquiry\n");

    // ─── Markup helpers ────────────────────────────────────────────────────

    private static Markup ColorMarkup(LightColor color) => color switch
    {
        LightColor.Red     => new Markup("[red]■ Red[/]"),
        LightColor.Green   => new Markup("[green]■ Green[/]"),
        LightColor.Blue    => new Markup("[blue]■ Blue[/]"),
        LightColor.Yellow  => new Markup("[yellow]■ Yellow[/]"),
        LightColor.Cyan    => new Markup("[cyan]■ Cyan[/]"),
        LightColor.Magenta => new Markup("[magenta]■ Magenta[/]"),
        LightColor.White   => new Markup("[white]■ White[/]"),
        _                  => new Markup("[grey]○ Off[/]"),
    };

    private static Markup StatusMarkup(ModuleStatus status) => status switch
    {
        ModuleStatus.Operating   => new Markup("[yellow bold]⚡ Active[/]"),
        ModuleStatus.Completed   => new Markup("[green bold]✓ Confirmed[/]"),
        ModuleStatus.Shortage    => new Markup("[orange1 bold]⚠ Shortage[/]"),
        ModuleStatus.Standby     => new Markup("[grey]— Standby[/]"),
        ModuleStatus.Malfunction => new Markup("[red bold]✗ FAULT[/]"),
        _                        => new Markup("[grey]?[/]"),
    };

}
