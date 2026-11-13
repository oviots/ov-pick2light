using System.Text.Json;

namespace P2L.Mqtt.Configuration;

/// <summary>Optional ACK endpoint. Loading this configuration does not send acknowledgements.</summary>
public sealed class AckApiSettings
{
    public bool Enabled { get; init; }
    public string? Url { get; init; }
    public string? Token { get; init; }

    public static AckApiSettings FromRemote(JsonElement root)
    {
        if (!root.TryGetProperty("ackApi", out var value) || !value.GetProperty("enabled").GetBoolean()) return new();
        var settings = new AckApiSettings {
            Enabled = true, Url = value.GetProperty("url").GetString(), Token = value.GetProperty("token").GetString(),
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
            Token is null || Token.Length is < 32 or > 1000 || Token.Any(c => c is < '!' or > '~'))
            throw new InvalidDataException("Invalid ACK API configuration");
    }
}
