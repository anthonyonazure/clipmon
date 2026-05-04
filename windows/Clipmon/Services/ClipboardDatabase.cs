using Clipmon.Models;
using Microsoft.Data.Sqlite;

namespace Clipmon.Services;

public sealed class ClipboardDatabase : IDisposable
{
    private const int CurrentEncVersion = 1; // AES-256-GCM via EncryptionService

    private readonly string _connectionString;
    private readonly EncryptionService? _crypto;

    public ClipboardDatabase(EncryptionService? crypto = null, string? overrideDirectory = null)
    {
        _crypto = crypto;

        var directory = overrideDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clipmon");

        Directory.CreateDirectory(directory);

        var dbPath = Path.Combine(directory, "clipmon.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        InitializeSchema();
        if (_crypto is not null) MigratePlaintextRows();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void InitializeSchema()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS entries (
                fingerprint TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                text_content TEXT,
                file_name TEXT,
                file_url TEXT,
                payload_data BLOB,
                uti_identifier TEXT,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                is_pinned INTEGER NOT NULL DEFAULT 0,
                source_application TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_entries_updated_at ON entries(updated_at DESC);
            CREATE INDEX IF NOT EXISTS idx_entries_pinned ON entries(is_pinned);
            """;
        cmd.ExecuteNonQuery();

        // Add enc_version column if not present (idempotent migration).
        using var migrateCmd = connection.CreateCommand();
        migrateCmd.CommandText = "PRAGMA table_info(entries);";
        var hasEncColumn = false;
        using (var reader = migrateCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "enc_version", StringComparison.OrdinalIgnoreCase))
                {
                    hasEncColumn = true;
                    break;
                }
            }
        }

        if (!hasEncColumn)
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE entries ADD COLUMN enc_version INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// One-time migration: re-encrypt any rows that were stored before encryption was enabled.
    /// Cheap because clipboard histories are small, and keeps "encrypted at rest" honest.
    /// </summary>
    private void MigratePlaintextRows()
    {
        if (_crypto is null) return;

        using var connection = Open();
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT fingerprint, text_content, payload_data FROM entries WHERE enc_version = 0";

        var pending = new List<(string fp, string? text, byte[]? payload)>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                pending.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2)));
            }
        }

        if (pending.Count == 0) return;

        using var tx = connection.BeginTransaction();
        foreach (var (fp, text, payload) in pending)
        {
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE entries
                   SET text_content = $text,
                       payload_data = $payload,
                       enc_version = $ver
                 WHERE fingerprint = $fp
                """;
            update.Parameters.AddWithValue("$text", (object?)(text is null ? null : _crypto.EncryptStringToBase64(text)) ?? DBNull.Value);
            update.Parameters.AddWithValue("$payload", (object?)(payload is null ? null : _crypto.EncryptBytes(payload)) ?? DBNull.Value);
            update.Parameters.AddWithValue("$ver", CurrentEncVersion);
            update.Parameters.AddWithValue("$fp", fp);
            update.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<ClipboardEntry> GetRecent(int limit)
    {
        if (limit <= 0) return Array.Empty<ClipboardEntry>();

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT fingerprint, kind, text_content, file_name, file_url,
                   payload_data, uti_identifier, created_at, updated_at,
                   is_pinned, source_application, enc_version
              FROM entries
             ORDER BY is_pinned DESC, updated_at DESC
             LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<ClipboardEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    public IReadOnlyList<ClipboardEntry> GetAll()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT fingerprint, kind, text_content, file_name, file_url,
                   payload_data, uti_identifier, created_at, updated_at,
                   is_pinned, source_application, enc_version
              FROM entries
             ORDER BY is_pinned DESC, updated_at DESC
            """;

        var results = new List<ClipboardEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    public ClipboardEntry? FindByFingerprint(string fingerprint)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT fingerprint, kind, text_content, file_name, file_url,
                   payload_data, uti_identifier, created_at, updated_at,
                   is_pinned, source_application, enc_version
              FROM entries
             WHERE fingerprint = $fp
            """;
        cmd.Parameters.AddWithValue("$fp", fingerprint);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapRow(reader) : null;
    }

    public void Upsert(ClipboardEntry entry)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entries
                (fingerprint, kind, text_content, file_name, file_url,
                 payload_data, uti_identifier, created_at, updated_at,
                 is_pinned, source_application, enc_version)
            VALUES
                ($fp, $kind, $text, $name, $url,
                 $payload, $uti, $created, $updated,
                 $pinned, $source, $enc)
            ON CONFLICT(fingerprint) DO UPDATE SET
                kind = excluded.kind,
                text_content = excluded.text_content,
                file_name = excluded.file_name,
                file_url = excluded.file_url,
                payload_data = excluded.payload_data,
                uti_identifier = excluded.uti_identifier,
                updated_at = excluded.updated_at,
                source_application = excluded.source_application,
                enc_version = excluded.enc_version
            """;

        var (storedText, storedPayload, encVersion) = EncryptForStorage(entry.TextContent, entry.PayloadData);

        cmd.Parameters.AddWithValue("$fp", entry.Fingerprint);
        cmd.Parameters.AddWithValue("$kind", entry.Kind.Serialize());
        cmd.Parameters.AddWithValue("$text", (object?)storedText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", (object?)entry.FileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$url", (object?)entry.FileUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$payload", (object?)storedPayload ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uti", (object?)entry.UtiIdentifier ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", new DateTimeOffset(entry.CreatedAt, TimeSpan.Zero).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$updated", new DateTimeOffset(entry.UpdatedAt, TimeSpan.Zero).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$pinned", entry.IsPinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$source", (object?)entry.SourceApplication ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enc", encVersion);

        cmd.ExecuteNonQuery();
    }

    public void UpdatePinned(string fingerprint, bool isPinned)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE entries
               SET is_pinned = $pinned,
                   updated_at = $updated
             WHERE fingerprint = $fp
            """;
        cmd.Parameters.AddWithValue("$pinned", isPinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        cmd.ExecuteNonQuery();
    }

    public void Delete(string fingerprint)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM entries WHERE fingerprint = $fp";
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        cmd.ExecuteNonQuery();
    }

    public void Clear(bool keepPinned)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = keepPinned
            ? "DELETE FROM entries WHERE is_pinned = 0"
            : "DELETE FROM entries";
        cmd.ExecuteNonQuery();
    }

    private (string? text, byte[]? payload, int version) EncryptForStorage(string? text, byte[]? payload)
    {
        if (_crypto is null)
        {
            return (text, payload, 0);
        }

        var storedText = text is null ? null : _crypto.EncryptStringToBase64(text);
        var storedPayload = payload is null ? null : _crypto.EncryptBytes(payload);
        return (storedText, storedPayload, CurrentEncVersion);
    }

    private ClipboardEntry MapRow(SqliteDataReader reader)
    {
        var encVersion = reader.GetInt32(11);
        var rawText = reader.IsDBNull(2) ? null : reader.GetString(2);
        var rawPayload = reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5);

        string? textContent = rawText;
        byte[]? payloadData = rawPayload;

        if (encVersion >= 1 && _crypto is not null)
        {
            try
            {
                textContent = rawText is null ? null : _crypto.DecryptStringFromBase64(rawText);
                payloadData = rawPayload is null ? null : _crypto.DecryptBytes(rawPayload);
            }
            catch
            {
                // Decryption failed — surface as empty so the row remains visible but inert.
                textContent = "[decryption failed]";
                payloadData = null;
            }
        }

        return new ClipboardEntry
        {
            Fingerprint = reader.GetString(0),
            Kind = ClipboardContentKindExtensions.Parse(reader.GetString(1)),
            TextContent = textContent,
            FileName = reader.IsDBNull(3) ? null : reader.GetString(3),
            FileUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
            PayloadData = payloadData,
            UtiIdentifier = reader.IsDBNull(6) ? null : reader.GetString(6),
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)).UtcDateTime,
            UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)).UtcDateTime,
            IsPinned = reader.GetInt32(9) != 0,
            SourceApplication = reader.IsDBNull(10) ? null : reader.GetString(10)
        };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}
