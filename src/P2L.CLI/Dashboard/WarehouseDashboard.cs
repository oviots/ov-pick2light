using P2L.Core;
using P2L.Hardware;
using P2L.Shared.Enums;
using P2L.Shared.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace P2L.CLI.Dashboard;

/// <summary>
/// Live Spectre.Console warehouse operator dashboard.
///
/// Displays:
///   • Gateway connection status header
///   • Active light modules table with address / display / color / status
///   • Recent confirmation event log
///   • Quick-help footer
/// </summary>
public sealed class WarehouseDashboard
{
    private readonly AioiGateway        _gateway;
    private readonly LightModuleRegistry _registry;

    private readonly Queue<string> _eventLog     = new(capacity: 20);
    private readonly object        _eventLogLock = new();
    private bool _running;

    public WarehouseDashboard(AioiGateway gateway, LightModuleRegistry registry)
    {
        _gateway  = gateway;
        _registry = registry;

        _gateway.Confirmed        += OnConfirmed;
        _gateway.ConnectionChanged += OnConnectionChanged;
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
        grid.AddRow(BuildEventLog());
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
            .AddColumn(new TableColumn("[grey]Display[/]").Centered())
            .AddColumn(new TableColumn("[grey]Color[/]").Centered())
            .AddColumn(new TableColumn("[grey]Mode[/]").Centered())
            .AddColumn(new TableColumn("[grey]Status[/]").Centered())
            .AddColumn(new TableColumn("[grey]Last Confirmed[/]").RightAligned());

        var modules = _registry.All;
        if (modules.Count == 0)
        {
            // Must supply exactly 6 cells (one per column) — Spectre throws on mismatch
            table.AddRow(
                new Markup("[grey italic]No active modules[/]"),
                Text.Empty, Text.Empty, Text.Empty, Text.Empty, Text.Empty);
            return table;
        }

        foreach (var m in modules)
        {
            table.AddRow(
                new Markup($"[bold]{m.Address}[/]"),
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

    private IRenderable BuildEventLog()
    {
        string[] snapshot;
        lock (_eventLogLock)
            snapshot = _eventLog.ToArray();

        var panel = new Panel(
            new Markup(string.Join("\n",
                snapshot.Reverse().Take(10))))
        {
            Header      = new PanelHeader("[bold]Recent Events[/]"),
            Border      = BoxBorder.Rounded,
            Expand      = true,
            Padding     = new Padding(1, 0),
        };
        return panel;
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

    // ─── Event handlers ────────────────────────────────────────────────────

    private void OnConfirmed(ConfirmationEvent evt)
    {
        var icon = evt.Status switch
        {
            CompletionStatus.Normal   => "[green]✓[/]",
            CompletionStatus.Shortage => "[orange1]⚠[/]",
            _                         => "[red]✗[/]",
        };
        var adj = evt.AdjustedDisplay is not null ? $" (qty={evt.AdjustedDisplay.Trim()})" : "";
        lock (_eventLogLock)
        {
            _eventLog.Enqueue($"{DateTime.Now:HH:mm:ss} {icon} CONFIRM addr=[bold]{evt.Address}[/]{adj}");
            if (_eventLog.Count > 20) _eventLog.Dequeue();
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        var msg = connected ? "[green]Gateway connected[/]" : "[red]Gateway disconnected[/]";
        lock (_eventLogLock)
        {
            _eventLog.Enqueue($"{DateTime.Now:HH:mm:ss} {msg}");
            if (_eventLog.Count > 20) _eventLog.Dequeue();
        }
    }
}
