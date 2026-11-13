using System.Text.Json;

namespace P2L.Mqtt.Configuration;

public sealed class ConfirmApiSettings
{
    public bool Enabled { get; init; }
    public string? Url { get; init; }
    public string? Token { get; init; }
    public int PollIntervalMs { get; init; } = 1000;
    public int BatchSize { get; init; } = 50;

    public static ConfirmApiSettings FromRemote(JsonElement root)
    {
        if (!root.TryGetProperty("confirmApi", out var value) || !value.GetProperty("enabled").GetBoolean()) return new();
        var settings = new ConfirmApiSettings
        {
            Enabled = true, Url = value.GetProperty("url").GetString(), Token = value.GetProperty("token").GetString(),
            PollIntervalMs = value.TryGetProperty("pollIntervalMs", out var poll) ? poll.GetInt32() : 1000,
            BatchSize = value.TryGetProperty("batchSize", out var batch) ? batch.GetInt32() : 50,
        };
        settings.Validate();
        return settings;
    }

    internal void Validate()
    {
        if (!Enabled) return;
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp)) ||
            uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Query.Length > 0 ||
            Token is null || Token.Length is < 32 or > 1000 || Token.Any(c => c is < '!' or > '~') ||
            PollIntervalMs is < 100 or > 60000 || BatchSize is < 1 or > 100)
            throw new InvalidDataException("Invalid Confirm API configuration");
    }
}
