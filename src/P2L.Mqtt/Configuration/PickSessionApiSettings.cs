using System.Text.Json;

namespace P2L.Mqtt.Configuration;

public sealed class PickSessionApiSettings
{
    public bool Enabled { get; init; }
    public string? Url { get; init; }
    public string? Token { get; init; }
    public int PollIntervalMs { get; init; } = 2000;
    public string[] BlockNos { get; init; } = [];

    public static PickSessionApiSettings FromRemote(JsonElement root, string[] blocks)
    {
        if (!root.TryGetProperty("pickSessionApi", out var value) || !value.GetProperty("enabled").GetBoolean()) return new();
        var settings = new PickSessionApiSettings
        {
            Enabled = true, Url = value.GetProperty("url").GetString(), Token = value.GetProperty("token").GetString(),
            PollIntervalMs = value.TryGetProperty("pollIntervalMs", out var poll) ? poll.GetInt32() : 2000,
            BlockNos = blocks,
        };
        settings.Validate();
        return settings;
    }

    internal void Validate()
    {
        if (!Enabled) return;
        new ConfirmApiSettings {Enabled = true, Url = Url, Token = Token, PollIntervalMs = PollIntervalMs}.Validate();
        if (BlockNos.Length is < 1 or > 99 || BlockNos.Distinct().Count() != BlockNos.Length ||
            BlockNos.Any(b => b.Length != 2 || !b.All(char.IsAsciiDigit)))
            throw new InvalidDataException("Invalid PickSession API blocks");
    }
}
