using System.Buffers;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using P2L.Core;
using P2L.Core.Services;
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
    private readonly DeviceScanService     _scanService;
    private readonly ILogger               _log;
    private readonly IMqttClient           _client;
    private readonly MqttClientOptions     _options;
    private readonly PreemptionGate        _gate;
    private readonly CsvPickTaskRunner?    _csvRunner;

    private readonly CancellationTokenSource _cts = new();
    private          Task?                   _connectLoop;
    private          TaskCompletionSource    _disconnectedTcs = new();


    // Network-restore wake-up: cancelled when NetworkAvailabilityChanged fires IsAvailable=true.
    private volatile CancellationTokenSource? _backoffCts;
    private readonly object                   _backoffLock = new();

    public MqttBridgeService(MqttSettings settings, AioiGateway gateway,
                              PickingEngine engine, LightModuleRegistry registry,
                              PreemptionGate gate, CsvPickTaskRunner? csvRunner,
                              DeviceLabelStore labelStore, DeviceScanService scanService,
                              ILoggerFactory loggerFactory)
    {
        _settings    = settings;
        _gateway     = gateway;
        _gate        = gate;
        _csvRunner   = csvRunner;
        _scanService = scanService;
        _log         = loggerFactory.CreateLogger<MqttBridgeService>();
        _client      = new MqttClientFactory().CreateMqttClient();
        _publisher   = new MqttEventPublisher(_client, settings, engine, registry,
                           labelStore,
                           loggerFactory.CreateLogger<MqttEventPublisher>());
        _dispatcher  = new MqttCommandDispatcher(engine, _publisher, gate, csvRunner,
                           labelStore, scanService,
                           loggerFactory.CreateLogger<MqttCommandDispatcher>());
        _options     = BuildOptions(settings);

        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _client.ConnectedAsync    += OnConnectedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;

        _gateway.ConnectionChanged += connected =>
        {
            _publisher.EnqueueStatus(online: true, gatewayConnected: connected);
        };

        if (_csvRunner is not null)
            _csvRunner.TaskEvent += e => _publisher.EnqueueTaskEvent(e);
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
        var delay = TimeSpan.FromSeconds(0.5);

        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _disconnectedTcs = new TaskCompletionSource();

                    // Per-attempt connect timeout (prevents hanging TCP connect when DNS resolves
                    // but the remote is unreachable). Linked to outer ct so service stop still works.
                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connectCts.CancelAfter(TimeSpan.FromSeconds(15));

                    await _client.ConnectAsync(_options, connectCts.Token);
                    delay = TimeSpan.FromSeconds(0.5); // reset back-off on success

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

                    // Back-off delay — cancelled immediately if network is restored.
                    using var backoffCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    lock (_backoffLock) _backoffCts = backoffCts;
                    try
                    {
                        await Task.Delay(delay, backoffCts.Token);

                        // Advance back-off only after a normal delay (not a network-restore wake-up).
                        delay = delay * 2 < TimeSpan.FromMinutes(2)
                            ? delay * 2
                            : TimeSpan.FromMinutes(2);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Network became available — reset back-off and retry immediately.
                        delay = TimeSpan.FromSeconds(0.5);
                        _log.LogInformation("[MQTT] Network restored — retrying immediately.");
                    }
                    catch (OperationCanceledException) { break; }
                    finally
                    {
                        lock (_backoffLock) _backoffCts = null;
                    }
                }
            }
        }
        finally
        {
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) return;
        _log.LogInformation("[MQTT] Network interface became available — waking reconnect loop.");
        CancellationTokenSource? cts;
        lock (_backoffLock) cts = _backoffCts;
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
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

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var payload = args.ApplicationMessage.Payload.ToArray();
        // MQTT QoS acknowledgements should happen after command dispatch, not when
        // the payload is merely accepted into this process.
        await _dispatcher.HandleAsync(payload, _cts.Token);
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
