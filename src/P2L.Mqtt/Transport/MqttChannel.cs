using System.Buffers;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using P2L.Mqtt.Configuration;

namespace P2L.Mqtt.Transport;

internal sealed class MqttChannel : IBridgeChannel
{
    private readonly IMqttClient _client;
    private readonly MqttSettings _settings;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenRegistration _registration;
    private Task? _loop;
    private TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _subscribed;
    private int _started;
    private int _disposed;
    public string Name { get; }
    public bool IsConnected => _subscribed && _client.IsConnected;
    public event Func<ChannelMessage, Task>? MessageReceived;
    public event Action<bool>? ConnectionChanged;

    public MqttChannel(string name, MqttSettings settings, ILogger log)
    {
        Name = name; _settings = settings; _log = log;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += ReceivedAsync;
        _client.ConnectedAsync += ConnectedAsync;
        _client.DisconnectedAsync += DisconnectedAsync;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return Task.CompletedTask;
        _registration = ct.Register(() => _cts.Cancel());
        _loop = Task.Run(() => ConnectLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var delay = 500;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(15));
                await _client.ConnectAsync(MqttBridgeService.BuildOptions(_settings), attempt.Token);
                delay = 500;
                await _disconnected.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _subscribed = false;
                ConnectionChanged?.Invoke(false);
                _log.LogWarning("[P2L] {Channel} connection unavailable: {Reason}", Name, ex.Message);
                if (_client.IsConnected)
                    try { await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), ct); } catch { }
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
                delay = Math.Min(delay * 2, 5000);
            }
        }
    }

    private async Task ConnectedAsync(MqttClientConnectedEventArgs _)
    {
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        ct.CancelAfter(TimeSpan.FromSeconds(15));
        var result = await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(_settings.CommandTopic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .Build(), ct.Token);
        if (result.Items.Any(i => (int)i.ResultCode >= 0x80)) throw new IOException("Command subscription rejected.");
        _subscribed = true;
        ConnectionChanged?.Invoke(true);
    }

    private Task DisconnectedAsync(MqttClientDisconnectedEventArgs _)
    {
        _subscribed = false;
        ConnectionChanged?.Invoke(false);
        _disconnected.TrySetResult();
        return Task.CompletedTask;
    }

    private async Task ReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        args.AutoAcknowledge = false;
        var handlers = MessageReceived;
        if (handlers is not null)
            foreach (Func<ChannelMessage, Task> handler in handlers.GetInvocationList())
                await handler(new(args.ApplicationMessage.Topic, args.ApplicationMessage.Payload.ToArray(), args.ApplicationMessage.Retain));
        // This confirms transport acceptance only. command_result remains the
        // authoritative hardware outcome; the coordinator tracks shared dispatch work.
        await args.AcknowledgeAsync(_cts.Token);
    }

    public async Task SendAsync(string topic, byte[] payload, bool retain, CancellationToken ct = default)
    {
        if (!IsConnected) throw new IOException($"{Name} is disconnected.");
        var result = await _client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
            .WithRetainFlag(retain).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), ct);
        if (result.ReasonCode != MqttClientPublishReasonCode.Success
            && !(retain && result.ReasonCode == MqttClientPublishReasonCode.NoMatchingSubscribers))
            throw new IOException($"{Name} publish rejected: {result.ReasonCode}");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _registration.Dispose();
        await _cts.CancelAsync();
        if (_loop is not null) await _loop;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            if (_client.IsConnected) await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), timeout.Token);
        }
        catch { }
        _client.Dispose();
        _cts.Dispose();
    }
}
