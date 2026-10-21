using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using P2L.Core;
using P2L.Core.Services;
using P2L.Hardware;
using P2L.Mqtt;
using P2L.Mqtt.Configuration;

// Isolated interoperability fixture. It never loads the CLI configuration, uses
// production endpoints, accepts hostnames, or publishes outside the test topics.
if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: P2L.TransportHarness primaryPort backupPort gatewayPort dataDir");
    return 2;
}

int ParsePort(string text)
{
    if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1024 or > 65535)
        throw new ArgumentException("Harness ports must be explicit unprivileged ports (1024..65535).");
    return port;
}

string ParseTransport(string name)
{
    string value = Environment.GetEnvironmentVariable(name) ?? "mqtt";
    if (value is not ("mqtt" or "ws" or "wss"))
        throw new ArgumentException($"{name} must be mqtt, ws or wss.");
    return value;
}
string? WebSocketUrl(string transport, int port) => transport == "mqtt" ? null : $"{transport}://127.0.0.1:{port}/mqtt";
MqttTlsSettings TransportTls(string transport) => new()
{
    Enabled = transport == "wss",
    CaCertPath = transport == "wss" ? Environment.GetEnvironmentVariable("P2L_TEST_CA_CERT") : null
};

int primaryPort, backupPort, gatewayPort;
string dataDir, primaryTransport, backupTransport;
try
{
    primaryPort = ParsePort(args[0]); backupPort = ParsePort(args[1]);
    primaryTransport = ParseTransport("P2L_TEST_PRIMARY_TRANSPORT");
    backupTransport = ParseTransport("P2L_TEST_BACKUP_TRANSPORT");
    gatewayPort = ParsePort(args[2]);
    if (new[] { primaryPort, backupPort, gatewayPort }.Distinct().Count() != 3)
        throw new ArgumentException("Harness endpoints require three distinct loopback ports.");
    dataDir = Path.GetFullPath(args[3]);
    string[] temporaryRoots = [Path.GetFullPath(Path.GetTempPath()), "/private/tmp", "/tmp"];
    if (!temporaryRoots.Any(root => dataDir.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        throw new ArgumentException("Harness data must be in a dedicated temporary directory.");
    var directory = new DirectoryInfo(dataDir);
    if (directory.Exists && directory.LinkTarget is not null)
        throw new ArgumentException("Harness data directory must not be a symbolic link.");
    string marker = Path.Combine(dataDir, ".p2l-transport-harness");
    if (Directory.Exists(dataDir) && Directory.EnumerateFileSystemEntries(dataDir).Any() && !File.Exists(marker))
        throw new ArgumentException("Refusing to reuse a non-empty directory without the harness owner marker.");
    Directory.CreateDirectory(dataDir);
    File.WriteAllText(marker, "P2L loopback interoperability test data; safe to retain for restart tests.\n");
}
catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("HARNESS_ERROR " + ex.Message);
    return 2;
}

const string prefix = "p2l-test-01";
string uniqueId = $"p2l-interop-{primaryPort}-{backupPort}";
string devicesDb = Path.Combine(dataDir, "devices.db");
string outbox = Path.Combine(dataDir, "outbox.jsonl");
using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = devicesDb, Pooling = false }.ToString()))
{
    database.Open();
    using var create = database.CreateCommand();
    create.CommandText = "CREATE TABLE IF NOT EXISTS devices (address TEXT PRIMARY KEY, label TEXT)";
    create.ExecuteNonQuery();
}

var settings = new MqttSettings
{
    Enabled = true, Host = "127.0.0.1", Port = primaryPort, ClientId = uniqueId + "-primary",
    WebSocketUrl = WebSocketUrl(primaryTransport, primaryPort),
    CommandTopic = prefix + "/commands", EventTopic = prefix + "/events",
    DevicesTopic = prefix + "/devices", StatusTopic = prefix + "/status",
    OfflineBufferPath = outbox, ClientDbPath = devicesDb,
    CommandReceiptPath = Path.Combine(dataDir, "commands.db"),
    SessionExpirySeconds = 600, Tls = TransportTls(primaryTransport),
    ProbeIntervalMs = 100, ProbeTimeoutMs = 600, FailbackStableMs = 1000,
    BackupBroker = new MqttSettings
    {
        Enabled = true, Host = "127.0.0.1", Port = backupPort, ClientId = uniqueId + "-backup",
        WebSocketUrl = WebSocketUrl(backupTransport, backupPort),
        CommandTopic = prefix + "/commands", EventTopic = prefix + "/events",
        DevicesTopic = prefix + "/devices", StatusTopic = prefix + "/status",
        SessionExpirySeconds = 600, Tls = TransportTls(backupTransport)
    }
};

using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(180));
await using var gateway = new AioiGateway("127.0.0.1", gatewayPort, NullLogger<AioiGateway>.Instance);
var registry = new LightModuleRegistry();
using var engine = new PickingEngine(gateway, registry, NullLogger<PickingEngine>.Instance);
using var persistence = new RegistryPersistenceService(registry, NullLogger<RegistryPersistenceService>.Instance,
    Path.Combine(dataDir, "state.json"));
persistence.Load();
var labels = new DeviceLabelStore(devicesDb, NullLogger<DeviceLabelStore>.Instance);
labels.Load();
var scan = new DeviceScanService(gateway, registry, NullLogger<DeviceScanService>.Instance);
await using var bridge = new MqttBridgeService(settings, gateway, engine, registry,
    new PreemptionGate(), null, labels, scan, NullLoggerFactory.Instance);
await gateway.StartAsync(lifetime.Token);
await bridge.StartAsync(lifetime.Token);
Console.WriteLine("READY");
Console.Out.Flush();

try
{
    while (true)
    {
        // Console input can block synchronously on some platforms. Keep the hard
        // fixture deadline independent from stdin, including a controller crash.
        string? command = await Task.Run(Console.ReadLine).WaitAsync(lifetime.Token);
        if (command is null || command == "stop") return 0;
        if (command != "state") continue;
        var modules = registry.All;
        int outboxLines;
        try { outboxLines = File.Exists(outbox) ? File.ReadLines(outbox).Count() : 0; }
        catch (IOException) { outboxLines = -1; }
        Console.WriteLine("STATE " + JsonSerializer.Serialize(new
        {
            count = modules.Count, activeCount = registry.ActiveCount, isBatchActive = engine.IsBatchActive,
            gatewayConnected = gateway.IsConnected,
            modules = modules.Select(module => new
            {
                address = module.Address, status = module.Status.ToString(), displayData = module.DisplayData,
                blockNo = module.BlockNo, correlationId = module.CorrelationId,
                commandItemId = module.CommandItemId, pickingDetailId = module.PickingDetailId,
                adjustedQuantity = module.AdjustedQuantity
            }),
            outboxLineCount = outboxLines
        }));
        Console.Out.Flush();
    }
}
catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
{
    Console.Error.WriteLine("HARNESS_TIMEOUT: 180-second fixture lifetime expired.");
    return 124;
}
