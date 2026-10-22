using Lidget.Core.Models;
using Microsoft.Data.Sqlite;

namespace Lidget.Core.Storage;

/// <summary>One stored reading of a limit.</summary>
public sealed record HistoryPoint(
    string ProviderId,
    string LimitId,
    LimitKind Kind,
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    DateTimeOffset ObservedAt);

/// <summary>
/// Keeps limit history in SQLite (plan decision D26). Each limit reading is stored once,
/// keyed by provider, limit, and observation time, so republished snapshots add no rows.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    // Each entry migrates the schema from version (index) to version (index + 1).
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE limit_samples (
            provider_id  TEXT    NOT NULL,
            limit_id     TEXT    NOT NULL,
            kind         TEXT    NOT NULL,
            label        TEXT    NOT NULL,
            used_percent REAL    NOT NULL,
            resets_at    INTEGER NULL,
            observed_at  INTEGER NOT NULL,
            PRIMARY KEY (provider_id, limit_id, observed_at)
        ) WITHOUT ROWID;
        CREATE INDEX ix_limit_samples_observed_at ON limit_samples (observed_at);
        """,
    ];

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    public HistoryStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode = WAL;");
        Migrate();
    }

    public static HistoryStore CreateDefault() => new(Path.Combine(AppInfo.DataDirectory, "history.db"));

    public int SchemaVersion
    {
        get
        {
            lock (_gate)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "PRAGMA user_version;";
                return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Stores the limits of a snapshot with fresh data. Returns the number of new rows.</summary>
    public int Record(ProviderSnapshot snapshot)
    {
        // Failed snapshots repeat the last good limits. Those readings are already stored.
        if (snapshot.Status != ProviderStatus.Ok || snapshot.Limits.Count == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO limit_samples
                    (provider_id, limit_id, kind, label, used_percent, resets_at, observed_at)
                VALUES ($provider, $limit, $kind, $label, $used, $resets, $observed);
                """;
            var provider = command.Parameters.Add("$provider", SqliteType.Text);
            var limit = command.Parameters.Add("$limit", SqliteType.Text);
            var kind = command.Parameters.Add("$kind", SqliteType.Text);
            var label = command.Parameters.Add("$label", SqliteType.Text);
            var used = command.Parameters.Add("$used", SqliteType.Real);
            var resets = command.Parameters.Add("$resets", SqliteType.Integer);
            var observed = command.Parameters.Add("$observed", SqliteType.Integer);

            var inserted = 0;
            foreach (var window in snapshot.Limits)
            {
                provider.Value = snapshot.ProviderId;
                limit.Value = window.Id;
                kind.Value = window.Kind.ToString();
                label.Value = window.Label;
                used.Value = window.UsedPercent;
                resets.Value = window.ResetsAt is { } r ? r.ToUnixTimeMilliseconds() : DBNull.Value;
                observed.Value = window.ObservedAt.ToUnixTimeMilliseconds();
                inserted += command.ExecuteNonQuery();
            }

            transaction.Commit();
            return inserted;
        }
    }

    /// <summary>Returns readings with <paramref name="from"/> &lt;= observed time &lt; <paramref name="to"/>, oldest first.</summary>
    public IReadOnlyList<HistoryPoint> Query(DateTimeOffset from, DateTimeOffset to, string? providerId = null)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT provider_id, limit_id, kind, label, used_percent, resets_at, observed_at
                FROM limit_samples
                WHERE observed_at >= $from AND observed_at < $to
                  AND ($provider IS NULL OR provider_id = $provider)
                ORDER BY observed_at, provider_id, limit_id;
                """;
            command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$provider", (object?)providerId ?? DBNull.Value);

            var points = new List<HistoryPoint>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                points.Add(new HistoryPoint(
                    reader.GetString(0),
                    reader.GetString(1),
                    Enum.TryParse<LimitKind>(reader.GetString(2), out var kind) ? kind : LimitKind.Other,
                    reader.GetString(3),
                    reader.GetDouble(4),
                    reader.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))));
            }

            return points;
        }
    }

    /// <summary>Returns the readings of the last <paramref name="range"/> before <paramref name="now"/>.</summary>
    public IReadOnlyList<HistoryPoint> QueryLast(TimeSpan range, DateTimeOffset now, string? providerId = null) =>
        Query(now - range, now + TimeSpan.FromMilliseconds(1), providerId);

    /// <summary>Deletes readings older than <paramref name="retentionDays"/>. Returns the number of deleted rows.</summary>
    public int Purge(int retentionDays, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM limit_samples WHERE observed_at < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", (now - TimeSpan.FromDays(retentionDays)).ToUnixTimeMilliseconds());
            return command.ExecuteNonQuery();
        }
    }

    private void Migrate()
    {
        var version = SchemaVersion;
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
