using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using P2L.CLI.Commands;
using P2L.CLI.Configuration;
using P2L.CLI.Infrastructure;
using P2L.Core;
using P2L.Core.Services;
using P2L.Hardware;
using P2L.Mqtt;
using Spectre.Console.Cli;

// ─── Configuration ────────────────────────────────────────────────────────

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("P2L_")
    .Build();

var appSettings = config.Get<AppSettings>() ?? new AppSettings();

// ─── Self-heal crash handlers (installed as early as possible) ────────────

if (appSettings.SelfHeal.Enabled)
    SelfHealHandler.Install(appSettings.SelfHeal, args);

// ─── Service registration ─────────────────────────────────────────────────

var services = new ServiceCollection();

services.AddSingleton(appSettings);
services.AddSingleton(appSettings.Gateway);

// Logging — console + rotating file audit trail
services.AddLogging(builder =>
{
    builder.AddConfiguration(config.GetSection("Logging"));
    builder.AddSimpleConsole(o =>
    {
        o.SingleLine      = true;
        o.TimestampFormat = "HH:mm:ss ";
    });
    // Rotating file for hardware TX/RX audit (mis-pick investigation)
    builder.AddProvider(new RotatingFileLoggerProvider(
        appSettings.LogDirectory,
        appSettings.MaxLogFileSizeBytes,
        LogLevel.Debug));
});

// Hardware
services.AddSingleton<AioiGateway>(sp =>
{
    var gw  = appSettings.Gateway;
    var log = sp.GetRequiredService<ILogger<AioiGateway>>();
    return new AioiGateway(gw.Host, gw.Port, log);
});

// Core
services.AddSingleton<LightModuleRegistry>();
services.AddSingleton<PickingEngine>();
services.AddSingleton<HeartbeatService>();
services.AddSingleton<WatchdogService>(sp =>
{
    var svc = new WatchdogService(
        sp.GetRequiredService<AioiGateway>(),
        sp.GetRequiredService<ILogger<WatchdogService>>(),
        appSettings.SelfHeal.WatchdogTimeoutMinutes);
    if (appSettings.SelfHeal.Enabled && appSettings.SelfHeal.WatchdogTimeoutMinutes > 0)
        svc.RestartRequested += reason => SelfHealHandler.TriggerWatchdog(reason);
    return svc;
});
services.AddSingleton<DeviceScanService>();
services.AddSingleton<PreemptionGate>();
services.AddSingleton<CsvPickTaskRunner>();
services.AddSingleton<RegistryPersistenceService>();

// MQTT bridge (optional — only registered when Enabled=true)
if (appSettings.Mqtt.Enabled)
{
    services.AddSingleton(appSettings.Mqtt);
    services.AddSingleton<MqttBridgeService>();
}
// Offline buffer-only mode: MQTT disabled but OfflineBufferPath is configured.
// Events are written to the JSONL file so nothing is lost before a broker is set up.
// MqttEventPublisher will replay this file automatically on the first successful connect.
else if (!string.IsNullOrWhiteSpace(appSettings.Mqtt.OfflineBufferPath))
{
    services.AddSingleton(appSettings.Mqtt);
    services.AddSingleton<OfflineBufferService>();
}

// CLI Commands
services.AddTransient<RootCommand>();
services.AddTransient<LightOnCommand>();
services.AddTransient<LightOffCommand>();
services.AddTransient<TerminateAllCommand>();
services.AddTransient<BatchPickCommand>();
services.AddTransient<ListDevicesCommand>();
services.AddTransient<ConfigureDeviceCommand>();
services.AddSingleton<DeviceConfigCsvService>();
services.AddTransient<ExportCommand>();
services.AddTransient<ImportCommand>();
services.AddTransient<RunPickCommand>();

// ─── CLI application ──────────────────────────────────────────────────────

var registrar  = new TypeRegistrar(services);
var app        = new CommandApp<RootCommand>(registrar);

app.Configure(cfg =>
{
    cfg.SetApplicationName("p2l");
    cfg.SetApplicationVersion("1.0.0");

    cfg.AddBranch("light", branch =>
    {
        branch.SetDescription("Control individual light modules");
        branch.AddCommand<LightOnCommand>("on")
              .WithDescription("Light up a module with a quantity");
        branch.AddCommand<LightOffCommand>("off")
              .WithDescription("Turn off a module");
        branch.AddCommand<TerminateAllCommand>("terminate")
              .WithDescription("Terminate all (or block) modules");
        branch.AddCommand<ConfigureDeviceCommand>("set")
              .WithDescription("Adjust colour, mode, SEG, buzzer or display of a module");
    });

    cfg.AddCommand<BatchPickCommand>("pick")
       .WithDescription("Start a batch picking session");

    cfg.AddCommand<ListDevicesCommand>("devices")
       .WithDescription("Scan and list all connected light modules");

    cfg.AddCommand<ExportCommand>("export")
       .WithDescription("Export device list to a CSV file for bulk editing");

    cfg.AddCommand<ImportCommand>("import")
       .WithDescription("Import a CSV file and apply parameter changes to hardware");

    cfg.AddCommand<RunPickCommand>("run")
       .WithDescription("Run a CSV pick-task file sequentially with auto-delete on confirmation");

#if DEBUG
    cfg.PropagateExceptions();
    cfg.ValidateExamples();
#endif
});

var exitCode = await app.RunAsync(args);

// Explicitly await async disposal so IAsyncDisposable singletons
// (HeartbeatService, AioiGateway, etc.) are fully torn down before exit.
await registrar.DisposeAsync();

return exitCode;
