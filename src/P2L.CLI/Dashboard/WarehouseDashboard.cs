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
    private readonly IAnsiConsole _console;

    private bool _running;
    private int _scrollOffset;
    private int _pageSize = 1;
    private int _moduleCount;

    public WarehouseDashboard(AioiGateway gateway, LightModuleRegistry registry, IAnsiConsole? console = null)
    {
        _gateway  = gateway;
        _registry = registry;
        _console = console ?? AnsiConsole.Console;
    }

    // ─── Public ───────────────────────────────────────────────────────────

    public async Task RunAsync(CancellationToken ct)
    {
        _running = true;
        // Alternate-screen scrolling can arrive as arrow keys. Initialize Console's
        // input reader before drawing so Unix disables terminal echo; otherwise the
        // echoed escape sequences move Live's cursor and corrupt subsequent frames.
        var readInput = !Console.IsInputRedirected;
        if (readInput) DrainInput();
        var alternate = _console.Profile.Capabilities.Ansi && _console.Profile.Capabilities.AlternateBuffer;
        var output = _console.Profile.Out.Writer;
        if (alternate) { output.Write("\u001b[?1049h\u001b[?1007h"); output.Flush(); }
        if (alternate) { output.Write("\u001b[?25l"); output.Flush(); }
        try
        {
            while (!ct.IsCancellationRequested && _running)
            {
                if (readInput) DrainInput();
                var width = _console.Profile.Width;
                var height = _console.Profile.Height;
                // Repaint from a fixed origin. Input or terminal resizing must not
                // invalidate a cached relative cursor position between frames.
                if (_console.Profile.Capabilities.Ansi)
                {
                    output.Write("\u001b[?2026h\u001b[H\u001b[2J");
                    try { _console.Write(BuildLayout(width, height)); }
                    finally { output.Write("\u001b[?2026l"); output.Flush(); }
                }
                else _console.Write(BuildLayout(width, height));
                try { await Task.Delay(250, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* Ctrl+C — exit cleanly */ }
        finally
        {
            if (alternate) { output.Write("\u001b[?25h\u001b[?1007l\u001b[?1049l"); output.Flush(); }
        }
    }

    public void Stop() => _running = false;

    private void DrainInput()
    {
        // Ctrl+C stays with the host cancellation handler. Do not echo navigation
        // keys or let a burst of wheel events indefinitely postpone a redraw.
        for (var count = 0; count < 128 && Console.KeyAvailable; count++)
            Navigate(Console.ReadKey(intercept: true).Key);
    }

    internal void Navigate(ConsoleKey key)
    {
        var lastPage = Math.Max(0, _moduleCount - _pageSize);
        _scrollOffset = key switch
        {
            ConsoleKey.UpArrow => Math.Max(0, _scrollOffset - 1),
            ConsoleKey.DownArrow => Math.Min(lastPage, _scrollOffset + 1),
            ConsoleKey.PageUp => Math.Max(0, _scrollOffset - _pageSize),
            ConsoleKey.PageDown => Math.Min(lastPage, _scrollOffset + _pageSize),
            ConsoleKey.Home => 0,
            ConsoleKey.End => lastPage,
            _ => _scrollOffset,
        };
    }

    // ─── Layout construction ──────────────────────────────────────────────

    internal IRenderable BuildLayout(int terminalWidth, int terminalHeight = int.MaxValue)
    {
        // Leave the last terminal column free to avoid automatic line wrapping.
        var width = Math.Max(1, terminalWidth - 1);
        var modules = _registry.All;
        _moduleCount = modules.Count;
        var header = BuildHeader(width);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, modules.Count - _pageSize));
        if (terminalHeight == int.MaxValue)
            _pageSize = Math.Max(1, modules.Count);
        else
        {
            // Measure the actual layout: narrow cards and wrapped text consume
            // more rows than a wide table. Only render a screenful of modules.
            var footer = BuildFooter(width, modules.Count, modules.Count, modules.Count);
            var available = Math.Max(1, terminalHeight - RenderHeight(header, width) - RenderHeight(footer, width) - 1);
            var low = 1;
            var high = Math.Min(Math.Max(1, modules.Count - _scrollOffset), available);
            while (low < high)
            {
                var count = (low + high + 1) / 2;
                var view = BuildModulesTable(width, modules.Skip(_scrollOffset).Take(count).ToArray());
                if (RenderHeight(view, width) <= available) low = count;
                else high = count - 1;
            }
            _pageSize = low;
        }
        var visible = modules.Skip(_scrollOffset).Take(_pageSize).ToArray();
        return new Rows(header, BuildModulesTable(width, visible),
            BuildFooter(width, modules.Count, modules.Count == 0 ? 0 : _scrollOffset + 1, _scrollOffset + visible.Length));
    }

    private int RenderHeight(IRenderable renderable, int width) => Segment.SplitLines(
        renderable.Render(new RenderOptions(_console.Profile.Capabilities, new Size(width, _console.Profile.Height)), width)).Count();

    private IRenderable BuildHeader(int width)
    {
        var connected = _gateway.IsConnected;
        var status = connected
            ? "[green bold]● CONNECTED[/]"
            : "[red bold]○ DISCONNECTED[/]";

        var active  = _registry.ActiveCount;
        var pending = _registry.PendingConfirmCount;

        return new Panel(
            new Markup($"  {status}  |  [yellow]Active:[/] {active}  |  [cyan]Confirmed:[/] {pending}  |  {DateTime.Now:HH:mm:ss}"))
        {
            Header = new PanelHeader(width >= 75 ? "[bold]Pick2Light (P2L) — Warehouse Control System[/]" : "[bold]Pick2Light (P2L)[/]"),
            Width = width,
            Border = BoxBorder.Heavy,
            BorderStyle = Style.Parse(connected ? "green" : "red"),
        };
    }

    private IRenderable BuildModulesTable(int width, IReadOnlyList<P2L.Shared.Models.LightModuleState> modules)
    {
        var extraSessionWidth = Math.Max(0, modules.Select(m => m.CorrelationId?.Length ?? 0).DefaultIfEmpty().Max() - 14);
        if (width < 72 + extraSessionWidth) return BuildModuleCards(modules, width);
        var compact = width < 115 + extraSessionWidth;
        var table = new Table().Border(TableBorder.Rounded).Title("[yellow bold]Light Modules[/]");
        table.Width = width;
        table.AddColumn(new TableColumn(compact ? "Addr" : "Address").Centered().NoWrap());
        table.AddColumn(new TableColumn(compact ? "Block" : "Block No").Centered().NoWrap());
        table.AddColumn(new TableColumn(compact ? "Session" : "Correlation ID").NoWrap());
        table.AddColumn(new TableColumn("Display").Centered().NoWrap());
        if (!compact)
        {
            table.AddColumn(new TableColumn("Color").Centered().NoWrap());
            table.AddColumn(new TableColumn("Mode").Centered().NoWrap());
        }
        table.AddColumn(new TableColumn("Status").Centered().NoWrap());
        if (!compact) table.AddColumn(new TableColumn("Last Confirmed").RightAligned().NoWrap());
        if (modules.Count == 0)
        {
            var cells = Enumerable.Repeat<IRenderable>(Text.Empty, compact ? 5 : 8).ToArray();
            cells[0] = new Text("No modules");
            table.AddRow(cells);
        }
        foreach (var module in modules)
        {
            var cells = new List<IRenderable>
            {
                new Text(module.Address), new Text(module.BlockNo),
                new Text(string.IsNullOrWhiteSpace(module.CorrelationId) ? "-" : module.CorrelationId),
                new Text(module.DisplayData.Trim()),
            };
            if (!compact) { cells.Add(ColorMarkup(module.Color)); cells.Add(new Text(module.LedMode.ToString())); }
            cells.Add(StatusMarkup(module.Status));
            if (!compact) cells.Add(new Text(LastConfirmed(module)));
            table.AddRow(cells.ToArray());
        }
        return table;
    }

    private static IRenderable BuildModuleCards(IReadOnlyList<P2L.Shared.Models.LightModuleState> modules, int width)
    {
        if (modules.Count == 0) return new Text("No modules");
        return new Rows(modules.Select(module => new Panel(new Rows(
            new Text($"Session: {module.CorrelationId ?? "-"}"),
            new Text($"Display: {module.DisplayData.Trim()} | {module.Color}/{module.LedMode}"),
            StatusMarkup(module.Status),
            new Text($"Last Confirmed: {LastConfirmed(module)}")))
        {
            Header = new PanelHeader($"{module.Address}  Block {module.BlockNo}"),
            Border = BoxBorder.Rounded,
            Width = width,
        }));
    }

    private static string LastConfirmed(P2L.Shared.Models.LightModuleState module) =>
        module.LastConfirmedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "-";

    private static IRenderable BuildFooter(int width, int total, int first, int last) => new Rows(
        new Text($"Lights {first}-{last}/{total}"),
        new Markup(width >= 75
            ? "[grey]Wheel / ↑↓[/] Scroll | [grey]PgUp/PgDn[/] Page | [grey]Home/End[/] | [grey]Ctrl+C[/] Quit"
            : "[grey]↑↓ PgUp/PgDn Home/End[/] Scroll | [grey]Ctrl+C[/] Quit"));

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
