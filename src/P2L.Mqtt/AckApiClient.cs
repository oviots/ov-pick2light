using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using P2L.Mqtt.Configuration;

namespace P2L.Mqtt;

/// <summary>Explicit-use HTTP client, reserved for future ACK integration. No background sends or retries.</summary>
public sealed class AckApiClient : IDisposable
{
    private readonly AckApiSettings _settings;
    private readonly HttpClient _http;

    public AckApiClient(AckApiSettings settings, HttpMessageHandler? handler = null)
    {
        settings.Validate();
        _settings = settings;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    public async Task SendAsync(JsonElement acknowledgement, CancellationToken ct = default)
    {
        if (!_settings.Enabled) throw new InvalidOperationException("ACK API is disabled");
        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.Url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token);
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(acknowledgement.GetRawText()));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}
