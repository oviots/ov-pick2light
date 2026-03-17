using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace P2L.Mqtt;

/// <summary>
/// Reads the Python web-client device database (p2l_devices.db) and caches
/// the address → label mapping in memory for fast lookup at publish time.
///
/// The database is read once on <see cref="Load"/> and can be refreshed at any
/// time by calling <see cref="Load"/> again (e.g. after the operator edits labels
/// in the web UI).
/// </summary>
public sealed class DeviceLabelStore
{
    private readonly string?  _dbPath;
    private readonly ILogger  _log;

    private FrozenDictionary<string, string> _labels    = FrozenDictionary<string, string>.Empty;
    private FrozenDictionary<string, string> _addresses = FrozenDictionary<string, string>.Empty;

    public DeviceLabelStore(string? dbPath, ILogger<DeviceLabelStore> log)
    {
        _dbPath = dbPath;
        _log    = log;
    }

    /// <summary>
    /// Returns the label for <paramref name="address"/>, or <c>""</c> if not found.
    /// </summary>
    public string GetLabel(string address)
        => _labels.TryGetValue(address, out var label) ? label : "";

    /// <summary>
    /// Returns the hardware address for <paramref name="label"/>, or <c>null</c> if not found.
    /// </summary>
    public string? GetAddress(string label)
        => _addresses.TryGetValue(label, out var addr) ? addr : null;

    /// <summary>
    /// Loads (or reloads) all rows from <c>devices</c> table into memory.
    /// Safe to call from any thread; replaces the cache atomically.
    /// No-op when no database path is configured.
    /// </summary>
    public void Load()
    {
        if (string.IsNullOrWhiteSpace(_dbPath)) return;

        var path = Path.GetFullPath(_dbPath);
        if (!File.Exists(path))
        {
            _log.LogDebug("[Labels] Database not found at {Path} — labels will be empty", path);
            return;
        }

        try
        {
            var labelBuilder   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var addressBuilder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT address, label FROM devices";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var addr  = reader.GetString(0);
                var label = reader.IsDBNull(1) ? "" : reader.GetString(1);
                labelBuilder[addr] = label;
                if (!string.IsNullOrWhiteSpace(label))
                    addressBuilder[label] = addr;
            }

            _labels    = labelBuilder.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            _addresses = addressBuilder.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            _log.LogInformation("[Labels] Loaded {Count} device label(s) from {Path}",
                _labels.Count, path);
        }
        catch (Exception ex)
        {
            _log.LogWarning("[Labels] Failed to load device labels from {Path}: {Err}", path, ex.Message);
        }
    }
}
