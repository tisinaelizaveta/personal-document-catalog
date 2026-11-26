using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace FileManager;

internal enum HistoryFilter { All, Changes, Errors }

internal sealed record HistoryEntry(string Timestamp, string Action, string? Target, bool Success);

internal sealed class Database : IDisposable
{
    private const int Iterations = 600_000;
    private readonly SqliteConnection connection;

    public Database(string path)
    {
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true
        }.ToString());
        connection.Open();
        using var schema = Query("""
            CREATE TABLE IF NOT EXISTS users (
                id INTEGER PRIMARY KEY,
                username TEXT NOT NULL UNIQUE,
                salt BLOB NOT NULL,
                password_hash BLOB NOT NULL,
                iterations INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS files (
                owner_id INTEGER NOT NULL REFERENCES users(id),
                name TEXT NOT NULL,
                size INTEGER NOT NULL,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (owner_id, name)
            );
            CREATE TABLE IF NOT EXISTS operations (
                id INTEGER PRIMARY KEY,
                user_id INTEGER REFERENCES users(id),
                action TEXT NOT NULL,
                target TEXT,
                success INTEGER NOT NULL CHECK (success IN (0, 1)),
                occurred_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS operations_by_owner ON operations(user_id, id);
            """);
        schema.ExecuteNonQuery();
    }

    public bool Register(string username, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        using var insert = Query("""
            INSERT INTO users (username, salt, password_hash, iterations)
            VALUES ($name, $salt, $hash, $iterations)
            ON CONFLICT (username) DO NOTHING;
            """, ("$name", username), ("$salt", salt),
            ("$hash", Hash(password, salt, Iterations)), ("$iterations", Iterations));
        return insert.ExecuteNonQuery() == 1;
    }

    public long? Authenticate(string username, string password)
    {
        using var select = Query("""
            SELECT id, salt, password_hash, iterations FROM users WHERE username = $name;
            """, ("$name", username));
        using var row = select.ExecuteReader();
        if (!row.Read())
        {
            _ = Hash(password, new byte[16], Iterations);
            return null;
        }
        byte[] candidate = Hash(password, (byte[])row[1], row.GetInt32(3));
        return CryptographicOperations.FixedTimeEquals(candidate, (byte[])row[2]) ? row.GetInt64(0) : null;
    }

    public void Log(long? userId, string action, string? target, bool success) =>
        InsertOperation(userId, action, target, success, null);

    public List<HistoryEntry> ReadHistory(long userId, HistoryFilter filter)
    {
        using var select = Query("""
            SELECT occurred_at, action, target, success
            FROM operations
            WHERE user_id = $user AND (
                $filter = 0 OR
                ($filter = 1 AND success = 1 AND action IN ('create', 'modify', 'delete', 'zip', 'unzip')) OR
                ($filter = 2 AND success = 0)
            )
            ORDER BY id DESC
            LIMIT 20;
            """, ("$user", userId), ("$filter", (int)filter));
        var entries = new List<HistoryEntry>();
        using var rows = select.ExecuteReader();
        while (rows.Read())
            entries.Add(new HistoryEntry(rows.GetString(0), rows.GetString(1),
                rows.IsDBNull(2) ? null : rows.GetString(2), rows.GetBoolean(3)));
        return entries;
    }

    public void RecordWrite(long userId, string name, long size, bool existed)
    {
        using var transaction = connection.BeginTransaction();
        UpsertFile(transaction, userId, name, size);
        InsertOperation(userId, existed ? "modify" : "create", name, true, transaction);
        transaction.Commit();
    }

    public void RecordArchive(long userId, string action, string target, IEnumerable<(string Name, long Size)> results)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var (name, size) in results)
        {
            UpsertFile(transaction, userId, name, size);
            InsertOperation(userId, "create", name, true, transaction);
        }
        InsertOperation(userId, action, target, true, transaction);
        transaction.Commit();
    }

    private void UpsertFile(SqliteTransaction transaction, long userId, string name, long size)
    {
        using var upsert = Query("""
            INSERT INTO files (owner_id, name, size)
            VALUES ($owner, $name, $size)
            ON CONFLICT (owner_id, name) DO UPDATE
            SET size = excluded.size, updated_at = CURRENT_TIMESTAMP;
            """, ("$owner", userId), ("$name", name), ("$size", size));
        upsert.Transaction = transaction;
        upsert.ExecuteNonQuery();
    }

    public void RecordDelete(long userId, string name)
    {
        using var transaction = connection.BeginTransaction();
        using var remove = Query("""
            DELETE FROM files WHERE owner_id = $owner AND name = $name;
            """, ("$owner", userId), ("$name", name));
        remove.Transaction = transaction;
        remove.ExecuteNonQuery();
        InsertOperation(userId, "delete", name, true, transaction);
        transaction.Commit();
    }

    private void InsertOperation(long? userId, string action, string? target, bool success, SqliteTransaction? transaction)
    {
        using var insert = Query("""
            INSERT INTO operations (user_id, action, target, success)
            VALUES ($id, $action, $target, $success);
            """, ("$id", userId), ("$action", action), ("$target", target), ("$success", success ? 1 : 0));
        insert.Transaction = transaction;
        insert.ExecuteNonQuery();
    }

    private static byte[] Hash(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    private SqliteCommand Query(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public void Dispose() => connection.Dispose();
}
