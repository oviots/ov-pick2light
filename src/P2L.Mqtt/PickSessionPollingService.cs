using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using P2L.Core;
using P2L.Mqtt.Configuration;
using P2L.Mqtt.Messages;
using P2L.Shared.Enums;

namespace P2L.Mqtt;

internal sealed record PickSessionPollRequest(string[] BlockNos);
internal sealed record PickSessionPollResponse(JsonElement[] Commands, string[] InactiveBlocks);

/// <summary>Reads WMS-created work independently of the broker. One request at a time.</summary>
internal sealed class PickSessionPollingService : IAsyncDisposable
{
    private readonly PickSessionApiSettings _settings;
    private readonly Func<byte[], CancellationToken, Task> _dispatch;
    private readonly LightModuleRegistry _registry;
    private readonly ILogger _log;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public PickSessionPollingService(PickSessionApiSettings settings, LightModuleRegistry registry,
        Func<byte[], CancellationToken, Task> dispatch, ILogger log)
    {
        settings.Validate();
        _settings = settings; _registry = registry; _dispatch = dispatch; _log = log;
        _http = new HttpClient(new HttpClientHandler {AllowAutoRedirect = false}) {Timeout = TimeSpan.FromSeconds(10)};
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PollAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogWarning("[PickSession API] Poll failed: {Error}", ex.Message); }
            try { await Task.Delay(_settings.PollIntervalMs, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    internal async Task PollAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.Url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token);
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
            new PickSessionPollRequest(_settings.BlockNos),
            P2LMqttJsonContext.Default.PickSessionPollRequest));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsByteArrayAsync(ct);
        var poll = JsonSerializer.Deserialize(body, P2LMqttJsonContext.Default.PickSessionPollResponse)
            ?? throw new InvalidDataException("Invalid PickSession API response");
        if (poll.Commands is null || poll.InactiveBlocks is null) throw new InvalidDataException("Invalid PickSession API response");
        foreach (var block in poll.InactiveBlocks)
        {
            if (!_settings.BlockNos.Contains(block)) throw new InvalidDataException("Unexpected block in API response");
            foreach (var session in _registry.All.Where(m => m.BlockNo == block && m.Status == ModuleStatus.Operating)
                         .Select(m => m.CorrelationId).Distinct())
            {
                var finish = new BatchFinishCmd {Type = "batch_finish", BlockNo = block, CorrelationId = session};
                await _dispatch(JsonSerializer.SerializeToUtf8Bytes(finish, P2LMqttJsonContext.Default.BatchFinishCmd), ct);
            }
        }
        foreach (var command in poll.Commands)
        {
            var block = command.GetProperty("blockNo").GetString();
            if (command.GetProperty("type").GetString() != "batch_start" || block is null ||
                !_settings.BlockNos.Contains(block) || poll.InactiveBlocks.Contains(block))
                throw new InvalidDataException("Unexpected command in API response");
            await _dispatch(System.Text.Encoding.UTF8.GetBytes(command.GetRawText()), ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null) await _loop;
        _http.Dispose(); _cts.Dispose();
    }
}
