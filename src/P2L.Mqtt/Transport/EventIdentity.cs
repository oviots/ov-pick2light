using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace P2L.Mqtt.Transport;

internal static class EventIdentity
{
    public static byte[] Ensure(byte[] payload, string? fallbackId = null)
    {
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.TryGetProperty("eventId", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
            return payload;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name != "eventId") property.WriteTo(writer);
            writer.WriteString("eventId", fallbackId ?? Guid.NewGuid().ToString("N"));
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static byte[] FromBuffered(BufferedEntry entry)
        => Ensure(Convert.FromBase64String(entry.Payload),
            "legacy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Topic + "\n" + entry.Payload))));
}
