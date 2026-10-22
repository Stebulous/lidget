using Microsoft.Data.Sqlite;

namespace Lidget.Core.Costs;

/// <summary>The scan position of one log file.</summary>
/// <param name="Offset">Byte offset after the last complete line that was read.</param>
/// <param name="Fingerprint">Hash of the first bytes, to detect a replaced file.</param>
/// <param name="ParserState">Serialized parser state at <paramref name="Offset"/>.</param>
public sealed record LogFileState(long Offset, long LastWriteTicks, string Fingerprint, string? ParserState);

/// <summary>One cached entry, as JSON, in file order.</summary>
public sealed record CachedEntry(string Path, long Sequence, long TimestampMs, string Payload);

/// <summary>
/// SQLite cache of parsed log entries (plan task T3.3). It keeps the read position of each file,
/// so that each scan reads only new lines. The cache is disposable: deleting it causes one full scan.
/// </summary>
public sealed class CostCache : IDisposable
{
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE log_files (
            source       TEXT    NOT NULL,
            path         TEXT    NOT NULL,
            offset       INTEGER NOT NULL,
            last_write   INTEGER NOT NULL,
            fingerprint  TEXT    NOT NULL,
            parser_state TEXT    NULL,
            PRIMARY KEY (source, path)
        ) WITHOUT ROWID;
        CREATE TABLE log_entries (
            source    TEXT    NOT NULL,
            path      TEXT    NOT NULL,
            sequence  INTEGER NOT NULL,
            timestamp INTEGER NOT NULL,
            payload   TEXT    NOT NULL,
            PRIMARY KEY (source, path, sequence)
        ) WITHOUT ROWID;
        CREATE INDEX ix_log_entries_time ON log_entries (source, timestamp);
        """,
    ];

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    public CostCache(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode = WAL;");
        Migrate();
    }

    public static CostCache CreateDefault() => new(Path.Combine(AppInfo.DataDirectory, "cost-cache.db"));

    public IReadOnlyDictionary<string, LogFileState> GetFiles(string source)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT path, offset, last_write, fingerprint, parser_state FROM log_files WHERE source = $source;";
            command.Parameters.AddWithValue("$source", source);
            var files = new Dictionary<string, LogFileState>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                files[reader.GetString(0)] = new LogFileState(
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4));
            }

            return files;
        }
    }

    /// <summary>Stores new entries of one file and its new state in one transaction.</summary>
    /// <param name="restart">True to delete the earlier entries of the file first.</param>
    public void Update(string source, string path, LogFileState state, IReadOnlyList<(long TimestampMs, string Payload)> entries, bool restart)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            long next = 0;
            if (restart)
            {
                Run(transaction, "DELETE FROM log_entries WHERE source = $source AND path = $path;", source, path);
            }
            else
            {
                using var max = Command(transaction, "SELECT COALESCE(MAX(sequence) + 1, 0) FROM log_entries WHERE source = $source AND path = $path;", source, path);
                next = Convert.ToInt64(max.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }

            using (var insert = Command(transaction, "INSERT INTO log_entries (source, path, sequence, timestamp, payload) VALUES ($source, $path, $sequence, $timestamp, $payload);", source, path))
            {
                var sequence = insert.Parameters.Add("$sequence", SqliteType.Integer);
                var timestamp = insert.Parameters.Add("$timestamp", SqliteType.Integer);
                var payload = insert.Parameters.Add("$payload", SqliteType.Text);
                foreach (var entry in entries)
                {
                    sequence.Value = next++;
                    timestamp.Value = entry.TimestampMs;
                    payload.Value = entry.Payload;
                    insert.ExecuteNonQuery();
                }
            }

            using (var upsert = Command(
                transaction,
                """
                INSERT INTO log_files (source, path, offset, last_write, fingerprint, parser_state)
                VALUES ($source, $path, $offset, $lastWrite, $fingerprint, $state)
                ON CONFLICT (source, path) DO UPDATE SET
                    offset = excluded.offset, last_write = excluded.last_write,
                    fingerprint = excluded.fingerprint, parser_state = excluded.parser_state;
                """,
                source,
                path))
            {
                upsert.Parameters.AddWithValue("$offset", state.Offset);
                upsert.Parameters.AddWithValue("$lastWrite", state.LastWriteTicks);
                upsert.Parameters.AddWithValue("$fingerprint", state.Fingerprint);
                upsert.Parameters.AddWithValue("$state", (object?)state.ParserState ?? DBNull.Value);
                upsert.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>Deletes the files that no longer exist, with their entries.</summary>
    public void RemoveFiles(string source, IEnumerable<string> paths)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var path in paths)
            {
                Run(transaction, "DELETE FROM log_entries WHERE source = $source AND path = $path;", source, path);
                Run(transaction, "DELETE FROM log_files WHERE source = $source AND path = $path;", source, path);
            }

            transaction.Commit();
        }
    }

    /// <summary>Returns the entries at or after <paramref name="sinceMs"/>, ordered by path and file order.</summary>
    public IReadOnlyList<CachedEntry> GetEntries(string source, long sinceMs)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT path, sequence, timestamp, payload FROM log_entries
                WHERE source = $source AND timestamp >= $since
                ORDER BY path, sequence;
                """;
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$since", sinceMs);
            var entries = new List<CachedEntry>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new CachedEntry(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3)));
            }

            return entries;
        }
    }

    /// <summary>Deletes entries older than <paramref name="beforeMs"/>. File positions stay, so old lines are not read again.</summary>
    public int Prune(long beforeMs)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM log_entries WHERE timestamp < $before;";
            command.Parameters.AddWithValue("$before", beforeMs);
            return command.ExecuteNonQuery();
        }
    }

    private SqliteCommand Command(SqliteTransaction transaction, string sql, string source, string path)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$path", path);
        return command;
    }

    private void Run(SqliteTransaction transaction, string sql, string source, string path)
    {
        using var command = Command(transaction, sql, source, path);
        command.ExecuteNonQuery();
    }

    private void Migrate()
    {
        using var read = _connection.CreateCommand();
        read.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(read.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        for (; version < Migrations.Length; version++)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"{Migrations[version]}\nPRAGMA user_version = {version + 1};";
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
