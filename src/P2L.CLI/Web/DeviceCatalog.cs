using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using P2L.Core;
using P2L.Mqtt;
using P2L.Shared.Enums;
using P2L.Shared.Models;

namespace P2L.CLI.Web;

/// <summary>UI metadata uses the existing devices table; live hardware state stays in PickingEngine.</summary>
internal sealed class DeviceCatalog : IDisposable
{
    private readonly string _connectionString;
    private readonly DeviceLabelStore _labels;
    private readonly LightModuleRegistry _registry;
    private readonly ConcurrentDictionary<string, LightModuleState> _states = new();
    private readonly ConcurrentDictionary<string, (string Label, int Order)> _metadata = new();
    private readonly object _writeLock = new();

    public DeviceCatalog(string path, DeviceLabelStore labels, LightModuleRegistry registry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder {DataSource = path}.ToString();
        _labels = labels; _registry = registry;
        using var db = Open();
        using var create = db.CreateCommand();
        create.CommandText = "CREATE TABLE IF NOT EXISTS devices (address TEXT PRIMARY KEY, label TEXT NOT NULL DEFAULT '', order_no INTEGER NOT NULL DEFAULT 0)";
        create.ExecuteNonQuery();
        using var columns = db.CreateCommand(); columns.CommandText = "PRAGMA table_info(devices)";
        var hasOrder = false;
        using (var reader = columns.ExecuteReader())
            while (reader.Read()) if (reader.GetString(1) == "order_no") hasOrder = true;
        if (!hasOrder) { create.CommandText = "ALTER TABLE devices ADD COLUMN order_no INTEGER NOT NULL DEFAULT 0"; create.ExecuteNonQuery(); }
        using var query = db.CreateCommand(); query.CommandText = "SELECT address, label, order_no FROM devices";
        using (var rows = query.ExecuteReader())
            while (rows.Read())
            {
                var address = rows.GetString(0);
                _metadata[address] = (rows.GetString(1), rows.GetInt32(2));
                _states[address] = new LightModuleState {Address = address};
            }
        foreach (var state in registry.All) _states[state.Address] = state;
        registry.StateChanged += OnState;
        _labels.Load();
    }
    private SqliteConnection Open() { var connection = new SqliteConnection(_connectionString); connection.Open(); return connection; }
    private void OnState(LightModuleState state) => _states[state.Address] = state;
    public static string Address(string value)
    {
        if (!int.TryParse(value, out var address) || address is < 1 or > 9999) throw new ArgumentException("Address must be 1–9999");
        return address.ToString("0000");
    }
    public JsonObject? Get(string address) => _states.TryGetValue(address, out var state) ? View(state) : null;
    public JsonArray Snapshot() => new(_states.Values.OrderBy(m => m.Address).Select(m => (JsonNode)View(m)).ToArray());
    private JsonObject View(LightModuleState state)
    {
        var meta = _metadata.GetValueOrDefault(state.Address);
        return new JsonObject
        {
            ["address"] = state.Address, ["status"] = state.Status.ToString().ToUpperInvariant(), ["status_code"] = (int)state.Status,
            ["display_data"] = state.DisplayData, ["color"] = state.Color.ToString().ToUpperInvariant(), ["color_code"] = (int)state.Color,
            ["led_mode"] = state.LedMode.ToString().ToUpperInvariant(), ["last_instruction_at"] = state.LastInstructionAt?.ToString("O"),
            ["last_confirmed_at"] = state.LastConfirmedAt?.ToString("O"), ["adjusted_quantity"] = state.AdjustedQuantity,
            ["block_no"] = state.BlockNo, ["correlation_id"] = state.CorrelationId,
            ["label"] = meta.Label ?? "", ["order_no"] = meta.Order
        };
    }
    public JsonObject Upsert(string address, string? label, int? order)
    {
        if (label?.Length > 500) throw new ArgumentException("Label too long");
        lock (_writeLock)
        {
            var old = _metadata.GetValueOrDefault(address);
            var next = (Label: label ?? old.Label ?? "", Order: order ?? old.Order);
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO devices(address,label,order_no) VALUES($address,$label,$order) ON CONFLICT(address) DO UPDATE SET label=$label,order_no=$order";
            cmd.Parameters.AddWithValue("$address", address); cmd.Parameters.AddWithValue("$label", next.Label); cmd.Parameters.AddWithValue("$order", next.Order);
            cmd.ExecuteNonQuery();
            _metadata[address] = next;
            _states.TryAdd(address, new LightModuleState {Address = address});
            _labels.Load();
        }
        return Get(address)!;
    }
    public void Remove(string? address = null)
    {
        lock (_writeLock)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = address is null ? "DELETE FROM devices" : "DELETE FROM devices WHERE address=$address";
            if (address is not null) cmd.Parameters.AddWithValue("$address", address);
            cmd.ExecuteNonQuery();
            if (address is null) { _states.Clear(); _metadata.Clear(); }
            else { _states.TryRemove(address, out _); _metadata.TryRemove(address, out _); }
            _labels.Load();
        }
    }
    public void Dispose() => _registry.StateChanged -= OnState;
}
