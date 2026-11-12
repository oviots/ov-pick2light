using System.Text.Json;

namespace P2L.CLI.Licensing;

internal static class RemoteTls
{
    // Legacy API returns true; custom CA uses the same object shape as Blocks.
    public static string? ReadCa(JsonElement tls)
    {
        if (tls.ValueKind == JsonValueKind.True) return null;
        if (tls.ValueKind != JsonValueKind.Object ||
            (tls.TryGetProperty("rejectUnauthorized", out var verify) && verify.ValueKind != JsonValueKind.True) ||
            (tls.TryGetProperty("enabled", out var enabled) && enabled.ValueKind != JsonValueKind.True))
            throw new InvalidDataException("MQTT TLS verification is required");
        if (!tls.TryGetProperty("ca", out var ca) || ca.ValueKind == JsonValueKind.Null) return null;
        if (ca.ValueKind != JsonValueKind.String || ca.GetString()!.Length > 32768)
            throw new InvalidDataException("Invalid MQTT CA certificate");
        return string.IsNullOrWhiteSpace(ca.GetString()) ? null : ca.GetString();
    }
}
