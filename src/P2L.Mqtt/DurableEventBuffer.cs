using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using P2L.Mqtt.Messages;
using P2L.Mqtt.Transport;

namespace P2L.Mqtt;

/// <summary>Durable JSONL outbox, compatible with existing offline buffers.</summary>
internal sealed class DurableEventBuffer
{
    private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.Ordinal);
    private readonly string _path;
    private readonly object _sync;

    public DurableEventBuffer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A durable MQTT event buffer path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        _sync = FileLocks.GetOrAdd(_path, _ => new object());
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public void Append(string topic, bool retain, byte[] payload)
    {
        if (!retain) payload = EventIdentity.Ensure(payload);
        var entry = new BufferedEntry(topic, retain, Convert.ToBase64String(payload));
        AppendLine(_path, JsonSerializer.Serialize(entry, P2LMqttJsonContext.Default.BufferedEntry));
    }

    public string[] ReadBatch(int maxEntries = 256)
    {
        lock (_sync)
            return File.Exists(_path) ? File.ReadLines(_path).Take(maxEntries).ToArray() : [];
    }

    public static BufferedEntry Parse(string line)
    {
        var entry = JsonSerializer.Deserialize(line, P2LMqttJsonContext.Default.BufferedEntry)
                    ?? throw new JsonException("Empty MQTT buffer entry.");
        if (string.IsNullOrWhiteSpace(entry.Topic) || entry.Payload is null)
            throw new JsonException("MQTT buffer entry lacks topic or payload.");
        using var payload = JsonDocument.Parse(Convert.FromBase64String(entry.Payload));
        if (payload.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("MQTT buffer payload must be an object.");
        if (!payload.RootElement.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(type.GetString()))
            throw new JsonException("MQTT buffer payload requires a type.");
        if (payload.RootElement.TryGetProperty("eventId", out var id)
            && (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())))
            throw new JsonException("MQTT buffer eventId must be a non-empty string.");
        return entry;
    }

    public void Quarantine(string line) => AppendLine(_path + ".invalid", line);

    // New events may be appended while a batch is publishing. Remove only the
    // acknowledged prefix from the current file, preserving those concurrent appends.
    public void AcknowledgePrefix(string[] batch, int count)
    {
        if (count == 0) return;
        lock (_sync)
        {
            IEnumerable<string> current = File.Exists(_path) ? File.ReadLines(_path) : [];
            if (!current.Take(count).SequenceEqual(batch.Take(count)))
                throw new IOException("MQTT outbox changed during replay; preserving its contents.");
            Replace(current.Skip(count));
        }
    }

    private void AppendLine(string path, string line)
    {
        lock (_sync)
        {
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            // A crash may leave a partial legacy JSONL line. Delimit it before a
            // new durable entry so recovery can quarantine it without losing both.
            if (stream.Length > 0)
            {
                stream.Position = stream.Length - 1;
                if (stream.ReadByte() != '\n')
                {
                    stream.Position = stream.Length;
                    stream.WriteByte((byte)'\n');
                }
            }
            stream.Position = stream.Length;
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.WriteLine(line);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
    }

    private void Replace(IEnumerable<string> lines)
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                foreach (var line in lines) writer.WriteLine(line);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

internal sealed record BufferedEntry(string Topic, bool Retain, string Payload);
