using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace P2L.CLI.Configuration;

/// <summary>Optional, best-effort cloud logs. Local hardware audit remains independent.</summary>
public sealed class AxiomLoggingOptions
{
    public bool Enabled { get; init; }
    public string Endpoint { get; init; } = "https://us-east-1.aws.edge.axiom.co/v1/ingest/p2l-logs";
    public string ApiToken { get; init; } = "";
    public LogLevel MinimumLevel { get; init; } = LogLevel.Debug;
    public string ClientId { get; init; } = "p2l-bridge";
    public string Service { get; init; } = "p2l-bridge";
    public string Environment { get; init; } = "local";
    public string InstanceName { get; init; } = System.Environment.MachineName;
    public int QueueCapacity { get; init; } = 1024;
    public int BatchSize { get; init; } = 100;
    public int FlushIntervalMs { get; init; } = 2000;
    public int RequestTimeoutMs { get; init; } = 3000;
    public int ShutdownTimeoutMs { get; init; } = 1000;

    // Explicit reads preserve Native AOT compatibility; no reflection-based binding.
    public static AxiomLoggingOptions FromConfiguration(IConfiguration config)
    {
        int Number(string key, int fallback, int min, int max) =>
            int.TryParse(config["Axiom:" + key], out var value) ? Math.Clamp(value, min, max) : fallback;
        return new()
        {
            Enabled = bool.TryParse(config["Axiom:Enabled"], out var enabled) && enabled,
            Endpoint = config["Axiom:Endpoint"] ?? new AxiomLoggingOptions().Endpoint,
            ApiToken = config["Axiom:ApiToken"] ?? "",
            MinimumLevel = Enum.TryParse<LogLevel>(config["Axiom:MinimumLevel"], true, out var level)
                && Enum.IsDefined(level) ? level : LogLevel.Debug,
            ClientId = config["Mqtt:ClientId"] ?? "p2l-bridge",
            Service = config["Axiom:Service"] ?? "p2l-bridge",
            Environment = config["Axiom:Environment"] ?? "local",
            InstanceName = config["Axiom:InstanceName"] ?? System.Environment.MachineName,
            QueueCapacity = Number("QueueCapacity", 1024, 1, 8192),
            BatchSize = Number("BatchSize", 100, 1, 500),
            FlushIntervalMs = Number("FlushIntervalMs", 2000, 100, 60000),
            RequestTimeoutMs = Number("RequestTimeoutMs", 3000, 100, 5000),
            ShutdownTimeoutMs = Number("ShutdownTimeoutMs", 1000, 100, 3000),
        };
    }

    public bool IsValid() => !string.IsNullOrWhiteSpace(ApiToken) && ApiToken.Length <= 4096
        && !ApiToken.Any(char.IsWhiteSpace)
        && Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
        && string.IsNullOrEmpty(uri.Query) && uri.AbsolutePath.StartsWith("/v1/ingest/", StringComparison.Ordinal)
        && uri.AbsolutePath.Length > "/v1/ingest/".Length
        && QueueCapacity is >= 1 and <= 8192 && BatchSize is >= 1 and <= 500
        && FlushIntervalMs is >= 1 and <= 60000 && RequestTimeoutMs is >= 1 and <= 5000
        && ShutdownTimeoutMs is >= 1 and <= 3000;
}
