using System.Text.Json;

namespace P2L.IntegrationTests.Infrastructure;

/// <summary>
/// Integration test settings, loaded from testconfig.json then overridden
/// by environment variables so CI/CD can target a different gateway.
///
/// Environment variable prefix: P2L_TEST_
///   P2L_TEST_GATEWAY_HOST    — gateway IP
///   P2L_TEST_GATEWAY_PORT    — TCP port (default 5003)
///   P2L_TEST_MODULE_ADDR     — 4-digit address used for light-toggle tests
///   P2L_TEST_LIGHT_ON_MS     — how long to leave the light on (default 2000)
/// </summary>
public sealed class TestConfig
{
    public string GatewayHost    { get; private set; } = "192.168.1.254";
    public int    GatewayPort    { get; private set; } = 5003;
    public string TestModuleAddr { get; private set; } = "0001";
    public int    LightOnMs      { get; private set; } = 2000;
    public int    PingTimeoutMs  { get; private set; } = 1000;
    public int    TcpTimeoutMs   { get; private set; } = 5000;

    public static TestConfig Load()
    {
        var cfg = new TestConfig();

        // 1. JSON file baseline
        var jsonPath = Path.Combine(AppContext.BaseDirectory, "testconfig.json");
        if (File.Exists(jsonPath))
        {
            using var fs  = File.OpenRead(jsonPath);
            var doc       = JsonDocument.Parse(fs);
            var root      = doc.RootElement;

            if (root.TryGetProperty("GatewayHost",    out var v)) cfg.GatewayHost    = v.GetString() ?? cfg.GatewayHost;
            if (root.TryGetProperty("GatewayPort",    out v))     cfg.GatewayPort    = v.GetInt32();
            if (root.TryGetProperty("TestModuleAddr", out v))     cfg.TestModuleAddr = v.GetString() ?? cfg.TestModuleAddr;
            if (root.TryGetProperty("LightOnMs",      out v))     cfg.LightOnMs      = v.GetInt32();
            if (root.TryGetProperty("PingTimeoutMs",  out v))     cfg.PingTimeoutMs  = v.GetInt32();
            if (root.TryGetProperty("TcpTimeoutMs",   out v))     cfg.TcpTimeoutMs   = v.GetInt32();
        }

        // 2. Environment variable overrides (higher priority)
        cfg.GatewayHost    = Env("P2L_TEST_GATEWAY_HOST")  ?? cfg.GatewayHost;
        cfg.GatewayPort    = int.TryParse(Env("P2L_TEST_GATEWAY_PORT"),   out int p) ? p : cfg.GatewayPort;
        cfg.TestModuleAddr = Env("P2L_TEST_MODULE_ADDR")   ?? cfg.TestModuleAddr;
        cfg.LightOnMs      = int.TryParse(Env("P2L_TEST_LIGHT_ON_MS"),    out int l) ? l : cfg.LightOnMs;

        return cfg;
    }

    private static string? Env(string key) =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))
            ? null
            : Environment.GetEnvironmentVariable(key);

    public override string ToString() =>
        $"Gateway={GatewayHost}:{GatewayPort}  Module={TestModuleAddr}  LightOnMs={LightOnMs}";
}
