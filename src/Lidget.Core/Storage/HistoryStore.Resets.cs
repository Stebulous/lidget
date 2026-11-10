using Microsoft.Data.Sqlite;

namespace Lidget.Core.Storage;

public enum ResetEventKind
{
    /// <summary>Lidget saw the reset for the first time.</summary>
    Seen,

    /// <summary>The user redeemed the reset in Lidget.</summary>
    Redeemed,

    /// <summary>The reset went away before its expiry time, so another app used it.</summary>
    UsedElsewhere,

    Expired,
}

/// <summary>One change to a banked reset (plan decision D65).</summary>
public sealed record ResetEvent(
    string ProviderId,
    string ResetId,
    ResetEventKind Kind,
    string? Title,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset OccurredAt);

/// <summary>A banked reset that Lidget knows, with the count that it had at the last reading.</summary>
/// <param name="RedeemedAt">The last time that the user redeemed from this entry in Lidget, or null.</param>
public sealed record StoredReset(
    string ProviderId,
    string ResetId,
    string? Title,
    int Count,
    DateTimeOffset? GrantedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset? RedeemedAt);

public sealed partial class HistoryStore
{
    private const string ResetsMigration = """
        CREATE TABLE banked_resets (
            provider_id   TEXT    NOT NULL,
            reset_id      TEXT    NOT NULL,
            title         TEXT    NULL,
            count         INTEGER NOT NULL,
            granted_at    INTEGER NULL,
            expires_at    INTEGER NULL,
            first_seen_at INTEGER NOT NULL,
            redeemed_at   INTEGER NULL,
            PRIMARY KEY (provider_id, reset_id)
        ) WITHOUT ROWID;
        CREATE TABLE reset_events (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            provider_id TEXT    NOT NULL,
            reset_id    TEXT    NOT NULL,
            kind        TEXT    NOT NULL,
            title       TEXT    NULL,
            expires_at  INTEGER NULL,
            occurred_at INTEGER NOT NULL
        );
        CREATE INDEX ix_reset_events_occurred_at ON reset_events (occurred_at);
        """;

    /// <summary>Returns the banked resets that Lidget knows for <paramref name="providerId"/>.</summary>
    public IReadOnlyList<StoredReset> ReadResets(string providerId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT provider_id, reset_id, title, count, granted_at, expires_at, first_seen_at, redeemed_at
                FROM banked_resets
                WHERE provider_id = $provider
                ORDER BY reset_id;
                """;
            command.Parameters.AddWithValue("$provider", providerId);

            var resets = new List<StoredReset>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                resets.Add(new StoredReset(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3),
                    ReadTime(reader, 4),
                    ReadTime(reader, 5),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                    ReadTime(reader, 7)));
            }

            return resets;
        }
    }

    /// <summary>Saves changed resets, removes resets that are gone, and adds their events, in one transaction.</summary>
    public void ApplyResetChanges(IReadOnlyList<StoredReset> saved, IReadOnlyList<StoredReset> removed, IReadOnlyList<ResetEvent> events)
    {
        if (saved.Count == 0 && removed.Count == 0 && events.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var reset in saved)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR REPLACE INTO banked_resets
                        (provider_id, reset_id, title, count, granted_at, expires_at, first_seen_at, redeemed_at)
                    VALUES ($provider, $reset, $title, $count, $granted, $expires, $seen, $redeemed);
                    """;
                command.Parameters.AddWithValue("$provider", reset.ProviderId);
                command.Parameters.AddWithValue("$reset", reset.ResetId);
                command.Parameters.AddWithValue("$title", (object?)reset.Title ?? DBNull.Value);
                command.Parameters.AddWithValue("$count", reset.Count);
                command.Parameters.AddWithValue("$granted", TimeValue(reset.GrantedAt));
                command.Parameters.AddWithValue("$expires", TimeValue(reset.ExpiresAt));
                command.Parameters.AddWithValue("$seen", reset.FirstSeenAt.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$redeemed", TimeValue(reset.RedeemedAt));
                command.ExecuteNonQuery();
            }

            foreach (var reset in removed)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM banked_resets WHERE provider_id = $provider AND reset_id = $reset;";
                command.Parameters.AddWithValue("$provider", reset.ProviderId);
                command.Parameters.AddWithValue("$reset", reset.ResetId);
                command.ExecuteNonQuery();
            }

            foreach (var item in events)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO reset_events (provider_id, reset_id, kind, title, expires_at, occurred_at)
                    VALUES ($provider, $reset, $kind, $title, $expires, $occurred);
                    """;
                command.Parameters.AddWithValue("$provider", item.ProviderId);
                command.Parameters.AddWithValue("$reset", item.ResetId);
                command.Parameters.AddWithValue("$kind", item.Kind.ToString());
                command.Parameters.AddWithValue("$title", (object?)item.Title ?? DBNull.Value);
                command.Parameters.AddWithValue("$expires", TimeValue(item.ExpiresAt));
                command.Parameters.AddWithValue("$occurred", item.OccurredAt.ToUnixTimeMilliseconds());
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>Returns the newest reset events of <paramref name="providerId"/>, newest first.</summary>
    public IReadOnlyList<ResetEvent> QueryResetEvents(string providerId, int limit)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT provider_id, reset_id, kind, title, expires_at, occurred_at
                FROM reset_events
                WHERE provider_id = $provider
                ORDER BY occurred_at DESC, id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$provider", providerId);
            command.Parameters.AddWithValue("$limit", limit);

            var events = new List<ResetEvent>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                events.Add(new ResetEvent(
                    reader.GetString(0),
                    reader.GetString(1),
                    Enum.TryParse<ResetEventKind>(reader.GetString(2), out var kind) ? kind : ResetEventKind.Seen,
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    ReadTime(reader, 4),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))));
            }

            return events;
        }
    }

    private static DateTimeOffset? ReadTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(ordinal));

    private static object TimeValue(DateTimeOffset? time) => time is { } t ? t.ToUnixTimeMilliseconds() : DBNull.Value;
}
