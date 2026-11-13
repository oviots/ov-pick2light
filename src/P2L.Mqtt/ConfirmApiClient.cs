using System.Net.Http.Headers;
using System.Text.Json;
using P2L.Mqtt.Configuration;
using P2L.Mqtt.Messages;

namespace P2L.Mqtt;

internal sealed record ConfirmApiBatch(JsonElement[] Events);
internal sealed record ConfirmApiReceipt(string[]? StoredEventIds);

/// <summary>HTTP acceptance transfers durable ownership to P2L.Signals, not to an inventory transaction.</summary>
internal sealed class ConfirmApiClient : IDisposable
{
    private readonly ConfirmApiSettings _settings;
    private readonly HttpClient _http;
    public ConfirmApiClient(ConfirmApiSettings settings)
    {
        settings.Validate();
        _settings = settings;
        _http = new HttpClient(new HttpClientHandler {AllowAutoRedirect = false}) {Timeout = TimeSpan.FromSeconds(10)};
    }

    public async Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<(string EventId, string Payload)> receipts, CancellationToken ct)
    {
        var events = receipts.Select(receipt =>
        {
            using var doc = JsonDocument.Parse(receipt.Payload);
            return doc.RootElement.Clone();
        }).ToArray();
        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.Url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token);
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new ConfirmApiBatch(events), P2LMqttJsonContext.Default.ConfirmApiBatch));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        // A malformed response or unknown event ID must never delete a local receipt.
        var body = await response.Content.ReadAsByteArrayAsync(ct);
        var accepted = JsonSerializer.Deserialize(body, P2LMqttJsonContext.Default.ConfirmApiReceipt)?.StoredEventIds
            ?? throw new InvalidDataException("Confirm API response is missing storedEventIds");
        var sent = receipts.Select(r => r.EventId).ToHashSet(StringComparer.Ordinal);
        if (accepted.Any(id => id is null || !sent.Contains(id))) throw new InvalidDataException("Confirm API acknowledged an unknown event");
        return accepted.Distinct(StringComparer.Ordinal).ToArray();
    }
    public void Dispose() => _http.Dispose();
}
