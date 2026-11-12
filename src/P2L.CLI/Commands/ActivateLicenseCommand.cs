using P2L.CLI.Licensing;
using Spectre.Console;
using Spectre.Console.Cli;

namespace P2L.CLI.Commands;

public sealed class ActivateLicenseCommand : AsyncCommand<ActivateLicenseCommand.Settings>
{
    public sealed class Settings : CommandSettings { }
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var activated = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            using var api = new LicenseApiClient(Environment.GetEnvironmentVariable("P2L_LICENSE_API_URL"));
            var code = DeviceActivation.NormalizeLicense(AnsiConsole.Prompt(new TextPrompt<string>("License:").Secret()));
            // Persist before making any activation request; response-loss retries reuse the same key.
            using var key = DeviceCredentialStore.LoadOrCreate();
            var id = await DeviceActivation.ActivateAsync(api, key, code, Environment.MachineName, cts.Token);
            activated = true;
            using var provider = new RemoteConfigProvider();
            await provider.LoadAsync(cts.Token);
            AnsiConsole.MarkupLine("[green]License activated; Bridge configuration validated.[/]");
            AnsiConsole.WriteLine($"Device ID: {id}");
            AnsiConsole.WriteLine("Next: p2l serve");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteLine(activated ? "License activated, but config validation failed. Retry with p2l license check." : "Activation failed.");
            AnsiConsole.WriteLine(ex is InvalidOperationException ? ex.Message : "Check the license API, OS credential vault and network connection.");
            return 1;
        }
    }
}

public sealed class CheckLicenseCommand : AsyncCommand<CheckLicenseCommand.Settings>
{
    public sealed class Settings : CommandSettings { }
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var provider = new RemoteConfigProvider();
            var values = await provider.LoadAsync(cts.Token);
            AnsiConsole.MarkupLine("[green]License and Bridge configuration valid.[/]");
            AnsiConsole.WriteLine($"Controller: {values["Gateway:Host"]}:{values["Gateway:Port"]}");
            AnsiConsole.WriteLine("No MQTT or controller connection was opened.");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteLine("License check failed.");
            AnsiConsole.WriteLine(ex is InvalidOperationException ? ex.Message : "Check the license API, OS credential vault and network connection.");
            return 1;
        }
    }
}
