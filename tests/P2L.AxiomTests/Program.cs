using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using P2L.CLI.Observability;

static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
var config = JsonNode.Parse("""{"siteCode":"TEST","gateway":{"blockNo":"01"},"mqtt":{"clientId":"fixture-bridge","password":"MQTT-SEKRET"}}""")!;
Require(AxiomSettings.FromRemote(JsonDocument.Parse(config.ToJsonString()).RootElement) is null, "Missing Axiom must disable export");
config["axiom"] = new JsonObject {["enabled"] = false};
Require(AxiomSettings.FromRemote(JsonDocument.Parse(config.ToJsonString()).RootElement) is null, "Disabled Axiom must disable export");
config["axiom"] = new JsonObject {["enabled"] = true, ["endpoint"] = "https://us-east-1.aws.edge.axiom.co", ["dataset"] = "p2l", ["token"] = "AXIOM-SEKRET"};
var settings = AxiomSettings.FromRemote(JsonDocument.Parse(config.ToJsonString()).RootElement)!;
Require(settings.Mode == "operational", "Legacy config must default to operational filtering");
settings = settings with {Mode = "debug"}; // transport/redaction tests exercise the full debug path
Require(settings.IngestUrl.AbsolutePath == "/v1/ingest/p2l", "Edge URL wrong");
config["axiom"]!["endpoint"] = "https://api.axiom.co";
Require(AxiomSettings.FromRemote(JsonDocument.Parse(config.ToJsonString()).RootElement)!.IngestUrl.AbsolutePath == "/v1/datasets/p2l/ingest", "Legacy URL wrong");
config["axiom"]!["endpoint"] = "http://attacker.invalid";
try { AxiomSettings.FromRemote(JsonDocument.Parse(config.ToJsonString()).RootElement); throw new Exception("Unsafe endpoint accepted"); }
catch (InvalidDataException) { }
Console.WriteLine("PASS: optional config, edge/legacy endpoint and HTTPS destination policy");

var entries = new List<JsonObject>();
var calls = 0;
var handler = new FakeHandler(async (request, ct) =>
{
    Require(request.RequestUri == settings.IngestUrl, "Wrong destination");
    Require(request.Headers.Authorization?.ToString() == "Bearer AXIOM-SEKRET", "Missing ingest authorization");
    var text = await request.Content!.ReadAsStringAsync(ct);
    Require(!text.Contains("SEKRET") && !text.Contains("nested-sensitive") && !text.Contains("PRIVATE KEY"), "Credential leaked");
    calls++;
    if (calls == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
    entries.AddRange(JsonNode.Parse(text)!.AsArray().Select(x => x!.AsObject()));
    return new HttpResponseMessage(HttpStatusCode.OK) {Content = new StringContent("{\"failed\":0}")};
});
var telemetry = new AxiomTelemetry(settings, handler);
using (var factory = LoggerFactory.Create(builder => builder.AddProvider(telemetry).AddFilter<AxiomTelemetry>(null, LogLevel.Trace)))
{
    var log = factory.CreateLogger("FixtureService");
    log.LogDebug("Password={Password} token={Token}", "MQTT-SEKRET", "AXIOM-SEKRET");
    log.LogError(new InvalidOperationException("password=nested-sensitive"), "Service failed");
    telemetry.Record("command_received", "test/commands", """{"type":"batch_start","correlationId":"SESSION-01","items":[{"address":"0008","quantity":5,"password":"nested-sensitive"}],"token":"AXIOM-SEKRET"}""");
    telemetry.Record("event", "test/events", """{"type":"confirmed","correlationId":"SESSION-01","address":"0008"}""");
    telemetry.Record("command_received", "test/commands", "malformed private input");
    telemetry.Record("event", "test/events", new JsonObject {["type"] = "large", ["values"] = new JsonArray(Enumerable.Range(0, 100).Select(_ => (JsonNode)JsonValue.Create(new string('x', 1000))!).ToArray())}.ToJsonString());
}
await telemetry.DisposeAsync();
Require(calls >= 2, "Transient failure was not retried");
Require(entries.Count == 6, "Service logs, commands or events missing");
Require(entries.Any(x => (string?)x["level"] == "Debug"), "Debug log missing");
Require(entries.All(x => (string?)x["siteCode"] == "TEST" && (string?)x["clientId"] == "fixture-bridge"), "Machine metadata missing");
Require(entries.Count(x => (string?)x["correlationId"] == "SESSION-01") == 2, "Session correlation missing");
Require(entries.Any(x => x["data"]?["invalidJson"]?.GetValue<bool>() == true), "Malformed command not recorded safely");
Require(entries.Any(x => x["data"]?["truncated"]?.GetValue<bool>() == true), "Large payload not bounded");
Console.WriteLine("PASS: all record kinds, Debug logs, session metadata, redaction, batching, retries and shutdown flush");

var forbiddenCalls = 0;
var denied = new AxiomTelemetry(settings, new FakeHandler((_, _) => {
    forbiddenCalls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
}));
denied.Record("event", "events", "{}");
await denied.DisposeAsync();
Require(forbiddenCalls == 1 && denied.Dropped == 1, "Permanent rejection must be bounded and counted");
Console.WriteLine("PASS: ingest rejection cannot fail application commands");

var partial = new AxiomTelemetry(settings, new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content = new StringContent("{\"failed\":1}")})));
partial.Record("event", "events", "{}"); await partial.DisposeAsync();
Require(partial.Dropped == 1, "Partial ingest failure must be counted");
Console.WriteLine("PASS: HTTP 200 with rejected records is detected");

