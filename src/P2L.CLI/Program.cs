using System.Runtime.InteropServices;
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
using P2L.Mqtt.Configuration;
using P2L.Shared.Constants;
using Spectre.Console.Cli;

// ─── Configuration ────────────────────────────────────────────────────────

// Windows services use ProgramData; Unix installers grant write access to the
// installation directory rather than the system-wide application-data folder.
var appDataBase = OperatingSystem.IsWindows()
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "P2L")
    : Path.Combine(AppContext.BaseDirectory, "data");
try { Directory.CreateDirectory(appDataBase); }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Cannot create P2L runtime data directory '{appDataBase}': {ex.Message}");
    return 1;
}

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile(Path.Combine("config", "p2l_config.json"), optional: true)   // exe-dir config
    .AddEnvironmentVariables("P2L_")
    .Build();

// config.Get<T>() uses reflection which is trimmed under Native AOT.
// Read all values explicitly via the string-key indexer (no reflection).
static bool   Cfg_Bool  (IConfiguration c, string k, bool   def) => bool .TryParse(c[k], out var v) ? v : def;
static int    Cfg_Int   (IConfiguration c, string k, int    def) => int  .TryParse(c[k], out var v) ? v : def;
static long   Cfg_Long  (IConfiguration c, string k, long   def) => long .TryParse(c[k], out var v) ? v : def;
static string Cfg_String(IConfiguration c, string k, string def) => c[k] ?? def;

var appSettings = new AppSettings
{
    Gateway = new GatewaySettings
    {
        Host    = Cfg_String(config, "Gateway:Host",    AioiConstants.DefaultGatewayIp),
        Port    = Cfg_Int   (config, "Gateway:Port",    AioiConstants.DefaultTcpPort),
        BlockNo = Cfg_String(config, "Gateway:BlockNo", "00"),
    },
    LogDirectory        = Cfg_String(config, "LogDirectory",        Path.Combine(appDataBase, "logs")),
    MaxLogFileSizeBytes = Cfg_Long  (config, "MaxLogFileSizeBytes", 10 * 1024 * 1024),
    EnableStatePersistence = Cfg_Bool(config, "EnableStatePersistence", false),
    StartMinimizedOnWindows = Cfg_Bool(config, "StartMinimizedOnWindows", true),
    Mqtt = new MqttSettings
    {
        Enabled           = Cfg_Bool  (config, "Mqtt:Enabled",           false),
        Host              = Cfg_String(config, "Mqtt:Host",              "localhost"),
        Port              = Cfg_Int   (config, "Mqtt:Port",              8883),
        ClientId          = Cfg_String(config, "Mqtt:ClientId",          "p2l-bridge"),
        Username          = config["Mqtt:Username"],
        Password          = config["Mqtt:Password"],
        CommandTopic      = Cfg_String(config, "Mqtt:CommandTopic",      "p2l/commands"),
        EventTopic        = Cfg_String(config, "Mqtt:EventTopic",        "p2l/events"),
        DevicesTopic      = Cfg_String(config, "Mqtt:DevicesTopic",      "p2l/devices"),
        StatusTopic       = Cfg_String(config, "Mqtt:StatusTopic",       "p2l/status"),
        OfflineBufferPath = Cfg_String(config, "Mqtt:OfflineBufferPath", Path.Combine(appDataBase, "mqtt_offline_buffer.jsonl")),
        ClientDbPath      = config["Mqtt:ClientDbPath"] ?? Path.Combine(appDataBase, "p2l_devices.db"),
        SessionExpirySeconds = (uint)Math.Clamp(Cfg_Int(config, "Mqtt:SessionExpirySeconds", 600), 1, 86400),
        CommandReceiptPath = Cfg_String(config, "Mqtt:CommandReceiptPath", Path.Combine(appDataBase, "p2l_commands.db")),
        Tls = new MqttTlsSettings
        {
            Enabled             = Cfg_Bool  (config, "Mqtt:Tls:Enabled",             true),
            CaCertPath          = config["Mqtt:Tls:CaCertPath"],
            ClientCertPath      = config["Mqtt:Tls:ClientCertPath"],
            ClientKeyPath       = config["Mqtt:Tls:ClientKeyPath"],
            CertPassword        = config["Mqtt:Tls:CertPassword"],
            AllowUntrustedCerts = Cfg_Bool  (config, "Mqtt:Tls:AllowUntrustedCerts", false),
        },
    },
    SelfHeal = new SelfHealSettings
    {
        Enabled               = Cfg_Bool(config, "SelfHeal:Enabled",               true),
        MaxRestarts           = Cfg_Int (config, "SelfHeal:MaxRestarts",            5),
        RestartWindowMinutes  = Cfg_Int (config, "SelfHeal:RestartWindowMinutes",   10),
        BackOffSeconds        = Cfg_Int (config, "SelfHeal:BackOffSeconds",         3),
        WatchdogTimeoutMinutes= Cfg_Int (config, "SelfHeal:WatchdogTimeoutMinutes", 5),
    },
};

