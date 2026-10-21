using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using P2L.Core;
using P2L.Core.Services;
using P2L.Hardware;
using P2L.Mqtt.Configuration;
using P2L.Mqtt.Messages;
using P2L.Mqtt.Transport;

namespace P2L.Mqtt;

/// <summary>One engine/receipt/outbox owner with warm, independent transport links.</summary>
public sealed class MqttBridgeService : IAsyncDisposable, IDisposable
{
    private readonly AioiGateway _gateway;
    private readonly MqttCommandDispatcher _dispatcher;
    private readonly MqttEventPublisher _publisher;
    private readonly BridgeTransportCoordinator _transport;
    private readonly CsvPickTaskRunner? _csvRunner;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly CommandAdmissionQueue _commands;
    private readonly CommandReceiptStore _receipts;
    private int _started;
    private int _disposed;
    private CancellationTokenRegistration _registration;

    public MqttBridgeService(MqttSettings settings, AioiGateway gateway, PickingEngine engine,
        LightModuleRegistry registry, PreemptionGate gate, CsvPickTaskRunner? csvRunner,
        DeviceLabelStore labelStore, DeviceScanService scanService, ILoggerFactory loggerFactory)
        : this(settings, gateway, engine, registry, gate, csvRunner, labelStore, scanService, loggerFactory,
               CreateChannels(settings, loggerFactory)) { }

    internal MqttBridgeService(MqttSettings settings, AioiGateway gateway, PickingEngine engine,
        LightModuleRegistry registry, PreemptionGate gate, CsvPickTaskRunner? csvRunner,
        DeviceLabelStore labelStore, DeviceScanService scanService, ILoggerFactory loggerFactory,
        IReadOnlyList<IBridgeChannel> channels)
    {
        _gateway = gateway; _csvRunner = csvRunner;
        _log = loggerFactory.CreateLogger<MqttBridgeService>();
        _transport = new BridgeTransportCoordinator(channels, settings, loggerFactory.CreateLogger<BridgeTransportCoordinator>());
        _publisher = new MqttEventPublisher(_transport, settings, engine, registry, labelStore,
            loggerFactory.CreateLogger<MqttEventPublisher>());
        _receipts = new CommandReceiptStore(settings.CommandReceiptPath);
        _dispatcher = new MqttCommandDispatcher(engine, _publisher, gate, csvRunner, labelStore, scanService,
            loggerFactory.CreateLogger<MqttCommandDispatcher>(), _receipts, settings.DiagnosticTimeoutMs);
        _commands = new CommandAdmissionQueue(settings.CommandQueueCapacity, _dispatcher.HandleAsync, _cts.Token,
            ex => _log.LogError(ex, "[P2L] Command outcome could not be persisted; WMS must retain the pending intent."), settings.CommandExecutionTimeoutMs);
        _transport.CommandReceived += ScheduleCommandAsync;
        _gateway.ConnectionChanged += OnGatewayConnectionChanged;
        if (_csvRunner is not null) _csvRunner.TaskEvent += OnTaskEvent;
    }

    private static IReadOnlyList<IBridgeChannel> CreateChannels(MqttSettings settings, ILoggerFactory logs)
    {
        var channels = new List<IBridgeChannel>();
        if (settings.Enabled) channels.Add(new MqttChannel("primary", settings, logs.CreateLogger<MqttChannel>()));
        if (settings.BackupBroker?.Enabled == true)
            channels.Add(new MqttChannel("backup", settings.BackupBroker, logs.CreateLogger<MqttChannel>()));
        return channels;
    }

    private Task ScheduleCommandAsync(ChannelMessage message)
    {
        var result = _commands.TryEnqueue(message.Payload,
            () => _publisher.EnqueueCommandResult(GetQueueFullResult(_receipts, message.Payload)));
        if (result == CommandAdmission.Stopped) throw new OperationCanceledException("Bridge is stopping.");
        if (result == CommandAdmission.Conflict)
            _publisher.EnqueueCommandResult(GetQueueFullResult(_receipts, message.Payload, pendingConflict: true));
        return Task.CompletedTask;
    }

