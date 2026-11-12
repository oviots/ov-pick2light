using System.Text.Json.Nodes;
using P2L.Shared.Enums;

namespace P2L.CLI.Web;

/// <summary>Preserves the Python UI's REST-to-MQTT command shape and defaults.</summary>
internal static class WebCommandMapper
{
    public static string Text(JsonObject body, string key, string fallback = "") => body[key]?.GetValue<string>() ?? fallback;
    private static string Mode(JsonObject body, string key) => Enum.TryParse<LightState>(Text(body, key).Replace("_", "", StringComparison.Ordinal), true, out var value) && Enum.IsDefined(value) ? value.ToString() : "On";
    private static string Color(JsonObject body) => Enum.TryParse<LightColor>(Text(body, "color"), true, out var value) && Enum.IsDefined(value) ? value.ToString() : "Green";
    private static int Quantity(JsonObject body)
    {
        var quantity = body["quantity"]?.GetValue<int>() ?? 1;
        if (quantity is < 0 or > 99999) throw new ArgumentException("Quantity must be 0–99999");
        return quantity;
    }
    private static string Block(JsonObject body)
    {
        var block = Text(body, "block_no", "00");
        if (block.Length != 2 || block.Any(c => c < '0' || c > '9')) throw new ArgumentException("Block must contain two digits");
        return block;
    }
    public static JsonObject Light(string address, JsonObject body)
    {
        var label = Text(body, "label");
        var command = new JsonObject {["type"] = "light_on", ["address"] = DeviceCatalog.Address(address), ["quantity"] = Quantity(body),
            ["color"] = Color(body), ["ledMode"] = Mode(body, "led_mode"), ["blockNo"] = Block(body), ["label"] = label.Length == 0 ? null : label};
        if (body["buzz"]?.GetValue<bool>() == false) command["buzMode"] = "Off";
        return command;
    }
    public static JsonObject Batch(JsonObject body)
    {
        if (body["items"] is not JsonArray source || source.Count == 0 || source.Count > 9999) throw new ArgumentException("Batch requires 1–9999 items");
        var items = new JsonArray();
        foreach (var node in source)
        {
            var row = node as JsonObject ?? throw new ArgumentException("Invalid batch item");
            if (row["quantity"] is null) throw new ArgumentException("Batch item quantity required");
            var label = Text(row, "label");
            var item = new JsonObject {["address"] = DeviceCatalog.Address(Text(row, "address")), ["quantity"] = Quantity(row),
                ["color"] = Color(row), ["ledMode"] = Mode(row, "led_mode"), ["label"] = label.Length == 0 ? null : label};
            if (!string.IsNullOrEmpty(Text(row, "buz_mode"))) item["buzMode"] = Mode(row, "buz_mode");
            else if (body["buzz"]?.GetValue<bool>() == false) item["buzMode"] = "Off";
            items.Add(item);
        }
        return new JsonObject {["type"] = "batch_start", ["blockNo"] = Block(body), ["items"] = items};
    }
}
