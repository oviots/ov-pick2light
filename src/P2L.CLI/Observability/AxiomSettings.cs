using System.Text.Json;

namespace P2L.CLI.Observability;

internal sealed record AxiomSettings(Uri IngestUrl, string Token, string SiteCode, string ClientId, string BlockNo, string MqttPassword)
{
    public static AxiomSettings? FromRemote(JsonElement config)
    {
        if (!config.TryGetProperty("axiom", out var axiom) || !axiom.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True)
            return null;
        var endpoint = axiom.GetProperty("endpoint").GetString()!.TrimEnd('/');
        if (endpoint is not ("https://us-east-1.aws.edge.axiom.co" or "https://eu-central-1.aws.edge.axiom.co" or "https://api.axiom.co"))
            throw new InvalidDataException("Invalid Axiom endpoint");
        var dataset = axiom.GetProperty("dataset").GetString()!;
        var token = axiom.GetProperty("token").GetString()!;
        if (string.IsNullOrWhiteSpace(dataset) || string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace))
            throw new InvalidDataException("Invalid Axiom configuration");
        var path = endpoint.EndsWith("//api.axiom.co", StringComparison.Ordinal) ? $"/v1/datasets/{Uri.EscapeDataString(dataset)}/ingest" : $"/v1/ingest/{Uri.EscapeDataString(dataset)}";
        return new(new Uri(endpoint + path), token, config.GetProperty("siteCode").GetString()!,
            config.GetProperty("mqtt").GetProperty("clientId").GetString()!, config.GetProperty("gateway").GetProperty("blockNo").GetString()!,
            config.GetProperty("mqtt").GetProperty("password").GetString()!);
    }
}
