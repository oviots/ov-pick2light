using System.Text.Json;

namespace P2L.CLI.Licensing;

internal static class RemoteBlocks
{
    public static string[]? Read(JsonElement gateway)
    {
        if (!gateway.TryGetProperty("blockNos", out var values)) return null; // legacy API
        if (values.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid controller blocks");
        var blocks = values.EnumerateArray().Select(v => v.GetString() ?? "").ToArray();
        if (blocks.Length is < 1 or > 99 || blocks.Any(b => b.Length != 2 || !b.All(char.IsAsciiDigit)) ||
            blocks.Distinct().Count() != blocks.Length || (blocks.Length > 1 && blocks.Contains("00")) ||
            !blocks.Contains(gateway.GetProperty("blockNo").GetString()))
            throw new InvalidDataException("Invalid controller blocks");
        return blocks;
    }
}
