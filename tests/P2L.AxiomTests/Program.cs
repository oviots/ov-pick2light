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
Console.WriteLine("All Axiom checks passed. Fake HTTP only; no Axiom/UAT/controller access.");

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
}
