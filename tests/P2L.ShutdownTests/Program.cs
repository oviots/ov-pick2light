using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using P2L.CLI.Commands;
using P2L.CLI.Configuration;
using P2L.CLI.Infrastructure;
using P2L.CLI.Web;
using P2L.Core;
using P2L.Core.Services;
using P2L.Hardware;
using P2L.Hardware.Transport;
using P2L.Mqtt;
using P2L.Mqtt.Configuration;
using P2L.Shared;
using P2L.Shared.Models;
using Spectre.Console.Cli;

if (args.FirstOrDefault() == "--child") return await RunChild(args);

static void Require(bool test, string message) { if (!test) throw new Exception(message); }
// Exercise the exact cancelled-reader case that previously threw during transport disposal.
var idle = new TcpTransport("127.0.0.1", 1, NullLogger.Instance);
await idle.StartAsync();
await Task.Delay(50);
await idle.DisposeAsync();
await idle.DisposeAsync();
Console.WriteLine("PASS: cancelled transport readers dispose without throwing; repeated disposal is safe");

foreach (var signal in new[] {"INT", "TERM"})
foreach (var connected in new[] {false, true})
{
    var directory = Path.Combine(Path.GetTempPath(), "p2l-shutdown-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var gateway = new TcpListener(IPAddress.Loopback, 0); gateway.Start();
    var port = ((IPEndPoint)gateway.LocalEndpoint).Port;
    if (!connected) gateway.Stop();
    var mqtt = new TcpListener(IPAddress.Loopback, 0); mqtt.Start(); // accepts TCP but never MQTT CONNACK
    var mqttPort = ((IPEndPoint)mqtt.LocalEndpoint).Port;
    var ui = new TcpListener(IPAddress.Loopback, 0); ui.Start(); var uiPort = ((IPEndPoint)ui.LocalEndpoint).Port; ui.Stop();
    var info = new ProcessStartInfo("dotnet") {RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false};
    foreach (var arg in new[] {typeof(ServeCommand).Assembly.Location, "--child", directory, port.ToString(), mqttPort.ToString(), uiPort.ToString()}) info.ArgumentList.Add(arg);
    using var process = Process.Start(info)!;
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
    process.OutputDataReceived += (_, e) => { if (e.Data is { } line) {output.Enqueue(line); if (line.Contains("Awaiting stop signal")) ready.TrySetResult();} };
    process.ErrorDataReceived += (_, e) => {if (e.Data is { } line) output.Enqueue(line);};
    process.BeginOutputReadLine(); process.BeginErrorReadLine();
    TcpClient? controller = null;
    using var browserSocket = new System.Net.WebSockets.ClientWebSocket();
    try
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (connected) controller = await gateway.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await browserSocket.ConnectAsync(new Uri($"ws://127.0.0.1:{uiPort}/ws"), CancellationToken.None);
        var elapsed = Stopwatch.StartNew();
        using var kill = Process.Start(new ProcessStartInfo("/bin/kill") {ArgumentList = {"-" + signal, process.Id.ToString()}})!;
        await kill.WaitForExitAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Require(process.ExitCode == 0, $"{signal} exited {process.ExitCode}: " + string.Join('\n', output));
        Require(elapsed.Elapsed < TimeSpan.FromSeconds(8), "Shutdown took too long");
        Require(File.ReadAllLines(Path.Combine(directory, "starts.txt")).Length == 1, "Self-heal spawned a replacement");
        Require(!File.Exists(Path.Combine(directory, "crash_history.jsonl")), "Clean shutdown was recorded as a crash");
        Require(new ConfirmationOutbox(Path.Combine(directory, "confirms.db")).ReadPending().Count == 1, "Shutdown lost the unacknowledged Confirm");
        if (controller is not null)
        {
            var buffer = new byte[1];
            Require(await controller.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(2)) == 0, "Controller socket left open");
        }
        Console.WriteLine($"PASS: {signal}, controller {(connected ? "connected" : "offline")}, MQTT connecting, browser WebSocket open: clean exit, no restart, sockets closed");
    }
    finally
    {
        if (!process.HasExited) {process.Kill(entireProcessTree: true); await process.WaitForExitAsync();}
        controller?.Dispose(); gateway.Stop(); mqtt.Stop(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true);
    }
}
Console.WriteLine("All shutdown checks passed. Child fixtures only; no license API, vault or physical controller accessed.");
return 0;

