using Microsoft.Data.Sqlite;
using P2L.Mqtt.Messages;

namespace P2L.Mqtt;

/// <summary>Records intent before hardware I/O; an interrupted intent is never replayed blindly.</summary>
public sealed class CommandReceiptStore
{
    private readonly string _connectionString;
    private readonly object _sync = new();

    public CommandReceiptStore(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Pooling = false }.ToString();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS command_receipts (
                receipt_key TEXT PRIMARY KEY, payload_hash TEXT NOT NULL,
                status TEXT NOT NULL, error_code TEXT, message TEXT,
                updated_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <returns>Existing outcome, or null after durably recording a new execution intent.</returns>
    public CommandResultEvt? TryBegin(string key, string payloadHash, string? commandId, string? correlationId)
    {
        lock (_sync)
        {
            using var conn = Open();
            using var transaction = conn.BeginTransaction();
            var existing = ReadExisting(conn, transaction, key, payloadHash, commandId, correlationId);
            if (existing is not null) return existing;
            using var insert = conn.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO command_receipts(receipt_key,payload_hash,status,updated_at) VALUES($key,$hash,'executing',$now)";
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$hash", payloadHash);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
            transaction.Commit();
            return null;
        }
    }

    public CommandResultEvt? GetExisting(string key, string payloadHash, string? commandId, string? correlationId)
    {
        lock (_sync)
        {
            using var conn = Open();
            return ReadExisting(conn, null, key, payloadHash, commandId, correlationId);
        }
    }

    public CommandResultEvt GetExistingOrReject(string key, string payloadHash, string? commandId,
        string? correlationId, CommandResultEvt rejection)
    {
        if (rejection.Status != "rejected") throw new ArgumentException("A pre-execution rejection is required.", nameof(rejection));
        lock (_sync)
        {
            using var conn = Open();
            using var transaction = conn.BeginTransaction();
            var existing = ReadExisting(conn, transaction, key, payloadHash, commandId, correlationId);
            if (existing is not null) return existing;
            using var insert = conn.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO command_receipts(receipt_key,payload_hash,status,error_code,message,updated_at) VALUES($key,$hash,'rejected',$error,$message,$now)";
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$hash", payloadHash);
            insert.Parameters.AddWithValue("$error", (object?)rejection.ErrorCode ?? DBNull.Value);
            insert.Parameters.AddWithValue("$message", (object?)rejection.Message ?? DBNull.Value);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
            transaction.Commit();
            return rejection;
        }
    }

    private static CommandResultEvt? ReadExisting(SqliteConnection conn, SqliteTransaction? transaction,
        string key, string payloadHash, string? commandId, string? correlationId)
    {
        using var read = conn.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT payload_hash,status,error_code,message FROM command_receipts WHERE receipt_key=$key";
        read.Parameters.AddWithValue("$key", key);
        using var reader = read.ExecuteReader();
        if (!reader.Read()) return null;
        var samePayload = reader.GetString(0) == payloadHash;
        var status = reader.GetString(1);
        return new CommandResultEvt
        {
            Type = "command_result", CommandId = commandId, CorrelationId = correlationId,
            Status = !samePayload ? "rejected" : status == "executing" ? "uncertain" : status,
            ErrorCode = !samePayload ? "COMMAND_ID_CONFLICT" : status == "executing" ? "RECOVERY_UNCERTAIN" : reader.IsDBNull(2) ? null : reader.GetString(2),
            Message = !samePayload ? "Command ID was already used for another payload."
                : status == "executing" ? "Previous execution did not record an outcome; reconcile before a new explicit intent."
                : reader.IsDBNull(3) ? null : reader.GetString(3),
        };
    }

    public void Complete(string key, CommandResultEvt result)
    {
        lock (_sync)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE command_receipts SET status=$status,error_code=$error,message=$message,updated_at=$now WHERE receipt_key=$key";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$status", result.Status);
            cmd.Parameters.AddWithValue("$error", (object?)result.ErrorCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$message", (object?)result.Message ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Command receipt was not recorded before execution.");
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        try
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
            return conn;
        }
        catch { conn.Dispose(); throw; }
    }
}
