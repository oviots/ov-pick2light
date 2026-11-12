using System.Text.Json.Nodes;

namespace P2L.CLI.Observability;

/// <summary>Filters only the Axiom copy. Picking, MQTT and local logs keep their original data.</summary>
internal sealed class AxiomLogPolicy(bool debug, TimeProvider clock)
{
    private sealed class RepeatedError(JsonObject first, DateTimeOffset now)
    {
        public JsonObject Last = first;
        public DateTimeOffset Started = now;
        public DateTimeOffset LastSeen = now;
        public long Suppressed;
    }
    private readonly object _lock = new();
    private readonly Dictionary<string, RepeatedError> _errors = new(StringComparer.Ordinal);
    private string? _bridgeStatus;

    public JsonNode? Filter(string kind, JsonNode? node, string? template)
    {
        if (debug) return node;
        if (node is not JsonObject data) return null;
        var type = data["type"]?.GetValue<string>();
        if (kind == "command_received")
        {
            if (type is not ("batch_start" or "light_on" or "light_off" or "batch_finish" or "terminate_all" or "csv_run" or "csv_control" or "scan_devices")) return null;
            var summary = new JsonObject();
            foreach (var key in new[] {"type", "correlationId", "blockNo", "address", "quantity", "color", "ledMode", "buzMode", "action", "from", "to"})
                if (data.TryGetPropertyValue(key, out var value)) summary[key] = value?.DeepClone();
            if (type == "batch_start" && data["items"] is JsonArray items)
            {
                summary["itemCount"] = items.Count;
                summary["totalQuantity"] = items.Sum(item => item?["quantity"] is JsonValue v && v.TryGetValue<long>(out var q) ? q : 0);
            }
            return summary;
        }
        if (kind == "event")
        {
            if (type == "bridge_status")
            {
                var state = data["online"]?.ToJsonString() + ":" + data["gatewayConnected"]?.ToJsonString();
                lock (_lock)
                {
                    if (_bridgeStatus == state) return null;
                    _bridgeStatus = state;
                }
                return data;
            }
            return type is "confirmed" or "batch_completed" or "task_error" or "csv_completed" ? data : null;
        }
        if (kind != "service_log") return null;
        var category = data["category"]?.GetValue<string>() ?? "";
        var level = data["level"]?.GetValue<string>();
        if (level == "Information") return ImportantInformation(category, template ?? "") ? data : null;
        if (level is not ("Warning" or "Error" or "Critical")) return null;
        // Business errors (NACK, dispatch failure, invalid commands) are never aggregated.
        if (level == "Critical" || template is null || !ConnectionFailure(template)) return data;
        var detail = data["properties"]?["Err"]?.GetValue<string>() ?? data["properties"]?["Msg"]?.GetValue<string>() ?? "";
        var errorKey = category + "\n" + level + "\n" + template + "\n" + detail;
        lock (_lock)
        {
            if (_errors.TryGetValue(errorKey, out var repeated))
            {
                repeated.Suppressed++;
                repeated.LastSeen = clock.GetUtcNow();
                repeated.Last = data;
                return null;
            }
            // If new error variants exhaust the small map, send normally instead of losing errors.
            if (_errors.Count < 64) _errors[errorKey] = new(data, clock.GetUtcNow());
        }
        return data;
    }

    public List<JsonObject> FlushRepeated(bool force = false)
    {
        var result = new List<JsonObject>();
        lock (_lock)
        {
            foreach (var (key, repeated) in _errors.ToArray())
            {
                if (!force && clock.GetUtcNow() - repeated.Started < TimeSpan.FromSeconds(60)) continue;
                _errors.Remove(key);
                if (repeated.Suppressed == 0) continue;
                var summary = (JsonObject)repeated.Last.DeepClone();
                summary["summary"] = true;
                summary["repeatCount"] = repeated.Suppressed; // additional occurrences, excluding the first record
                summary["windowStartedAt"] = repeated.Started.ToString("O");
                summary["lastSeenAt"] = repeated.LastSeen.ToString("O");
                summary.Remove("exception"); // full exception was already sent with the first occurrence
                summary.Remove("properties");
                result.Add(summary);
            }
        }
        return result;
    }

    private static bool ConnectionFailure(string template) =>
        template.StartsWith("[MQTT] Connection failed:", StringComparison.Ordinal) ||
        template.StartsWith("Cannot connect to ", StringComparison.Ordinal) ||
        template.StartsWith("Receive error; will reconnect:", StringComparison.Ordinal) ||
        template.StartsWith("Heartbeat failed:", StringComparison.Ordinal);

    private static bool ImportantInformation(string category, string template) =>
        (category.EndsWith(".ServeCommand", StringComparison.Ordinal) && template is "P2L service starting." or "P2L service stopping.") ||
        (category.EndsWith(".MqttBridgeService", StringComparison.Ordinal) &&
            (template.StartsWith("[MQTT] Bridge starting", StringComparison.Ordinal) || template.StartsWith("[MQTT] Connected to ", StringComparison.Ordinal) || template.StartsWith("[MQTT] Network restored", StringComparison.Ordinal))) ||
        (category.EndsWith(".AioiGateway", StringComparison.Ordinal) && template.StartsWith("Connected to gateway ", StringComparison.Ordinal)) ||
        category.EndsWith(".WatchdogService", StringComparison.Ordinal) ||
        (category.EndsWith(".DeviceScanService", StringComparison.Ordinal) &&
            (template.StartsWith("Device scan started:", StringComparison.Ordinal) || template.StartsWith("Device scan complete:", StringComparison.Ordinal))) ||
        (category.EndsWith(".MqttEventPublisher", StringComparison.Ordinal) &&
            (template.StartsWith("[MQTT] Replaying ", StringComparison.Ordinal) || template.StartsWith("[MQTT] Buffer replay ", StringComparison.Ordinal) || template.StartsWith("[MQTT] Buffer partially replayed:", StringComparison.Ordinal)));
}
