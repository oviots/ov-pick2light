using System.Buffers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using P2L.Core;
using P2L.Hardware;
using P2L.Mqtt.Configuration;
using P2L.Mqtt.Messages;

namespace P2L.Mqtt;

/// <summary>
/// Lifecycle owner for the MQTT bridge.
/// Manages the TLS-encrypted connection to the broker, subscribes to command topics,
/// and delegates to <see cref="MqttCommandDispatcher"/> / <see cref="MqttEventPublisher"/>.
///
/// Pattern: plain class with <c>StartAsync()</c> / <c>DisposeAsync()</c>
/// (same as <c>HeartbeatService</c> — no Microsoft.Extensions.Hosting dependency).
/// </summary>
public sealed class MqttBridgeService : IAsyncDisposable, IDisposable
{
    private readonly MqttSettings          _settings;
    private readonly AioiGateway           _gateway;
    private readonly MqttCommandDispatcher _dispatcher;
    private readonly MqttEventPublisher    _publisher;
    private readonly ILogger               _log;
    private readonly IMqttClient           _client;
    private readonly MqttClientOptions     _options;

    private readonly CancellationTokenSource _cts = new();
    private          Task?                   _connectLoop;
    private          TaskCompletionSource    _disconnectedTcs = new();

    public MqttBridgeService(MqttSettings settings, AioiGateway gateway,
                              PickingEngine engine, ILoggerFactory loggerFactory)
    {
        _settings   = settings;
        _gateway    = gateway;
        _log        = loggerFactory.CreateLogger<MqttBridgeService>();
        _client     = new MqttClientFactory().CreateMqttClient();
        _dispatcher = new MqttCommandDispatcher(engine,
                          loggerFactory.CreateLogger<MqttCommandDispatcher>());
        _publisher  = new MqttEventPublisher(_client, settings, engine,
                          loggerFactory.CreateLogger<MqttEventPublisher>());
        _options    = BuildOptions(settings);

        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _client.ConnectedAsync    += OnConnectedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;

        _gateway.ConnectionChanged += connected =>
            _publisher.EnqueueStatus(online: true, gatewayConnected: connected);
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct = default)
    {
        _publisher.Start();
        _connectLoop = Task.Run(() => ConnectLoopAsync(_cts.Token), _cts.Token);
        _log.LogInformation("[MQTT] Bridge starting → {Host}:{Port} (TLS={Tls})",
            _settings.Host, _settings.Port, _settings.Tls.Enabled);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_connectLoop is not null)
            try { await _connectLoop; } catch (OperationCanceledException) { }

        if (_client.IsConnected)
        {
            try
            {
                await _client.DisconnectAsync(
                    new MqttClientDisconnectOptionsBuilder()
                        .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
                        .Build(),
                    CancellationToken.None);
            }
            catch { /* best-effort */ }
        }

        await _publisher.DisposeAsync();
        _client.Dispose();
        _cts.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    // ─── Connect / reconnect loop ─────────────────────────────────────────

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _disconnectedTcs = new TaskCompletionSource();
                await _client.ConnectAsync(_options, ct);
                delay = TimeSpan.FromSeconds(2); // reset back-off on success

                // Wait here until the broker disconnects us
                await _disconnectedTcs.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning("[MQTT] Connection failed: {Err}. Retrying in {D:F0}s",
                    ex.Message, delay.TotalSeconds);
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { break; }

                delay = delay * 2 < TimeSpan.FromMinutes(2)
                    ? delay * 2
                    : TimeSpan.FromMinutes(2);
            }
        }
    }

    // ─── MQTTnet event handlers ───────────────────────────────────────────

    private async Task OnConnectedAsync(MqttClientConnectedEventArgs _)
    {
        _log.LogInformation("[MQTT] Connected to {Host}:{Port}", _settings.Host, _settings.Port);

        // Subscribe to command topic (QoS 1)
        var sub = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f
                .WithTopic(_settings.CommandTopic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .Build();
        await _client.SubscribeAsync(sub, CancellationToken.None);

        // Publish online status
        _publisher.EnqueueStatus(online: true, gatewayConnected: _gateway.IsConnected);
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        _log.LogWarning("[MQTT] Disconnected: {Reason}", args.Reason);
        _disconnectedTcs.TrySetResult();
        return Task.CompletedTask;
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var payload = args.ApplicationMessage.Payload.ToArray();
        // Fire-and-forget dispatch; errors are caught inside HandleAsync
        _ = _dispatcher.HandleAsync(payload, _cts.Token);
        return Task.CompletedTask;
    }

    // ─── Options builder ─────────────────────────────────────────────────

    private static MqttClientOptions BuildOptions(MqttSettings s)
    {
        // Build LWT payload: broker publishes this automatically on ungraceful disconnect
        var lwtPayload = JsonSerializer.SerializeToUtf8Bytes(
            new BridgeStatusEvt { Type = "bridge_status", Online = false, GatewayConnected = false },
            P2LMqttJsonContext.Default.BridgeStatusEvt);

        var builder = new MqttClientOptionsBuilder()
            .WithClientId(s.ClientId)
            .WithTcpServer(s.Host, s.Port)
            .WithWillTopic(s.StatusTopic)
            .WithWillPayload(lwtPayload)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithWillRetain(true)
            .WithCleanSession(false);   // resume subscriptions across reconnects

        if (s.Username is not null)
            builder.WithCredentials(s.Username, s.Password);

        if (s.Tls.Enabled)
            builder.WithTlsOptions(tls =>
            {
                if (s.Tls.AllowUntrustedCerts)
                {
                    tls.WithAllowUntrustedCertificates();
                    tls.WithIgnoreCertificateChainErrors();
                }

                if (s.Tls.CaCertPath is not null)
                {
                    var ca = new X509Certificate2(s.Tls.CaCertPath);
                    tls.WithTrustChain(new X509Certificate2Collection { ca });
                }

                if (s.Tls.ClientCertPath is not null)
                {
                    var cert = LoadClientCertificate(s.Tls);
                    tls.WithClientCertificates(new[] { cert });
                }
            });

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
