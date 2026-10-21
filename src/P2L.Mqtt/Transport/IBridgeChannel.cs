namespace P2L.Mqtt.Transport;

internal sealed record ChannelMessage(string Topic, byte[] Payload, bool Retain = false);

internal interface IBridgeChannel : IAsyncDisposable
{
    string Name { get; }
    bool IsConnected { get; }
    event Func<ChannelMessage, Task>? MessageReceived;
    event Action<bool>? ConnectionChanged;
    Task StartAsync(CancellationToken ct = default);
    Task SendAsync(string topic, byte[] payload, bool retain, CancellationToken ct = default);
}
