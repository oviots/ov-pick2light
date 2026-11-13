using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using P2L.CLI.Configuration;
using P2L.CLI.Web;
using P2L.Core;
using P2L.Hardware;
using P2L.Mqtt;
using P2L.Mqtt.Configuration;
using P2L.Shared.Models;
using P2L.Shared.Enums;

static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
var directory = Path.Combine(Path.GetTempPath(), "p2l-web-test-" + Guid.NewGuid().ToString("N"));
var telemetry = new CaptureTelemetry();
await TlsChecks.RunAsync();
Directory.CreateDirectory(directory);
var trap = new TcpListener(IPAddress.Loopback, 0); trap.Start();
var gatewayPort = ((IPEndPoint)trap.LocalEndpoint).Port;
var connected = true;
var commands = new List<JsonObject>();
var path = Path.Combine(directory, "devices.db");
var registry = new LightModuleRegistry();
using var labelsLogger = NullLoggerFactory.Instance;
await using var gateway = new AioiGateway("127.0.0.1", gatewayPort, NullLogger<AioiGateway>.Instance);
using var engine = new PickingEngine(gateway, registry, NullLogger<PickingEngine>.Instance);
var labels = new DeviceLabelStore(path, NullLogger<DeviceLabelStore>.Instance);
var settings = new AppSettings
{
    Gateway = new GatewaySettings {Host = "127.0.0.1", Port = gatewayPort, BlockNo = "01"},
    Mqtt = new MqttSettings {Enabled = true, Host = "fixture.example", Password = "SECRET-MUST-NOT-LEAK", Username = "fixture",
        ConfirmApi = new() {Enabled=true, Url="https://fixture.example/confirm/events", Token="CONFIRM-API-SECRET-MUST-NOT-LEAK-123456"},
        ClientDbPath = path, ClientId = "fixture-bridge", CommandTopic = "fixture/commands"}
};
var catalog = new DeviceCatalog(path, labels, registry);
using var logs = new WebLogProvider();
var server = new WebClientService(settings, gateway, registry, engine, catalog, logs,
    (bytes, ct) => { commands.Add(JsonNode.Parse(bytes)!.AsObject()); return Task.CompletedTask; }, () => connected,
    NullLogger<WebClientService>.Instance);
