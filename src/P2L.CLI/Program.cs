using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using P2L.CLI.Commands;
using P2L.CLI.Configuration;
using P2L.CLI.Infrastructure;
using P2L.Core;
using P2L.Core.Services;
using P2L.Hardware;
using Spectre.Console.Cli;

// ─── Configuration ────────────────────────────────────────────────────────

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("P2L_")
    .AddCommandLine(args)
    .Build();

var appSettings = config.Get<AppSettings>() ?? new AppSettings();

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
services.AddSingleton<DeviceScanService>();

// CLI Commands
services.AddTransient<RootCommand>();
services.AddTransient<LightOnCommand>();
services.AddTransient<LightOffCommand>();
services.AddTransient<TerminateAllCommand>();
services.AddTransient<BatchPickCommand>();
services.AddTransient<ListDevicesCommand>();

// ─── CLI application ──────────────────────────────────────────────────────

var registrar = new TypeRegistrar(services);
var app       = new CommandApp<RootCommand>(registrar);

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
    });

    cfg.AddCommand<BatchPickCommand>("pick")
       .WithDescription("Start a batch picking session");

    cfg.AddCommand<ListDevicesCommand>("devices")
       .WithDescription("Scan and list all connected light modules");

#if DEBUG
    cfg.PropagateExceptions();
    cfg.ValidateExamples();
#endif
});

return await app.RunAsync(args);