    internal static CommandResultEvt GetQueueFullResult(CommandReceiptStore receipts, byte[] payload, bool pendingConflict = false)
    {
        string? commandId = null, correlationId = null, type = null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("commandId", out var id) && id.ValueKind == JsonValueKind.String) commandId = id.GetString();
                if (doc.RootElement.TryGetProperty("correlationId", out var correlation) && correlation.ValueKind == JsonValueKind.String) correlationId = correlation.GetString();
                if (doc.RootElement.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String) type = kind.GetString();
            }
        }
        catch (JsonException) { }
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        var key = commandId is not null ? "command:" + commandId
            : !string.IsNullOrWhiteSpace(correlationId) && (type is "batch_start" or "light_on") ? "legacy:" + hash : null;
        var rejection = new CommandResultEvt { Type = "command_result", CommandId = commandId, CorrelationId = correlationId,
            Status = "rejected", ErrorCode = pendingConflict ? "COMMAND_ID_CONFLICT" : "BRIDGE_BUSY",
            Message = pendingConflict ? "Command ID is already pending with another payload."
                : "Bridge command queue is full; create a new attempt only after receiving this known rejection." };
        if (key is null) return rejection;
        // A busy receipt is terminal: a delayed rejection must never race an
        // accepted retry of that same ID after queue space becomes available.
        if (!pendingConflict) return receipts.GetExistingOrReject(key, hash, commandId, correlationId, rejection);
        return receipts.GetExisting(key, hash, commandId, correlationId) ?? rejection;
    }

    private void OnGatewayConnectionChanged(bool connected) => _publisher.EnqueueStatus(true, connected);
    private void OnTaskEvent(CsvTaskEvent evt) => _publisher.EnqueueTaskEvent(evt);

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _registration = ct.Register(() => _cts.Cancel());
        _publisher.Start();
        await _transport.StartAsync(_cts.Token);
        _publisher.EnqueueStatus(true, _gateway.IsConnected);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _registration.Dispose();
        _transport.CommandReceived -= ScheduleCommandAsync;
        _gateway.ConnectionChanged -= OnGatewayConnectionChanged;
        if (_csvRunner is not null) _csvRunner.TaskEvent -= OnTaskEvent;
        var drained = _commands.StopAsync();
        await _cts.CancelAsync();
        await drained;
        try { await _publisher.DisposeAsync(); }
        finally { await _transport.DisposeAsync(); _cts.Dispose(); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    internal static MqttClientOptions BuildOptions(MqttSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.ClientId))
            throw new ArgumentException("MQTT client ID must be stable and non-empty.");
        if (s.SessionExpirySeconds is 0 or > 86400)
            throw new ArgumentOutOfRangeException(nameof(s.SessionExpirySeconds), "MQTT session expiry must be 1..86400 seconds.");
        Uri? webSocketUri = null;
        if (!string.IsNullOrWhiteSpace(s.WebSocketUrl))
        {
            if (!Uri.TryCreate(s.WebSocketUrl, UriKind.Absolute, out webSocketUri)
                || !webSocketUri.IsWellFormedOriginalString()
                || webSocketUri.Scheme is not ("ws" or "wss")
                || string.IsNullOrWhiteSpace(webSocketUri.Host)
                || !string.IsNullOrEmpty(webSocketUri.UserInfo)
                || !string.IsNullOrEmpty(webSocketUri.Fragment))
                throw new ArgumentException("MQTT WebSocket URL must be an absolute ws/wss endpoint without credentials or fragment.", nameof(s.WebSocketUrl));
        }
        // Build LWT payload: broker publishes this automatically on ungraceful disconnect
        var lwtPayload = JsonSerializer.SerializeToUtf8Bytes(
            new BridgeStatusEvt { Type = "bridge_status", Online = false, GatewayConnected = false },
            P2LMqttJsonContext.Default.BridgeStatusEvt);

        var builder = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithSessionExpiryInterval(s.SessionExpirySeconds)
            .WithClientId(s.ClientId)
            .WithWillTopic(s.StatusTopic)
            .WithWillPayload(lwtPayload)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithWillRetain(true)
            .WithCleanSession(false); // resume session within the bounded MQTT 5 expiry

        if (webSocketUri is null)
            builder.WithTcpServer(s.Host, s.Port);
        else
            builder.WithWebSocketServer(ws => ws.WithUri(webSocketUri.AbsoluteUri).WithSubProtocols(new[] { "mqtt" }));

        if (s.Username is not null)
            builder.WithCredentials(s.Username, s.Password);

        bool useTls = webSocketUri is null ? s.Tls.Enabled : webSocketUri.Scheme == "wss";
        if (useTls)
            builder.WithTlsOptions(tls =>
            {
                if (s.Tls.AllowUntrustedCerts)
                {
                    tls.WithAllowUntrustedCertificates();
                    tls.WithIgnoreCertificateChainErrors();
                    if (webSocketUri is not null)
                        tls.WithCertificateValidationHandler(_ => true);
                }
                else if (s.Tls.CaCertPath is null)
                {
                    // MQTTnet v5 does not fall back to the OS trust store by default on macOS.
                    // Explicitly delegate to .NET's built-in SSL validation (OS keychain).
                    tls.WithCertificateValidationHandler(ctx =>
                        ctx.SslPolicyErrors == SslPolicyErrors.None);
                }

                if (s.Tls.CaCertPath is not null)
                {
                    var ca = new X509Certificate2(s.Tls.CaCertPath);
                    tls.WithTrustChain(new X509Certificate2Collection { ca });
                    if (webSocketUri is not null && !s.Tls.AllowUntrustedCerts)
                        tls.WithCertificateValidationHandler(ctx =>
                        {
                            // MQTTnet's WebSocket adapter does not apply TrustChain.
                            // Keep the TLS hostname check and validate the server chain against this CA.
                            if (ctx.Certificate is null
                                || (ctx.SslPolicyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
                                return false;
                            using var certificate = X509CertificateLoader.LoadCertificate(ctx.Certificate.GetRawCertData());
                            using var chain = new X509Chain();
                            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                            chain.ChainPolicy.CustomTrustStore.Add(ca);
                            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                            chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
                            if (ctx.Chain is not null)
                                foreach (var element in ctx.Chain.ChainElements)
                                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                            return chain.Build(certificate);
                        });
                }

                if (s.Tls.ClientCertPath is not null)
                {
                    var cert = LoadClientCertificate(s.Tls);
                    tls.WithClientCertificates(new[] { cert });
                }
            });
        else if (webSocketUri is not null)
            builder.WithTlsOptions(tls => tls.UseTls(false));

        return builder.Build();
    }

    private static X509Certificate2 LoadClientCertificate(MqttTlsSettings tls)
    {
        // PFX / PKCS#12
        if (tls.ClientCertPath!.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
            tls.ClientCertPath.EndsWith(".p12", StringComparison.OrdinalIgnoreCase))
        {
            return tls.CertPassword is not null
                ? new X509Certificate2(tls.ClientCertPath, tls.CertPassword)
                : new X509Certificate2(tls.ClientCertPath);
        }

        // PEM — cert and key in separate files
        if (tls.ClientKeyPath is not null)
            return X509Certificate2.CreateFromPemFile(tls.ClientCertPath, tls.ClientKeyPath);

        // PEM — cert and key bundled in one file
        return X509Certificate2.CreateFromPemFile(tls.ClientCertPath);
    }
}
