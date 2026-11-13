using System.Text.Json;
using Microsoft.Data.Sqlite;
using P2L.Mqtt.Messages;
using P2L.Core.Services;
using P2L.Shared;
using P2L.Shared.Models;

namespace P2L.Mqtt;

/// <summary>Physical Confirm receipts. MQTT PUBACK never removes a receipt.</summary>
public sealed class ConfirmationOutbox : IConfirmationJournal
{
    private readonly string _connectionString;
    private readonly object _lock = new();
    private readonly IP2LTelemetry? _telemetry;
    private readonly string? _topic;
    private readonly DeviceLabelStore? _labels;
    public event Action? Recorded;
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "P2L", "p2l_confirms.db");

    public ConfirmationOutbox(string path, string? scope = null, IP2LTelemetry? telemetry = null, string? topic = null, DeviceLabelStore? labels = null)
    {
        _telemetry = telemetry;
        _topic = topic;
        _labels = labels;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS ConfirmScope (Id INTEGER PRIMARY KEY CHECK (Id=1), Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS ConfirmOutbox (
                EventId TEXT PRIMARY KEY, BlockNo TEXT NOT NULL, CorrelationId TEXT,
                Address TEXT NOT NULL, Payload TEXT NOT NULL, State TEXT NOT NULL DEFAULT 'Pending', NextAttempt INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS IX_ConfirmOutbox_Delivery ON ConfirmOutbox(NextAttempt);
            CREATE INDEX IF NOT EXISTS IX_ConfirmOutbox_Detail ON ConfirmOutbox(json_extract(Payload, '$.pickingDetailId'));
            """;
        cmd.ExecuteNonQuery();
        if (scope is not null)
        {
            cmd.CommandText = "SELECT Value FROM ConfirmScope WHERE Id=1";
            var previous = cmd.ExecuteScalar() as string;
            if (previous is not null && previous != scope && HasPending())
                throw new InvalidOperationException("Pending Confirm receipts belong to another MQTT/controller configuration. Reconcile before changing endpoints.");
            cmd.CommandText = "INSERT OR REPLACE INTO ConfirmScope(Id,Value) VALUES(1,$scope)";
            cmd.Parameters.AddWithValue("$scope", scope);
            cmd.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return db;
    }

    public void Record(ConfirmationEvent confirmation, LightModuleState instruction)
    {
        var evt = new ConfirmedEvt
        {
            Type = "confirmed", ConfirmProtocol = 1, EventId = instruction.AssignmentId,
            Timestamp = confirmation.ReceivedAt, Address = instruction.Address,
            BlockNo = instruction.BlockNo, CorrelationId = instruction.CorrelationId,
            CommandItemId = instruction.CommandItemId, PickingDetailId = instruction.PickingDetailId,
            Status = confirmation.Status, AdjustedDisplay = confirmation.AdjustedDisplay,
            AdjustedQuantity = int.TryParse(confirmation.AdjustedDisplay?.Trim(), out var q) ? q : null,
            DisplayData = instruction.DisplayData,
            Label = _labels?.GetLabel(instruction.Address) is { Length: > 0 } label ? label : null,
        };
        var payload = JsonSerializer.Serialize(evt, P2LMqttJsonContext.Default.ConfirmedEvt);
        int inserted;
        lock (_lock)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO ConfirmOutbox(EventId,BlockNo,CorrelationId,Address,Payload) VALUES($id,$block,$session,$address,$payload)";
            cmd.Parameters.AddWithValue("$id", evt.EventId);
            cmd.Parameters.AddWithValue("$block", evt.BlockNo);
            cmd.Parameters.AddWithValue("$session", (object?)evt.CorrelationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$address", evt.Address);
            cmd.Parameters.AddWithValue("$payload", payload);
            inserted = cmd.ExecuteNonQuery(); // FULL synchronous commit before the controller ACK.
        }
        if (inserted == 0) return;
        // Wake delivery after commit. Diagnostics and MQTT never hold the database lock.
        try { Recorded?.Invoke(); } catch { /* The periodic retry still finds this receipt. */ }
        try { _telemetry?.Record("event", _topic, payload); } catch { /* Diagnostics are best effort. */ }
    }

    public bool HasRecorded(string pickingDetailId)
    {
        lock (_lock)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM ConfirmOutbox WHERE json_extract(Payload, '$.pickingDetailId')=$detail)";
            cmd.Parameters.AddWithValue("$detail", pickingDetailId);
            return (long)cmd.ExecuteScalar()! != 0;
        }
    }

    private bool HasPending()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM ConfirmOutbox)";
        return (long)cmd.ExecuteScalar()! != 0;
    }

    public IReadOnlyList<string> ReadPending()
    {
        lock (_lock)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT Payload FROM ConfirmOutbox ORDER BY rowid";
            using var rows = cmd.ExecuteReader();
            var result = new List<string>();
            while (rows.Read()) result.Add(rows.GetString(0));
            return result;
        }
    }

    // Claim a bounded batch with one FULL commit instead of one fsync per message.
    internal IReadOnlyList<(string EventId, string Payload)> ClaimForDelivery(int limit = 100)
    {
        lock (_lock)
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            using var cmd = db.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT EventId,Payload FROM ConfirmOutbox WHERE NextAttempt <= $now ORDER BY NextAttempt,rowid LIMIT $limit";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$limit", limit);
            var result = new List<(string EventId, string Payload)>();
            using (var rows = cmd.ExecuteReader())
                while (rows.Read()) result.Add((rows.GetString(0), rows.GetString(1)));
            if (result.Count == 0) return result;
            cmd.CommandText = "UPDATE ConfirmOutbox SET NextAttempt=$now + CASE WHEN State='ReconcileRequired' THEN 60 ELSE 5 END WHERE EventId=$id";
            var id = cmd.Parameters.Add("$id", SqliteType.Text);
            foreach (var receipt in result)
            {
                id.Value = receipt.EventId;
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
            return result;
        }
    }

    internal void AcknowledgeStored(IReadOnlyList<string> eventIds)
    {
        if (eventIds.Count == 0) return;
        lock (_lock)
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            using var cmd = db.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM ConfirmOutbox WHERE EventId=$id";
            var id = cmd.Parameters.Add("$id", SqliteType.Text);
            foreach (var eventId in eventIds) { id.Value = eventId; cmd.ExecuteNonQuery(); }
            transaction.Commit();
        }
    }

    public bool Acknowledge(string eventId, string blockNo, string? correlationId, string address, string outcome)
    {
        if (outcome is not ("stored" or "applied" or "reconcile_required")) return false;
        var accepted = outcome is "stored" or "applied";
        lock (_lock)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = (accepted ? "DELETE FROM ConfirmOutbox" : "UPDATE ConfirmOutbox SET State='ReconcileRequired', NextAttempt=$next") +
                " WHERE EventId=$id AND BlockNo=$block AND CorrelationId IS $session AND Address=$address" + (accepted ? "" : " AND State <> 'ReconcileRequired'");
            if (!accepted) cmd.Parameters.AddWithValue("$next", DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$id", eventId);
            cmd.Parameters.AddWithValue("$block", blockNo);
            cmd.Parameters.AddWithValue("$session", (object?)correlationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$address", address);
            return cmd.ExecuteNonQuery() > 0;
        }
    }
}
