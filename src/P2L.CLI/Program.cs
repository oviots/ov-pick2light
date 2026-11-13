using P2L.CLI.Web;
using P2L.CLI.Licensing;
using P2L.CLI.Observability;
using P2L.Shared;
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

// Inspect local business receipts even when authen or MQTT is unreachable.
if (args is ["confirms"])
{
    if (!File.Exists(ConfirmationOutbox.DefaultPath)) { Console.WriteLine("No pending Confirm receipts."); return 0; }
    var pending = new ConfirmationOutbox(ConfirmationOutbox.DefaultPath).ReadPending();
    Console.WriteLine($"Pending Confirm receipts: {pending.Count} ({ConfirmationOutbox.DefaultPath})");
    foreach (var payload in pending) Console.WriteLine(payload);
    return 0;
}

// Enrollment must run before remote config bootstrap and without constructing hardware services.
if (args.Length > 0 && args[0] == "license")
{
    var licenseApp = new CommandApp();
    licenseApp.Configure(options =>
    {
        options.SetApplicationName("p2l license");
        options.AddCommand<ActivateLicenseCommand>("activate").WithDescription("Activate this machine using a license entered privately in the terminal");
        options.AddCommand<CheckLicenseCommand>("check").WithDescription("Validate license and fetch config without connecting to hardware");
    });
    return await licenseApp.RunAsync(args[1..]);
}

// ─── Configuration ────────────────────────────────────────────────────────

// ProgramData\P2L — writable by NT SERVICE\* accounts; used for logs and DB.
var appDataBase = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "P2L");

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile(Path.Combine("config", "p2l_config.json"), optional: true)   // exe-dir config
    .AddEnvironmentVariables("P2L_")
    .Build();

// Resolve operational settings before creating the gateway or starting any services.
// No fallback to local MQTT/gateway config when the API is unavailable.
RemoteConfigProvider? remoteConfig = null;
try
{
    remoteConfig = new RemoteConfigProvider();
    using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var remoteValues = await remoteConfig.LoadAsync(startupTimeout.Token);
    config = new ConfigurationBuilder().AddConfiguration(config).AddInMemoryCollection(remoteValues).Build();
}
catch
{
    remoteConfig?.Dispose();
    Console.Error.WriteLine("Cannot load licensed configuration. Check activation, OS service account and HTTPS license API. No hardware connection was started.");
    return 1;
}