try
{
    await server.StartAsync(0, default);
    using var http = new HttpClient {BaseAddress = new Uri(server.Url), Timeout = TimeSpan.FromSeconds(5)};
    async Task<JsonObject> Call(HttpMethod method, string url, JsonObject? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (method != HttpMethod.Get) request.Content = new StringContent(body?.ToJsonString() ?? "", Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        Require(response.IsSuccessStatusCode, $"{method} {url} failed {(int)response.StatusCode}: {raw}");
        return JsonNode.Parse(raw)!.AsObject();
    }
    var page = await http.GetStringAsync("/");
    Require(page.Contains("/static/js/integrated.js") && page.Contains("Dashboard"), "Embedded page missing");
    Require((await http.GetAsync("/static/js/app.js")).Content.Headers.ContentType!.MediaType == "application/javascript", "JS MIME type");
    Require((await http.GetStringAsync("/static/js/integrated.js")).Contains("Bridge owns license"), "Integrated overlay missing");
    Require((await http.GetAsync("/static/css/style.css")).IsSuccessStatusCode, "CSS missing");
    Console.WriteLine("PASS: HTML, JS and CSS embedded in the Bridge assembly");

    var config = await Call(HttpMethod.Get, "/api/config");
    Require(config["integrated"]!.GetValue<bool>(), "Not integrated");
    Require(!config.ToJsonString().Contains("SECRET-MUST-NOT-LEAK") && !config["mqtt"]!.AsObject().ContainsKey("password"), "Credential exposed");
    Require((await Call(HttpMethod.Get, "/api/license"))["has_device_key"]!.GetValue<bool>(), "License status");
    Console.WriteLine("PASS: safe config and license status without reading the OS vault");

    await Call(HttpMethod.Post, "/api/devices", new JsonObject {["address"] = "8", ["label"] = "Bin-8", ["order_no"] = 4});
    Require(labels.GetLabel("0008") == "Bin-8", "MQTT label cache not refreshed");
    await Call(HttpMethod.Patch, "/api/devices/0008", new JsonObject {["order_no"] = 7});
    Require((await Call(HttpMethod.Get, "/api/devices/0008"))["order_no"]!.GetValue<int>() == 7, "Order update missing");
    using (var reopened = new DeviceCatalog(path, labels, new LightModuleRegistry()))
        Require(reopened.Get("0008")!["label"]!.GetValue<string>() == "Bin-8", "Metadata did not survive restart");
    Console.WriteLine("PASS: device metadata CRUD, SQLite persistence and shared MQTT label cache");

    await Call(HttpMethod.Post, "/api/devices/0008/light", new JsonObject {["quantity"] = 5, ["color"] = "BLUE", ["led_mode"] = "FLASH", ["buzz"] = false, ["block_no"] = "01"});
    Require(commands[^1]["type"]!.GetValue<string>() == "light_on" && commands[^1]["color"]!.GetValue<string>() == "Blue"
        && commands[^1]["ledMode"]!.GetValue<string>() == "Flash" && commands[^1]["buzMode"]!.GetValue<string>() == "Off", "Light command shape changed");
    await Call(HttpMethod.Post, "/api/batch/start", new JsonObject {["block_no"] = "01", ["buzz"] = false,
        ["items"] = new JsonArray(new JsonObject {["address"] = "8", ["quantity"] = 2, ["color"] = "RED", ["led_mode"] = "FLASH"},
            new JsonObject {["address"] = "14", ["quantity"] = 3, ["buz_mode"] = "HIGH_FLASH"})});
    var batch = commands[^1];
    Require(batch["type"]!.GetValue<string>() == "batch_start" && batch["items"]![0]!["buzMode"]!.GetValue<string>() == "Off"
        && batch["items"]![1]!["buzMode"]!.GetValue<string>() == "HighFlash", "Batch buzzer overrides changed");
    await Call(HttpMethod.Post, "/api/batch/finish");
    Require(commands[^1]["blockNo"]!.GetValue<string>() == "01", "Batch finish lost configured block");
    await Call(HttpMethod.Post, "/api/terminate-all");
    Require(commands[^1].ToJsonString() == "{\"type\":\"terminate_all\"}", "Terminate payload changed");
    await Call(HttpMethod.Post, "/api/scan-devices?addr_from=8&addr_to=14");
    Require(commands[^1]["from"]!.GetValue<int>() == 8 && commands[^1]["to"]!.GetValue<int>() == 14, "Scan range changed");
    Require(!trap.Pending(), "Web adapter bypassed the shared command handler and connected directly to controller");
    Console.WriteLine("PASS: light, batch, scan and termination retain command payloads; adapter opens no controller connection");

    using var socket = new ClientWebSocket();
    socket.Options.SetRequestHeader("Origin", server.Url);
    await socket.ConnectAsync(new Uri(server.Url.Replace("http:", "ws:") + "/ws"), default);
    async Task<JsonObject> ReceiveUntil(string type, Func<JsonObject, bool>? matches = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bytes = new byte[65536];
        while (true)
        {
            using var full = new MemoryStream();
            WebSocketReceiveResult received;
            do { received = await socket.ReceiveAsync(bytes, timeout.Token); full.Write(bytes, 0, received.Count); } while (!received.EndOfMessage);
            var frame = JsonNode.Parse(full.ToArray())!.AsObject();
            if (frame["type"]?.GetValue<string>() == type && (matches is null || matches(frame))) return frame;
        }
    }
    var init = await ReceiveUntil("init");
    Require(init["data"]!["devices"]!.AsArray().Count == 1, "WS initial devices missing");
    registry.SetInstructed(new PickingInstruction {Address = "0008", Quantity = 5, Color = LightColor.Blue, BlockNo = "01"});
    // A periodic snapshot queued before SetInstructed can arrive first.
    var update = await ReceiveUntil("state_update", frame =>
        frame["data"]![0]!["status"]!.GetValue<string>() == "OPERATING");
    Require(update["data"]![0]!["status"]!.GetValue<string>() == "OPERATING", "WS does not reflect core state");
    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    Console.WriteLine("PASS: WebSocket init and live device state share Bridge registry");

    foreach (var (header, value) in new[] {("Origin", "https://attacker.example"), ("Host", "attacker.example:" + new Uri(server.Url).Port)})
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/config"); request.Headers.TryAddWithoutValidation(header, value);
        using var response = await http.SendAsync(request); Require(response.StatusCode == HttpStatusCode.Forbidden, header + " not restricted");
    }
    using (var form = await http.PostAsync("/api/terminate-all", new StringContent("x=y", Encoding.UTF8, "application/x-www-form-urlencoded")))
        Require(form.StatusCode == HttpStatusCode.UnsupportedMediaType, "Form POST was accepted");
    using (var forbidden = await http.PutAsync("/api/config", new StringContent("{}", Encoding.UTF8, "application/json")))
        Require(forbidden.StatusCode == HttpStatusCode.Conflict, "Operational config mutation accepted");
    Console.WriteLine("PASS: loopback host/origin enforcement, JSON requirement and immutable connection config");

    var before = commands.Count;
    await Call(HttpMethod.Post, "/api/disconnect");
    using (var disabled = await http.PostAsync("/api/terminate-all", new StringContent("{}", Encoding.UTF8, "application/json")))
        Require(disabled.StatusCode == HttpStatusCode.ServiceUnavailable, "Disconnected UI sent commands");
    Require(commands.Count == before, "Disconnected request reached command sink");
    await Call(HttpMethod.Post, "/api/connect");
    connected = false;
    using (var offline = await http.PostAsync("/api/terminate-all", new StringContent("{}", Encoding.UTF8, "application/json")))
        Require(offline.StatusCode == HttpStatusCode.ServiceUnavailable, "Offline broker accepted commands");
    Require(!trap.Pending(), "Unexpected hardware connection");
    Console.WriteLine("PASS: disconnect and offline broker fail closed without stopping/resetting hardware");
    var offlinePath = Path.Combine(directory, "missing", "offline", "events.jsonl");
    registry.SetInstructed(new PickingInstruction {Address = "0091", Quantity = 1, BlockNo = "01", CorrelationId = "ROW-A"});
    registry.SetInstructed(new PickingInstruction {Address = "0092", Quantity = 1, BlockNo = "02", CorrelationId = "ROW-B"});
    var scopedStatus = JsonNode.Parse(await http.GetStringAsync("/api/batch/status?block_no=02"))!;
    Require(scopedStatus["pending"]!.AsArray().Count == 1 && (string?)scopedStatus["pending"]![0] == "0092",
        "Batch status mixed two rows");
    Console.WriteLine("PASS: batch status is isolated by the selected block");
    await using (var offline = new OfflineBufferService(new MqttSettings {OfflineBufferPath = offlinePath},
        engine, registry, NullLogger<OfflineBufferService>.Instance, telemetry: telemetry))
    {
        offline.Start();
        registry.SetInstructed(new PickingInstruction {Address = "0017", Quantity = 3});
    }
    Require(File.Exists(offlinePath) && JsonNode.Parse(File.ReadAllLines(offlinePath)[0])!["topic"] is not null,
        "Buffer-only service did not create parent directory and preserve JSONL envelope");
    Console.WriteLine("PASS: buffer-only service creates nested parent directories before writing");

    await using (var apiUi = new WebClientService(new AppSettings {Gateway=settings.Gateway,
        Mqtt=new MqttSettings {Enabled=false,ClientDbPath=path}}, gateway, registry, engine, catalog, logs,
        (bytes,ct)=>{commands.Add(JsonNode.Parse(bytes)!.AsObject());return Task.CompletedTask;},()=>false,
        NullLogger<WebClientService>.Instance))
    {
        await apiUi.StartAsync(0,default);
        using var apiHttp = new HttpClient {BaseAddress=new Uri(apiUi.Url)};
        var status = JsonNode.Parse(await apiHttp.GetStringAsync("/api/status"))!;
        Require(status["mqtt_enabled"]!.GetValue<bool>()==false && status["mqtt_connected"]!.GetValue<bool>()==false,
            "API-only UI misreports MQTT connection");
        using var result = await apiHttp.PostAsync("/api/scan-devices?addr_from=1&addr_to=2",new StringContent("{}",Encoding.UTF8,"application/json"));
        Require(result.IsSuccessStatusCode && commands[^1]["type"]!.GetValue<string>()=="scan_devices",
            "API-only local controls still require MQTT");
    }
    Console.WriteLine("PASS API-only web UI exposes disabled MQTT and local controls work without broker");

    var publisherPath = Path.Combine(directory, "missing-publisher", "events.jsonl");
    var blackhole = new TcpListener(IPAddress.Loopback, 0); blackhole.Start();
    try
    {
        await using var mqtt = new MqttBridgeService(new MqttSettings {Enabled = true, Host = "127.0.0.1",
            Port = ((IPEndPoint)blackhole.LocalEndpoint).Port, Tls = new MqttTlsSettings {Enabled = false},
            OfflineBufferPath = publisherPath}, gateway, engine, registry, new P2L.Core.Services.PreemptionGate(), null,
            labels, new P2L.Core.Services.DeviceScanService(gateway, registry, NullLogger<P2L.Core.Services.DeviceScanService>.Instance),
            NullLoggerFactory.Instance, telemetry: telemetry);
        await mqtt.StartAsync();
        var dispatcher = typeof(MqttBridgeService).GetField("_dispatcher", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(mqtt)!;
        var handle = dispatcher.GetType().GetMethod("HandleAsync")!;
        await (Task)handle.Invoke(dispatcher, new object[] {Encoding.UTF8.GetBytes("{\"type\":\"get_snapshot\",\"correlationId\":\"SESSION-TEST\"}"), CancellationToken.None})!;
        await (Task)handle.Invoke(dispatcher, new object[] {Encoding.UTF8.GetBytes("malformed-command"), CancellationToken.None})!;
        registry.SetInstructed(new PickingInstruction {Address = "0018", Quantity = 4});
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(publisherPath) || !File.ReadAllText(publisherPath).EndsWith('\n'))
        {
            if (DateTime.UtcNow > until) throw new Exception("MQTT publisher did not persist offline event");
            await Task.Delay(25);
        }
        Require(JsonNode.Parse(File.ReadAllLines(publisherPath)[0])!["payload"] is not null,
            "MQTT offline JSONL envelope changed");
    }
    finally { blackhole.Stop(); }
    Console.WriteLine("PASS: disconnected MQTT publisher creates parent directories and persists events");
    Require(telemetry.Entries.Any(x => x.Kind == "command_received" && x.Payload.Contains("SESSION-TEST")) &&
        telemetry.Entries.Any(x => x.Kind == "command_received" && x.Payload == "malformed-command") &&
        telemetry.Entries.Any(x => x.Kind == "event" && x.Payload.Contains("light_changed")),
        "Telemetry must capture commands before validation and events even while MQTT is offline");
    Console.WriteLine("PASS: command and event telemetry hooks preserve offline dispatch behavior");
    Console.WriteLine("All 11 integrated/buffer/telemetry checks passed. Local fixtures only; no UAT/keychain/controller accessed.");
}
finally
{
    await server.DisposeAsync(); catalog.Dispose(); trap.Stop();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(directory, true);
}

sealed class CaptureTelemetry : P2L.Shared.IP2LTelemetry
{
    public readonly System.Collections.Concurrent.ConcurrentQueue<(string Kind, string? Topic, string Payload)> Entries = new();
    public void Record(string kind, string? topic, string payload) => Entries.Enqueue((kind, topic, payload));
}
