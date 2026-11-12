using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace P2L.CLI.Observability;

internal sealed class TelemetryRedactor(params string[] secrets)
{
    private static readonly Regex Sensitive = new("password|passwd|token|secret|authorization|license|private.?key|client.?key|certpassword", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PrivateKey = new("-----BEGIN [^-]*PRIVATE KEY-----[\\s\\S]*?-----END [^-]*PRIVATE KEY-----", RegexOptions.CultureInvariant);
    private static readonly Regex License = new("P2L-[A-F0-9]{48}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Credentials = new("(?i)(password|passwd|token|secret|authorization|licenseCode)\\s*[:=]\\s*(?:Bearer\\s+)?[^\\s,;]+", RegexOptions.CultureInvariant);

    public bool IsSensitive(string key) => Sensitive.IsMatch(key);
    public string Text(string value)
    {
        foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => s.Length))
            value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        value = PrivateKey.Replace(value, "[REDACTED PRIVATE KEY]");
        value = License.Replace(value, "[REDACTED LICENSE]");
        value = Credentials.Replace(value, "$1=[REDACTED]");
        return value.Length > 8192 ? value[..8192] + " [TRUNCATED]" : value;
    }

    public JsonNode? Clean(JsonNode? node, int depth = 0)
    {
        if (depth > 16) return JsonValue.Create("[MAX DEPTH]");
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var field in obj) result[field.Key] = IsSensitive(field.Key) ? JsonValue.Create("[REDACTED]") : Clean(field.Value, depth + 1);
            return result;
        }
        if (node is JsonArray array) return new JsonArray(array.Select(value => Clean(value, depth + 1)).ToArray());
        if (node is JsonValue val && val.TryGetValue<string>(out var text)) return JsonValue.Create(Text(text));
        return node?.DeepClone();
    }
}