var gate = new TaskCompletionSource();
var bounded = new AxiomTelemetry(settings, new FakeHandler(async (_, ct) => {await gate.Task.WaitAsync(ct); return new HttpResponseMessage(HttpStatusCode.OK) {Content = new StringContent("{\"failed\":0}")};}));
var stopwatch = System.Diagnostics.Stopwatch.StartNew();
for (var i = 0; i < 2000; i++) bounded.Record("event", "events", "{}");
Require(stopwatch.Elapsed < TimeSpan.FromSeconds(3) && bounded.Dropped > 0, "Full queue blocked picking thread");
gate.SetResult(); await bounded.DisposeAsync();
Console.WriteLine("PASS: outage queue is bounded and producers do not wait for HTTP");
// Operational filtering is tested with fake HTTP and clock; no sleeping for a 60s aggregation window.
var clock = new TestClock();
var operational = new List<JsonObject>();
var operation = new AxiomTelemetry(settings with {Mode = "operational"}, new FakeHandler(async (request, ct) => {
    var body = await request.Content!.ReadAsStringAsync(ct);
    operational.AddRange(JsonNode.Parse(body)!.AsArray().Select(n => n!.AsObject()));
    return new HttpResponseMessage(HttpStatusCode.OK) {Content = new StringContent("{\"failed\":0}")};
}), clock);
using (var factory = LoggerFactory.Create(builder => builder.AddProvider(operation).AddFilter<AxiomTelemetry>(null, LogLevel.Trace)))
{
    var hardware = factory.CreateLogger("P2L.Hardware.AioiGateway");
    var publisher = factory.CreateLogger("P2L.Mqtt.MqttEventPublisher");
    var bridge = factory.CreateLogger("P2L.Mqtt.MqttBridgeService");
    var dispatch = factory.CreateLogger("P2L.Mqtt.MqttCommandDispatcher");
    for (var i = 0; i < 100; i++)
    {
        hardware.LogDebug("[TX] SeqNo={Seq} Cmd={Cmd}", i, "Az0000");
        hardware.LogInformation("[CONFIRM] Address={Addr} Status={Status}", "0008", "Normal");
        publisher.LogDebug("[MQTT] Published {Type}", "light_changed");
        operation.Record("event", "devices", "{\"type\":\"light_changed\",\"address\":\"0008\"}");
        operation.Record("command_received", "commands", "{\"type\":\"get_snapshot\"}");
        bridge.LogWarning("[MQTT] Connection failed: {Err}. Retrying in {D:F0}s", "token=MQTT-SEKRET", i);
    }
    hardware.LogInformation("Connected to gateway {Host}:{Port}", "localhost", 5003);
    operation.Record("command_received", "commands", """{"type":"batch_start","blockNo":"02","correlationId":"PICK-02","items":[{"address":"0008","quantity":5},{"address":"0009","quantity":7}],"token":"AXIOM-SEKRET"}""");
    operation.Record("command_received", "commands", """{"type":"light_on","blockNo":"02","correlationId":"PICK-02","address":"0008","quantity":5}""");
    for (var i = 0; i < 3; i++) operation.Record("event", "events", """{"type":"confirmed","blockNo":"02","correlationId":"PICK-02","address":"0008","status":"Normal"}""");
    operation.Record("event", "events", """{"type":"batch_completed","blockNo":"02","correlationId":"PICK-02"}""");
    for (var i = 0; i < 100; i++) operation.Record("event", "status", """{"type":"bridge_status","online":true,"gatewayConnected":true}""");
    operation.Record("event", "status", """{"type":"bridge_status","online":true,"gatewayConnected":false}""");
    // Business errors and critical failures must not be sampled or grouped.
    for (var i = 0; i < 2; i++) dispatch.LogError("[MQTT] Command dispatch failed: {T}", "batch_start");
    for (var i = 0; i < 2; i++) bridge.LogCritical("[MQTT] Connection failed: {Err}", "fatal");
}
await operation.DisposeAsync();
Require(operational.Count == 15, "Unexpected operational record count: " + operational.Count);
Require(!operational.Any(e => (string?)e["type"] == "light_changed" || (string?)e["level"] == "Debug" || (string?)e["type"] == "get_snapshot"), "Noise was exported");
Require(operational.Count(e => (string?)e["type"] == "confirmed") == 3, "Business confirmations were dropped");
Require(operational.Count(e => (string?)e["type"] == "bridge_status") == 2, "Bridge state transitions were lost or repeated");
var command = operational.Single(e => (string?)e["type"] == "batch_start");
Require((string?)command["correlationId"] == "PICK-02" && (string?)command["blockNo"] == "02", "Summary lost session routing");
Require(command["data"]!["items"] is null && (int?)command["data"]!["itemCount"] == 2 && (long?)command["data"]!["totalQuantity"] == 12, "Batch payload was not compacted");
var repeat = operational.Single(e => e["data"]?["summary"]?.GetValue<bool>() == true);
Require((long?)repeat["data"]!["repeatCount"] == 99, "Repeat count must exclude the first occurrence");
Require(!string.Join("", operational.Select(e => e.ToJsonString())).Contains("SEKRET"), "Summarized errors leaked credentials");
Require(operational.Count(e => (string?)e["level"] == "Critical") == 2 && operational.Count(e => (string?)e["level"] == "Error") == 2, "Business/critical errors were aggregated");
Console.WriteLine("PASS: operational noise filter, compact commands, complete business events, transition-only status, error counting and secret redaction");

var policyClock = new TestClock();
var policy = new AxiomLogPolicy(false, policyClock);
var warning = JsonNode.Parse("""{"level":"Warning","category":"P2L.Mqtt.MqttBridgeService","message":"connection lost","properties":{"Err":"offline"}}""")!;
const string warningTemplate = "[MQTT] Connection failed: {Err}";
Require(policy.Filter("service_log", warning.DeepClone(), warningTemplate) is not null, "First warning must be immediate");
Require(policy.Filter("service_log", warning.DeepClone(), warningTemplate) is null, "Repeat must be grouped");
policyClock.Advance(TimeSpan.FromSeconds(59)); Require(policy.FlushRepeated().Count == 0, "Summary flushed before 60s");
policyClock.Advance(TimeSpan.FromSeconds(1)); Require(policy.FlushRepeated().Single()["repeatCount"]!.GetValue<long>() == 1, "60s summary missing");
Require(policy.Filter("service_log", warning.DeepClone(), warningTemplate) is not null, "New warning window must send its first error");
Console.WriteLine("PASS: exact aggregation window and first-error delivery with fake time");

Console.WriteLine("All Axiom checks passed. Fake HTTP only; no Axiom/UAT/controller access.");

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
}

sealed class TestClock : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
}
