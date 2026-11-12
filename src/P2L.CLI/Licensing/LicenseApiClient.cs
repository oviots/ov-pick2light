using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace P2L.CLI.Licensing;

internal sealed class LicenseApiClient : IDisposable
{
    private readonly HttpClient _http;
    public LicenseApiClient(string? address, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(address?.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Configure an HTTPS P2L_LICENSE_API_URL before starting P2L.");
        _http = new HttpClient(handler ?? new HttpClientHandler {AllowAutoRedirect = false}) {BaseAddress = uri, Timeout = TimeSpan.FromSeconds(15)};
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
    public async Task<JsonElement> PostAsync(string path, JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/p2l/v1/" + path)
            {Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")};
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"License API rejected request ({(int)response.StatusCode}); check license, device and service availability.");
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > 65536) throw new InvalidDataException("License API response too large");
            buffer.Write(chunk, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        if (!document.RootElement.GetProperty("Status").GetBoolean()) throw new InvalidDataException("License API response invalid");
        return document.RootElement.GetProperty("Data").Clone();
    }
    public void Dispose() => _http.Dispose();
}
