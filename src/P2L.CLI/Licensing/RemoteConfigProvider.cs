using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using P2L.CLI.Observability;

namespace P2L.CLI.Licensing;

internal sealed class RemoteConfigProvider : IDisposable
{
    private readonly LicenseApiClient _api;
    private string? _startupConfig;
    public P2L.Mqtt.Configuration.ConfirmApiSettings ConfirmApi { get; private set; } = new();
    public AxiomSettings? Axiom { get; private set; }
    public string[]? BlockNos { get; private set; }
    public RemoteConfigProvider() => _api = new(Environment.GetEnvironmentVariable("P2L_LICENSE_API_URL"));

    public async Task<Dictionary<string, string?>> LoadAsync(CancellationToken ct = default)
    {
        var result = await FetchAsync(ct);
        var gateway = result.GetProperty("gateway");
        BlockNos = RemoteBlocks.Read(gateway);
        var mqtt = result.GetProperty("mqtt");
        static string Text(JsonElement node, string name)
        {
            var value = node.GetProperty(name).GetString();
            return !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException("Remote config missing " + name);
        }
        static string Port(JsonElement node) => node.GetProperty("port").GetInt32() is >= 1 and <= 65535
            ? node.GetProperty("port").GetInt32().ToString() : throw new InvalidDataException("Remote port invalid");
        var ca = RemoteTls.ReadCa(mqtt.GetProperty("tls"));
        var block = Text(gateway, "blockNo");
        if (block.Length != 2 || block.Any(c => c < '0' || c > '9')) throw new InvalidDataException("Remote block invalid");
        var values = new Dictionary<string, string?>
        {
            ["Gateway:Host"] = Text(gateway, "host"), ["Gateway:Port"] = Port(gateway), ["Gateway:BlockNo"] = block,
            ["Mqtt:Enabled"] = "true", ["Mqtt:Host"] = Text(mqtt, "host"), ["Mqtt:Port"] = Port(mqtt),
            ["Mqtt:ClientId"] = Text(mqtt, "clientId"), ["Mqtt:Username"] = Text(mqtt, "username"), ["Mqtt:Password"] = Text(mqtt, "password"),
            ["Mqtt:Tls:Enabled"] = "true", ["Mqtt:Tls:AllowUntrustedCerts"] = "false",
            ["Mqtt:Tls:CaCertPem"] = ca,
            // Legacy certificate paths/secrets cannot override the remote policy.
            ["Mqtt:Tls:CaCertPath"] = null, ["Mqtt:Tls:ClientCertPath"] = null,
            ["Mqtt:Tls:ClientKeyPath"] = null, ["Mqtt:Tls:CertPassword"] = null
        };
        foreach (var topic in new[] {"commandTopic", "eventTopic", "devicesTopic", "statusTopic"})
        {
            var value = Text(mqtt, topic);
            if (value.IndexOfAny(['+', '#', '\0']) >= 0) throw new InvalidDataException("Remote topic invalid");
            values["Mqtt:" + char.ToUpperInvariant(topic[0]) + topic[1..]] = value;
        }
        ConfirmApi = P2L.Mqtt.Configuration.ConfirmApiSettings.FromRemote(result);
        Axiom = AxiomSettings.FromRemote(result);
        _startupConfig = result.GetRawText();
        return values;
    }

    public async Task AuthorizeReconnectAsync(CancellationToken ct)
    {
        var current = await FetchAsync(ct);
        // Do not mutate gateway/topics while a picking session is running.
        if (current.GetRawText() != _startupConfig)
            throw new InvalidOperationException("Remote config changed; restart P2L at a safe time to apply it.");
    }

    private async Task<JsonElement> FetchAsync(CancellationToken ct)
    {
        using var key = DeviceCredentialStore.Load();
        var deviceId = Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        var challenge = await _api.PostAsync("challenge", new JsonObject {["deviceId"] = deviceId, ["role"] = "bridge"}, ct);
        var nonce = challenge.GetProperty("challenge").GetString();
        if (nonce is null || nonce.Length != 64 || nonce.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("Invalid challenge");
        var signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes($"p2l.config.v1\n{deviceId}\nbridge\n{nonce}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return await _api.PostAsync("config", new JsonObject {["deviceId"] = deviceId, ["role"] = "bridge", ["challenge"] = nonce, ["signature"] = signature}, ct);
    }
    public void Dispose() => _api.Dispose();
}
