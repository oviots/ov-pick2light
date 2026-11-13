using Microsoft.Extensions.Logging.Abstractions;
using P2L.CLI.Dashboard;
using P2L.Core;
using P2L.Hardware;
using P2L.Shared.Enums;
using P2L.Shared.Models;
using Spectre.Console;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
await using var gateway = new AioiGateway("127.0.0.1", 1, NullLogger.Instance);
var registry = new LightModuleRegistry();
foreach (var address in new[] {"0008", "0014", "0017"}) registry.SetInstructed(new PickingInstruction {
    Address = address, BlockNo = address == "0017" ? "02" : "01",
    CorrelationId = address == "0017" ? "430200000007" : "430100000007",
    Quantity = 3, Color = LightColor.Green, LedMode = LightState.Flash,
});
if (args.Contains("--live"))
{
    if (args.Contains("--many"))
        for (var i = 2000; i < 2400; i++) registry.SetInstructed(new PickingInstruction {
            Address = i.ToString(), BlockNo = i % 2 == 0 ? "01" : "02", CorrelationId = "SCROLL-TEST", Quantity = 3,
        });
    using var stop = new CancellationTokenSource(5000);
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
    Console.CancelKeyPress += cancel;
    try { await new WarehouseDashboard(gateway, registry).RunAsync(stop.Token); }
    finally { Console.CancelKeyPress -= cancel; }
    return;
}
foreach (var width in new[] {180, 120, 100, 80, 72, 60, 40, 32, 100, 180})
{
    using var writer = new StringWriter();
    var console = AnsiConsole.Create(new AnsiConsoleSettings {
        Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Out = new AnsiConsoleOutput(writer),
    });
    console.Profile.Width = width;
    var dashboard = new WarehouseDashboard(gateway, registry, console);
    console.Write(dashboard.BuildLayout(width));
    var rendered = writer.ToString();
    foreach (var line in rendered.Split('\n')) Check(line.TrimEnd('\r').Length <= width, $"width {width} overflow: {line}");
    Check(rendered.Contains("0008") && rendered.Contains("0014") && rendered.Contains("0017"), $"width {width} lost module");
    Check(rendered.Contains("430100000007") && rendered.Contains("430200000007"), $"width {width} wrapped or lost correlation ID");
    Check(rendered.Contains("Block"), $"width {width} lost Block No");
    File.WriteAllText(Path.Combine(Path.GetTempPath(), $"p2l-dashboard-{width}.txt"), rendered);
}
Console.WriteLine("PASS terminal widths 32–180 retain addresses, block and correlation ID without horizontal overflow");
var empty = new LightModuleRegistry();
using var emptyWriter = new StringWriter();
var emptyConsole = AnsiConsole.Create(new AnsiConsoleSettings {Ansi=AnsiSupport.No,Out=new AnsiConsoleOutput(emptyWriter)});
emptyConsole.Profile.Width=80;
emptyConsole.Write(new WarehouseDashboard(gateway,empty,emptyConsole).BuildLayout(80));
Check(emptyWriter.ToString().Contains("No modules"),"empty table layout failed");
Console.WriteLine("PASS empty dashboard renders without a column mismatch");

var many = new LightModuleRegistry();
var addresses = Enumerable.Range(2000, 120).Select(i => i.ToString()).ToArray();
foreach (var address in addresses) many.SetInstructed(new PickingInstruction {
    Address = address, BlockNo = "01", CorrelationId = "430100000007", Quantity = 3,
});
foreach (var width in new[] {32, 40, 80, 120, 180})
foreach (var height in new[] {16, 24, 40})
{
    using var writer = new StringWriter();
    var console = AnsiConsole.Create(new AnsiConsoleSettings {Ansi=AnsiSupport.No, Out=new AnsiConsoleOutput(writer)});
    console.Profile.Width = width;
    console.Profile.Height = height;
    var dashboard = new WarehouseDashboard(gateway, many, console);
    string Render()
    {
        writer.GetStringBuilder().Clear();
        console.Write(dashboard.BuildLayout(width, height));
        var output = writer.ToString();
        Check(output.TrimEnd('\r', '\n').Split('\n').Length <= height, $"{width}x{height} vertical overflow");
        return output;
    }
    var seen = new HashSet<string>();
    for (var page = 0; page < addresses.Length; page++)
    {
        var output = Render();
        foreach (var address in addresses.Where(output.Contains)) seen.Add(address);
        if (output.Contains(addresses[^1])) break;
        dashboard.Navigate(ConsoleKey.PageDown);
    }
    Check(seen.Count == addresses.Length, $"{width}x{height} paging skipped {addresses.Length-seen.Count} lights");
    dashboard.Navigate(ConsoleKey.Home);
    Check(Render().Contains(addresses[0]), "Home did not return to first light");
    dashboard.Navigate(ConsoleKey.DownArrow);
    Check(!Render().Contains(addresses[0]), "Down/wheel did not scroll");
    dashboard.Navigate(ConsoleKey.End);
    Check(Render().Contains(addresses[^1]), "End did not reach last light");
    dashboard.Navigate(ConsoleKey.UpArrow);
    Check(!Render().Contains(addresses[^1]), "Up/wheel did not scroll");
}
Console.WriteLine("PASS 120 lights remain reachable by scrolling and paging at widths 32–180 / heights 16–40");