static async Task<int> RunChild(string[] arguments)
{
    var directory = arguments[1];
    File.AppendAllText(Path.Combine(directory, "starts.txt"), Environment.ProcessId + "\n");
    SelfHealHandler.Install(new SelfHealSettings {Enabled = true, BackOffSeconds = 0}, arguments, directory);
    using var cts = new CancellationTokenSource();
    using var signals = new ShutdownSignals(cts);
    var settings = new AppSettings {Mqtt = new MqttSettings {Host = "127.0.0.1", Port = int.Parse(arguments[3]),
        Enabled = true, Tls = new MqttTlsSettings {Enabled = false}, ClientDbPath = Path.Combine(directory, "devices.db"), OfflineBufferPath = Path.Combine(directory, "events.jsonl")}};
    var services = new ServiceCollection();
    services.AddSingleton(cts); services.AddSingleton(settings);
    services.AddLogging(builder => builder.AddSimpleConsole(options => options.SingleLine = true));
    services.AddSingleton(sp => new AioiGateway("127.0.0.1", int.Parse(arguments[2]), sp.GetRequiredService<ILogger<AioiGateway>>()));
    services.AddSingleton<LightModuleRegistry>(); services.AddSingleton<PickingEngine>();
    services.AddSingleton<HeartbeatService>(); services.AddSingleton<WatchdogService>(); services.AddSingleton<PreemptionGate>();
    var outbox = new ConfirmationOutbox(Path.Combine(directory, "confirms.db"));
    outbox.Record(new ConfirmationEvent {Address="0001"}, new LightModuleState {Address="0001",BlockNo="01",CorrelationId="SHUTDOWN-TEST"});
    services.AddSingleton(outbox);
    services.AddSingleton<IConfirmationJournal>(sp=>sp.GetRequiredService<ConfirmationOutbox>());
    services.AddSingleton<DeviceScanService>(); services.AddSingleton(new WebLogProvider());
    services.AddSingleton(sp => new DeviceLabelStore(settings.Mqtt.ClientDbPath, sp.GetRequiredService<ILogger<DeviceLabelStore>>()));
    services.AddSingleton(sp => new MqttBridgeService(settings.Mqtt, sp.GetRequiredService<AioiGateway>(), sp.GetRequiredService<PickingEngine>(),
        sp.GetRequiredService<LightModuleRegistry>(), sp.GetRequiredService<PreemptionGate>(), null, sp.GetRequiredService<DeviceLabelStore>(),
        sp.GetRequiredService<DeviceScanService>(), sp.GetRequiredService<ILoggerFactory>(), confirmations:sp.GetRequiredService<ConfirmationOutbox>()));
    services.AddSingleton<DeviceCatalog>(sp => new DeviceCatalog(settings.Mqtt.ClientDbPath, sp.GetRequiredService<DeviceLabelStore>(), sp.GetRequiredService<LightModuleRegistry>()));
    services.AddSingleton(sp => new WebClientService(settings, sp.GetRequiredService<AioiGateway>(), sp.GetRequiredService<LightModuleRegistry>(),
        sp.GetRequiredService<PickingEngine>(), sp.GetRequiredService<DeviceCatalog>(), sp.GetRequiredService<WebLogProvider>(),
        sp.GetRequiredService<MqttBridgeService>().HandleLocalCommandAsync, () => false, sp.GetRequiredService<ILogger<WebClientService>>()));
    var registrar = new TypeRegistrar(services);
    var app = new CommandApp<ServeCommand>(registrar);
    try { return await app.RunAsync(new[] {"--no-browser", "--ui-port", arguments[4]}); }
    finally
    {
        SelfHealHandler.BeginShutdown();
        await cts.CancelAsync();
        await registrar.DisposeAsync();
        SelfHealHandler.TriggerWatchdog("shutdown must never restart");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