using var remoteConfigLifetime = remoteConfig;

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
        BlockNos = remoteConfig.BlockNos,
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
        ConfirmApi = remoteConfig.ConfirmApi,
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
        OfflineBufferPath = Cfg_String(config, "Mqtt:OfflineBufferPath", Path.Combine(AppContext.BaseDirectory, "mqtt_offline_buffer.jsonl")),
        ClientDbPath      = config["Mqtt:ClientDbPath"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "P2L", "p2l_devices.db"),
        Tls = new MqttTlsSettings
        {
            Enabled             = Cfg_Bool  (config, "Mqtt:Tls:Enabled",             true),
            CaCertPath          = config["Mqtt:Tls:CaCertPath"],
            CaCertPem           = config["Mqtt:Tls:CaCertPem"],
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

using var cts = new CancellationTokenSource();
using var shutdownSignals = new ShutdownSignals(cts);

// ─── Service registration ─────────────────────────────────────────────────

var services = new ServiceCollection();
await using var axiom = remoteConfig.Axiom is { } axiomSettings ? new AxiomTelemetry(axiomSettings) : null;
if (axiom is not null) services.AddSingleton<IP2LTelemetry>(axiom);
var webLogs = new WebLogProvider();
services.AddSingleton(webLogs);
var isDashboardMode = args.Length == 0;

services.AddSingleton<Func<CancellationToken, Task>>(remoteConfig.AuthorizeReconnectAsync);
services.AddSingleton(appSettings);
services.AddSingleton(appSettings.Gateway);

// Logging — console + rotating file audit trail
services.AddLogging(builder =>
{
    builder.AddProvider(webLogs);
    builder.AddConfiguration(config.GetSection("Logging"));
    if (axiom is not null)
    {
        builder.AddProvider(axiom);
        builder.AddFilter<AxiomTelemetry>(null, remoteConfig.Axiom?.Mode == "debug" ? LogLevel.Trace : LogLevel.Information);
    }
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
services.AddSingleton(new BlockPolicy(appSettings.Gateway.BlockNos));
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
services.AddSingleton(sp => new CsvPickTaskRunner(sp.GetRequiredService<PickingEngine>(),
    sp.GetRequiredService<PreemptionGate>(), sp.GetRequiredService<ILogger<CsvPickTaskRunner>>(), appSettings.Gateway.BlockNo));
if (appSettings.EnableStatePersistence)
    services.AddSingleton<RegistryPersistenceService>();

// MQTT bridge (optional — only registered when Enabled=true)
if (appSettings.Mqtt.Enabled)
{
    services.AddSingleton(appSettings.Mqtt);
    services.AddSingleton(sp =>
        new DeviceLabelStore(
            appSettings.Mqtt.ClientDbPath,
            sp.GetRequiredService<ILogger<DeviceLabelStore>>()));
    // Stable across versioned publish folders. Never store pending business events beside an updateable binary.
    services.AddSingleton(sp => new ConfirmationOutbox(ConfirmationOutbox.DefaultPath,
        $"{appSettings.Gateway.Host}:{appSettings.Gateway.Port}|{appSettings.Mqtt.Host}:{appSettings.Mqtt.Port}|{appSettings.Mqtt.CommandTopic}|{appSettings.Mqtt.EventTopic}",
        sp.GetService<IP2LTelemetry>(), appSettings.Mqtt.EventTopic, sp.GetRequiredService<DeviceLabelStore>()));
    services.AddSingleton<IConfirmationJournal>(sp => sp.GetRequiredService<ConfirmationOutbox>());
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

// Embedded browser UI uses this process's state and MQTT connection; no Python or second credential reader.
services.AddSingleton(sp => new DeviceCatalog(appSettings.Mqtt.ClientDbPath!,
    sp.GetRequiredService<DeviceLabelStore>(), sp.GetRequiredService<LightModuleRegistry>()));
services.AddSingleton(sp =>
{
    var mqtt = sp.GetRequiredService<MqttBridgeService>();
    return new WebClientService(appSettings, sp.GetRequiredService<AioiGateway>(),
        sp.GetRequiredService<LightModuleRegistry>(), sp.GetRequiredService<PickingEngine>(),
        sp.GetRequiredService<DeviceCatalog>(), webLogs, mqtt.HandleLocalCommandAsync, () => mqtt.IsConnected,
        sp.GetRequiredService<ILogger<WebClientService>>());
});

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
       .WithDescription("Run Bridge and open the integrated local web client");

#if DEBUG
    cfg.PropagateExceptions();
    cfg.ValidateExamples();
#endif
});

var commandCompleted = false;
try
{
// ─── Windows Service Control Manager mode ────────────────────────────────
#if WINDOWS_SERVICE
    if (OperatingSystem.IsWindows() && !Environment.UserInteractive)
    {
        // Launched by SCM: hand lifecycle to ServiceBase so Start/Stop hooks work.
        using var winsvc = new P2LWindowsService(() => app.RunAsync(args), cts);
        System.ServiceProcess.ServiceBase.Run(winsvc);
        commandCompleted = true;
        return 0;
    }
#endif

    var exitCode = await app.RunAsync(args);
    commandCompleted = true;
    return exitCode;
}
finally
{
    // Keep genuine unhandled crashes eligible for self-heal; normal/user stops are final.
    if (commandCompleted || cts.IsCancellationRequested) SelfHealHandler.BeginShutdown();
    await cts.CancelAsync();
    await registrar.DisposeAsync();
}