if (OperatingSystem.IsWindows() &&
    Environment.UserInteractive &&
    args.Length == 0 &&
    appSettings.StartMinimizedOnWindows)
{
    WindowsConsoleWindow.MinimizeIfPresent();
}

// ─── Self-heal crash handlers (installed as early as possible) ────────────

if (appSettings.SelfHeal.Enabled)
    SelfHealHandler.Install(appSettings.SelfHeal, args, appDataBase);

// ─── Graceful-shutdown token ──────────────────────────────────────────────
// Created early so it can be registered in DI for long-running commands.

var cts = new CancellationTokenSource();

// Ctrl-C (interactive) → cancel
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// SIGTERM from systemd / container runtimes → cancel
if (!OperatingSystem.IsWindows())
    PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => cts.Cancel());

// ─── Service registration ─────────────────────────────────────────────────

var services = new ServiceCollection();
var isDashboardMode = args.Length == 0;

services.AddSingleton(appSettings);
services.AddSingleton(appSettings.Gateway);

// Logging — console + rotating file audit trail
services.AddLogging(builder =>
{
    builder.AddConfiguration(config.GetSection("Logging"));
    if (!isDashboardMode)
    {
        builder.AddSimpleConsole(o =>
        {
            o.SingleLine      = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
    }
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
if (appSettings.EnableStatePersistence || appSettings.Mqtt.Enabled)
    services.AddSingleton(sp => new RegistryPersistenceService(
        sp.GetRequiredService<LightModuleRegistry>(),
        sp.GetRequiredService<ILogger<RegistryPersistenceService>>(),
        Path.Combine(appDataBase, "p2l_state.json")));

// MQTT bridge (optional — only registered when Enabled=true)
if (appSettings.Mqtt.Enabled)
{
    services.AddSingleton(appSettings.Mqtt);
    services.AddSingleton(sp =>
        new DeviceLabelStore(
            appSettings.Mqtt.ClientDbPath,
            sp.GetRequiredService<ILogger<DeviceLabelStore>>()));
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
services.AddTransient<ServeCommand>();
// Shutdown CTS — long-running commands can inject this to honour graceful stop
services.AddSingleton(cts);

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

    cfg.AddCommand<ServeCommand>("serve")
       .WithDescription("Run as a headless daemon (Windows Service / systemd unit entry point)");

#if DEBUG
    cfg.PropagateExceptions();
    cfg.ValidateExamples();
#endif
});

// ─── Windows Service Control Manager mode ────────────────────────────────
#if WINDOWS_SERVICE
if (OperatingSystem.IsWindows() && !Environment.UserInteractive)
{
    // Launched by SCM: hand lifecycle to ServiceBase so Start/Stop hooks work.
    using var winsvc = new P2LWindowsService(() => app.RunAsync(args), cts);
    System.ServiceProcess.ServiceBase.Run(winsvc);
    await registrar.DisposeAsync();
    return 0;
}
#endif

var exitCode = await app.RunAsync(args);

// Explicitly await async disposal so IAsyncDisposable singletons
// (HeartbeatService, AioiGateway, etc.) are fully torn down before exit.
await registrar.DisposeAsync();
cts.Dispose();

return exitCode;
