using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using P2L.CLI.Configuration;
using P2L.Core;
using P2L.Hardware;
using P2L.Shared.Enums;
using P2L.Shared.Models;

namespace P2L.CLI.Web;

/// <summary>Loopback-only UI hosted in the Bridge process. Never reads a key or exposes broker credentials.</summary>
internal sealed class WebClientService : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly AioiGateway _gateway;
    private readonly LightModuleRegistry _registry;
    private readonly PickingEngine _engine;
    private readonly DeviceCatalog _devices;
    private readonly WebLogProvider _logs;
    private readonly Func<byte[], CancellationToken, Task> _dispatch;
    private readonly Func<bool> _mqttConnected;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, Channel<string>> _sockets = new();
    private readonly CancellationTokenSource _stop = new();
    private WebApplication? _app;
    private Task? _pump;
    private int _dirty = 1;
    private volatile bool _visible = true;
    public string Url { get; private set; } = "";

    public WebClientService(AppSettings settings, AioiGateway gateway, LightModuleRegistry registry, PickingEngine engine,
        DeviceCatalog devices, WebLogProvider logs, Func<byte[], CancellationToken, Task> dispatch,
        Func<bool> mqttConnected, ILogger<WebClientService> logger)
    {
        _settings = settings; _gateway = gateway; _registry = registry; _engine = engine;
        _devices = devices; _logs = logs; _dispatch = dispatch; _mqttConnected = mqttConnected; _logger = logger;
    }

    public async Task StartAsync(int port, CancellationToken ct)
    {
        if (_app is not null) throw new InvalidOperationException("Web client already started");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions {Args = [], ApplicationName = typeof(WebClientService).Assembly.GetName().Name});
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = 1024 * 1024;
        });
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(HandleAsync);
        try { await app.StartAsync(ct); }
        catch { await app.DisposeAsync(); throw; }
        _app = app;
        Url = app.Urls.Single().TrimEnd('/');
        _registry.StateChanged += OnState;
        _engine.ItemConfirmed += OnConfirmation;
        _engine.BlockBatchCompleted += OnBatch;
        _logs.EntryAdded += OnLog;
        _pump = PumpAsync(_stop.Token);
        _logger.LogInformation("Local P2L client: {Url}", Url);
    }
    private void OnState(LightModuleState state) => Interlocked.Exchange(ref _dirty, 1);
    private void OnLog(JsonObject entry) => Broadcast("log", entry.DeepClone());
    private void OnBatch(string blockNo, string? correlationId) => Broadcast("batch_completed", new JsonObject {["correlation_id"] = correlationId, ["block_no"] = blockNo});
    private void OnConfirmation(ConfirmationEvent evt, LightModuleState? state) => Broadcast("confirmation", new JsonObject
    {
        ["address"] = evt.Address, ["status"] = evt.Status.ToString().ToUpperInvariant(), ["adjusted_display"] = evt.AdjustedDisplay,
        ["received_at"] = evt.ReceivedAt.ToString("O"), ["correlation_id"] = state?.CorrelationId, ["block_no"] = state?.BlockNo, ["display_data"] = state?.DisplayData
    });
    private JsonObject Connection() => new()
    {
        ["connected"] = true, ["mqtt_connected"] = _visible && _mqttConnected(), ["gateway_connected"] = _gateway.IsConnected,
        ["host"] = _settings.Mqtt.Host, ["port"] = _settings.Mqtt.Port,
        ["block_no"] = _settings.Gateway.BlockNo,
        ["block_nos"] = new JsonArray((_settings.Gateway.BlockNos ?? new[] {_settings.Gateway.BlockNo}).Select(b => (JsonNode)JsonValue.Create(b)!).ToArray())
    };
    private JsonObject Status()
    {
        var result = Connection();
        result["total_devices"] = _devices.Snapshot().Count;
        result["active_devices"] = _registry.ActiveCount;
        result["pending_confirm"] = _registry.PendingConfirmCount;
        result["integrated"] = true;
        return result;
    }
    private void Broadcast(string type, JsonNode data)
    {
        var json = new JsonObject {["type"] = type, ["data"] = data}.ToJsonString();
        foreach (var channel in _sockets.Values) channel.Writer.TryWrite(json);
    }
    private async Task PumpAsync(CancellationToken ct)
    {
        string? previous = null;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var status = Connection().ToJsonString();
                if (status != previous)
                {
                    previous = status;
                    Broadcast("connection_changed", Connection());
                    Broadcast("bridge_status", new JsonObject {["online"] = true, ["gateway_connected"] = _gateway.IsConnected});
                }
                if (Interlocked.Exchange(ref _dirty, 0) != 0) Broadcast("state_update", _devices.Snapshot());
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var host = request.Host;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        // No proxy headers are trusted. A browser must have the exact local origin.
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)
            || (host.Host != "127.0.0.1" && host.Host != "localhost") || host.Port != new Uri(Url).Port
            || (request.Headers.Origin.Count > 0 && (request.Headers.Origin.Count != 1 || request.Headers.Origin[0] != $"http://{host}")))
        { await Reply(context, new JsonObject {["detail"] = "Local origin required"}, 403); return; }
        if (request.Method is not ("GET" or "HEAD") && !(request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? false))
        { await Reply(context, new JsonObject {["detail"] = "JSON required"}, 415); return; }
        try
        {
            if (request.Path == "/ws" && context.WebSockets.IsWebSocketRequest) { await SocketAsync(context); return; }
            if (request.Method is "GET" or "HEAD" && (request.Path == "/" || request.Path.StartsWithSegments("/static")))
            { await AssetAsync(context); return; }
            JsonObject body = new();
            if (request.ContentLength > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
                body = await JsonNode.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted) as JsonObject ?? throw new ArgumentException("JSON object required");
            await ApiAsync(context, body);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or FormatException or OverflowException)
        { if (!context.Response.HasStarted) await Reply(context, new JsonObject {["detail"] = "Invalid request or Bridge unavailable"}, ex is InvalidOperationException ? 503 : 400); }
        catch (BadHttpRequestException ex)
        { if (!context.Response.HasStarted) await Reply(context, new JsonObject {["detail"] = "Invalid HTTP request"}, ex.StatusCode); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception)
        { if (!context.Response.HasStarted) await Reply(context, new JsonObject {["detail"] = "Local client request failed"}, 500); }
    }
    private async Task AssetAsync(HttpContext context)
    {
        var path = context.Request.Path == "/" ? "index.html" : context.Request.Path.Value!.TrimStart('/');
        await using var stream = typeof(WebClientService).Assembly.GetManifestResourceStream("P2L.Web/" + path);
        if (stream is null) { context.Response.StatusCode = 404; return; }
        var extension = Path.GetExtension(path);
        context.Response.ContentType = extension switch {".js" => "application/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8", ".html" => "text/html; charset=utf-8", ".svg" => "image/svg+xml", _ => "application/octet-stream"};
        if (context.Request.Method == "HEAD") return;
        if (path == "index.html")
        {
            using var reader = new StreamReader(stream);
            var page = await reader.ReadToEndAsync(context.RequestAborted);
            page = page.Replace("</head>", "<style>section[aria-label=License],#btn-save-config,#btn-save-appsettings,#btn-p2l-svc-start,#btn-p2l-svc-stop{display:none!important}</style></head>")
                .Replace("</body>", "<script src=\"/static/js/integrated.js\"></script></body>");
            await context.Response.WriteAsync(page, context.RequestAborted);
        }
        else await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    private static Task Reply(HttpContext context, JsonNode result, int status = 200)
    {
        context.Response.StatusCode = status; context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(result.ToJsonString(), context.RequestAborted);
    }
    private async Task SendAsync(JsonObject command, CancellationToken ct)
    {
        if (!_visible || !_mqttConnected()) throw new InvalidOperationException("MQTT unavailable");
        await _dispatch(Encoding.UTF8.GetBytes(command.ToJsonString()), ct);
    }
    private async Task ApiAsync(HttpContext context, JsonObject body)
    {
        var request = context.Request;
        var path = request.Path.Value!;
        var method = request.Method;
        JsonNode result;
        if (method == "GET" && path == "/api/status") result = Status();
        else if (method == "GET" && path == "/api/license") result = new JsonObject {["has_device_key"] = true, ["requires_online_validation"] = true, ["integrated"] = true};
        else if (method == "GET" && path is "/api/service/status" or "/api/p2l-service/status") result = new JsonObject {["running"] = true, ["binary_path"] = Environment.ProcessPath};
        else if (method == "GET" && path == "/api/config") result = new JsonObject
        {
            ["integrated"] = true, ["block_no"] = _settings.Gateway.BlockNo,
            ["block_nos"] = new JsonArray((_settings.Gateway.BlockNos ?? new[] {_settings.Gateway.BlockNo}).Select(b => (JsonNode)JsonValue.Create(b)!).ToArray()),
            ["mqtt"] = new JsonObject {["enabled"] = true, ["host"] = _settings.Mqtt.Host, ["port"] = _settings.Mqtt.Port, ["client_id"] = _settings.Mqtt.ClientId,
                ["command_topic"] = _settings.Mqtt.CommandTopic, ["event_topic"] = _settings.Mqtt.EventTopic, ["devices_topic"] = _settings.Mqtt.DevicesTopic,
                ["status_topic"] = _settings.Mqtt.StatusTopic, ["tls"] = new JsonObject {["enabled"] = _settings.Mqtt.Tls.Enabled}},
            ["service"] = new JsonObject {["binary_path"] = Environment.ProcessPath, ["args"] = new JsonArray("serve"), ["auto_start"] = false}
        };
        else if (method == "GET" && path == "/api/logs") result = new JsonObject {["logs"] = _logs.Snapshot()};
        else if (method == "DELETE" && path == "/api/logs") { _logs.Clear(); result = new JsonObject {["status"] = "cleared"}; }
        else if (method == "GET" && path == "/api/devices") result = new JsonObject {["devices"] = _devices.Snapshot()};
        else if (method == "POST" && path == "/api/devices")
        {
            result = _devices.Upsert(DeviceCatalog.Address(WebCommandMapper.Text(body, "address")), WebCommandMapper.Text(body, "label"), body["order_no"]?.GetValue<int>() ?? 0);
            OnState(null!);
        }
        else if (method == "DELETE" && path == "/api/devices") { _devices.Remove(); OnState(null!); result = new JsonObject {["status"] = "cleared"}; }
        else if (path.StartsWith("/api/devices/", StringComparison.Ordinal))
        {
            var parts = path.Split('/');
            var address = DeviceCatalog.Address(parts[3]);
            if (method == "GET" && parts.Length == 4) result = _devices.Get(address) ?? throw new ArgumentException("Unknown device");
            else if (method == "PATCH" && parts.Length == 4)
            {
                result = _devices.Upsert(address, body["label"]?.GetValue<string>(), body["order_no"]?.GetValue<int>()); OnState(null!);
            }
            else if (method == "DELETE" && parts.Length == 4) { _devices.Remove(address); OnState(null!); result = new JsonObject {["status"] = "removed"}; }
            else if (method == "POST" && parts.Length == 5 && parts[4] is "light" or "off")
            {
                if (body["block_no"] is null) body["block_no"] = _settings.Gateway.BlockNo;
                await SendAsync(parts[4] == "light" ? WebCommandMapper.Light(address, body) : new JsonObject {["type"] = "light_off", ["address"] = address}, context.RequestAborted);
                result = new JsonObject {["status"] = "sent", ["address"] = address};
            }
            else { await Reply(context, new JsonObject {["detail"] = "Not found"}, 404); return; }
        }
        else if (method == "GET" && path == "/api/batch/status")
        {
            var block = request.Query["block_no"].FirstOrDefault() ?? _settings.Gateway.BlockNo;
            _engine.ValidateBlock(block);
            var modules = _registry.All.Where(m => m.BlockNo == block).ToArray();
            var active = modules.Any(m => m.Status is ModuleStatus.Operating or ModuleStatus.Operating2);
            var pending = new JsonArray(modules.Where(m => m.Status is ModuleStatus.Operating or ModuleStatus.Operating2).Select(m => (JsonNode)JsonValue.Create(m.Address)!).ToArray());
            result = new JsonObject {["block_no"] = block, ["active"] = active, ["pending"] = pending, ["complete"] = !active};
        }
        else if (method == "POST" && path == "/api/connect")
        {
            if (!_mqttConnected()) throw new InvalidOperationException("MQTT unavailable");
            _visible = true; result = new JsonObject {["status"] = "connected", ["service_started"] = false};
        }
        else if (method == "POST" && path == "/api/disconnect") { _visible = false; result = new JsonObject {["status"] = "disconnected"}; }
        else if (method == "POST" && path == "/api/batch/start")
        { if (body["block_no"] is null) body["block_no"] = _settings.Gateway.BlockNo; var command = WebCommandMapper.Batch(body); await SendAsync(command, context.RequestAborted); result = new JsonObject {["status"] = "sent", ["count"] = command["items"]!.AsArray().Count}; }
        else if (method == "POST" && path is "/api/batch/finish" or "/api/terminate-all")
        {
            var command = new JsonObject {["type"] = path.EndsWith("finish", StringComparison.Ordinal) ? "batch_finish" : "terminate_all"};
            if (path.EndsWith("finish", StringComparison.Ordinal)) command["blockNo"] = WebCommandMapper.Text(body, "block_no", _settings.Gateway.BlockNo);
            await SendAsync(command, context.RequestAborted);
            result = new JsonObject {["status"] = "sent"};
        }
        else if (method == "POST" && path == "/api/scan-devices")
        {
            var from = int.Parse(request.Query["addr_from"].FirstOrDefault() ?? "1");
            var to = int.Parse(request.Query["addr_to"].FirstOrDefault() ?? "99");
            if (from < 1 || to < from || to > 9999) throw new ArgumentException("Invalid scan range");
            await SendAsync(new JsonObject {["type"] = "scan_devices", ["from"] = from, ["to"] = to}, context.RequestAborted);
            result = new JsonObject {["status"] = "scanning"};
        }
        else if (path is "/api/config" or "/api/appsettings" or "/api/license/activate" or "/api/self/restart" || path.StartsWith("/api/service/", StringComparison.Ordinal) || path.StartsWith("/api/p2l-service/", StringComparison.Ordinal))
        { await Reply(context, new JsonObject {["detail"] = "Managed by p2l serve. Configure license in authen; stop this process with Ctrl+C."}, 409); return; }
        else { await Reply(context, new JsonObject {["detail"] = "Not found"}, 404); return; }
        await Reply(context, result);
    }
    private async Task SocketAsync(HttpContext context)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(64) {FullMode = BoundedChannelFullMode.DropOldest});
        var id = Guid.NewGuid(); _sockets[id] = channel;
        var init = Connection(); init["devices"] = _devices.Snapshot();
        channel.Writer.TryWrite(new JsonObject {["type"] = "init", ["data"] = init}.ToJsonString());
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stop.Token);
        async Task Send()
        {
            await foreach (var message in channel.Reader.ReadAllAsync(ct.Token))
                await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, ct.Token);
        }
        var sender = Send();
        try
        {
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open)
            {
                var response = await socket.ReceiveAsync(buffer, ct.Token);
                if (response.MessageType == WebSocketMessageType.Close) break;
                if (response.MessageType == WebSocketMessageType.Text && Encoding.UTF8.GetString(buffer, 0, response.Count) == "ping") channel.Writer.TryWrite("pong");
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        finally
        {
            _sockets.TryRemove(id, out _); channel.Writer.TryComplete(); await ct.CancelAsync();
            try { await sender; } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
            if (socket.State == WebSocketState.CloseReceived)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closeTimeout.Token); }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _registry.StateChanged -= OnState; _engine.ItemConfirmed -= OnConfirmation; _engine.BlockBatchCompleted -= OnBatch; _logs.EntryAdded -= OnLog;
        await _stop.CancelAsync();
        if (_pump is not null) await _pump;
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        _stop.Dispose();
    }
}
