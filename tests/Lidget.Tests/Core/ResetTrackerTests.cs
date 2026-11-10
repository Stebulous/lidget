using Lidget.Core.Models;
using Lidget.Core.Resets;
using Lidget.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace Lidget.Tests.Core;

public class ResetTrackerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly HistoryStore _store;
    private readonly ResetTracker _tracker;

    public ResetTrackerTests()
    {
        _store = new HistoryStore(_dir.File("history.db"));
        _tracker = new ResetTracker(_store, _time);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private static BankedReset Reset(string id, int count = 1, DateTimeOffset? expiresAt = null, DateTimeOffset? grantedAt = null) =>
        new(id, "Full reset", count, grantedAt, expiresAt ?? Now.AddDays(20));

    private ProviderSnapshot Snapshot(params BankedReset[] resets) => Snapshot(new ResetSummary(resets.Sum(r => r.Count), resets, _time.GetUtcNow()));

    private ProviderSnapshot Snapshot(ResetSummary? summary) =>
        new(ProviderIds.Codex, ProviderStatus.Ok, [], null, _time.GetUtcNow()) { Resets = summary };

    private IReadOnlyList<ResetEvent> Events() => _store.QueryResetEvents(ProviderIds.Codex, 50);

    private void Advance(TimeSpan time) => _time.Advance(time);

    [Fact]
    public void FirstReadingRecordsEachResetAsSeen()
    {
        var granted = Now.AddDays(-10);
        var changes = 0;
        _tracker.Changed += (_, _) => changes++;

        _tracker.Apply(Snapshot(Reset("a", grantedAt: granted), Reset("b")));

        var events = Events();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(ResetEventKind.Seen, e.Kind));
        Assert.Equal(granted, events.Single(e => e.ResetId == "a").OccurredAt);
        Assert.Equal(Now, events.Single(e => e.ResetId == "b").OccurredAt);
        Assert.Equal("Full reset", events[0].Title);
        Assert.Equal(1, changes);
        Assert.Equal(2, _store.ReadResets(ProviderIds.Codex).Count);
    }

    [Fact]
    public void ARepeatedReadingWritesNoEvent()
    {
        _tracker.Apply(Snapshot(Reset("a")));
        var changes = 0;
        _tracker.Changed += (_, _) => changes++;

        _tracker.Apply(Snapshot(Reset("a")));
        Advance(TimeSpan.FromMinutes(5));
        _tracker.Apply(Snapshot(Reset("a")));

        Assert.Single(Events());
        Assert.Equal(0, changes);
    }

    [Fact]
    public void ARestartDoesNotRecordKnownResetsAgain()
    {
        _tracker.Apply(Snapshot(Reset("a")));
        Advance(TimeSpan.FromMinutes(5));

        new ResetTracker(_store, _time).Apply(Snapshot(Reset("a")));

        Assert.Single(Events());
    }

    [Fact]
    public void ACountWithoutDetailsAndARefreshWithoutResetDataWriteNoEvent()
    {
        _tracker.Apply(Snapshot(Reset("a"), Reset("b")));
        Advance(TimeSpan.FromMinutes(5));

        _tracker.Apply(Snapshot(new ResetSummary(1, null, _time.GetUtcNow())));
        _tracker.Apply(Snapshot((ResetSummary?)null));

        Assert.Equal(2, Events().Count);
        Assert.Equal(2, _store.ReadResets(ProviderIds.Codex).Count);
    }

    [Fact]
    public void AResetThatGoesAwayBeforeItsExpiryWasUsedInAnotherApp()
    {
        _tracker.Apply(Snapshot(Reset("a"), Reset("b")));
        Advance(TimeSpan.FromMinutes(5));

        _tracker.Apply(Snapshot(Reset("b")));

        var used = Assert.Single(Events(), e => e.Kind == ResetEventKind.UsedElsewhere);
        Assert.Equal("a", used.ResetId);
        Assert.Equal(_time.GetUtcNow(), used.OccurredAt);
        Assert.Equal("b", Assert.Single(_store.ReadResets(ProviderIds.Codex)).ResetId);
    }

    [Fact]
    public void AResetExpiresAtItsExpiryTimeWithoutANewReading()
    {
        var expires = Now.AddHours(3);
        _tracker.Apply(Snapshot(Reset("a", expiresAt: expires)));
        Advance(TimeSpan.FromHours(4));

        // The provider did not answer, so the snapshot has no reset data.
        _tracker.Apply(Snapshot((ResetSummary?)null));

        var expired = Assert.Single(Events(), e => e.Kind == ResetEventKind.Expired);
        Assert.Equal(expires, expired.OccurredAt);
        Assert.Empty(_store.ReadResets(ProviderIds.Codex));
    }

    [Fact]
    public void AnExpiredResetInAnOldReadingIsNotRecordedAgain()
    {
        var old = Snapshot(Reset("a", expiresAt: Now.AddHours(3)));
        _tracker.Apply(old);
        Advance(TimeSpan.FromHours(4));
        _tracker.Apply(Snapshot((ResetSummary?)null));

        // A new tracker, as after a restart, gets the reading from before the expiry time.
        new ResetTracker(_store, _time).Apply(old);

        Assert.Equal([ResetEventKind.Expired, ResetEventKind.Seen], Events().Select(e => e.Kind));
        Assert.Empty(_store.ReadResets(ProviderIds.Codex));
    }

    [Fact]
    public void ALowerCountRecordsOneUseForEachStep()
    {
        _tracker.Apply(Snapshot(Reset("grant", count: 3)));
        Advance(TimeSpan.FromMinutes(5));

        _tracker.Apply(Snapshot(Reset("grant", count: 1)));

        Assert.Equal(2, Events().Count(e => e.Kind == ResetEventKind.UsedElsewhere));
        Assert.Equal(1, Assert.Single(_store.ReadResets(ProviderIds.Codex)).Count);
    }

    [Fact]
    public void ARedemptionInLidgetIsNotCountedAsUsedInAnotherApp()
    {
        _tracker.Apply(Snapshot(Reset("a"), Reset("b")));
        Advance(TimeSpan.FromMinutes(5));
        var changes = 0;
        _tracker.Changed += (_, _) => changes++;

        _tracker.RecordRedeemed(ProviderIds.Codex, "a");
        Advance(TimeSpan.FromSeconds(10));
        _tracker.Apply(Snapshot(Reset("b")));

        var redeemed = Assert.Single(Events(), e => e.Kind == ResetEventKind.Redeemed);
        Assert.Equal("a", redeemed.ResetId);
        Assert.Equal("Full reset", redeemed.Title);
        Assert.DoesNotContain(Events(), e => e.Kind == ResetEventKind.UsedElsewhere);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void AnOldCountDirectlyAfterARedemptionIsIgnored()
    {
        _tracker.Apply(Snapshot(Reset("grant", count: 3)));
        Advance(TimeSpan.FromMinutes(5));
        _tracker.RecordRedeemed(ProviderIds.Codex, "grant");

        // The provider still reports the count from before the redemption.
        Advance(TimeSpan.FromSeconds(10));
        _tracker.Apply(Snapshot(Reset("grant", count: 3)));
        Advance(TimeSpan.FromMinutes(1));
        _tracker.Apply(Snapshot(Reset("grant", count: 2)));

        Assert.Equal([ResetEventKind.Redeemed, ResetEventKind.Seen], Events().Select(e => e.Kind));
        Assert.Equal(2, Assert.Single(_store.ReadResets(ProviderIds.Codex)).Count);
    }

    [Fact]
    public void AShortenedListDoesNotRemoveResets()
    {
        _tracker.Apply(Snapshot(Reset("a"), Reset("b"), Reset("c")));
        Advance(TimeSpan.FromMinutes(5));

        // Codex can give fewer entries than the available count.
        _tracker.Apply(Snapshot(new ResetSummary(3, [Reset("a"), Reset("b")], _time.GetUtcNow())));

        Assert.DoesNotContain(Events(), e => e.Kind == ResetEventKind.UsedElsewhere);
        Assert.Equal(3, _store.ReadResets(ProviderIds.Codex).Count);
    }

    [Fact]
    public void OtherProvidersKeepTheirResets()
    {
        _tracker.Apply(Snapshot(Reset("a")));
        var claude = new ProviderSnapshot(ProviderIds.Claude, ProviderStatus.Ok, [], null, Now)
        {
            Resets = new ResetSummary(0, [], Now),
        };

        _tracker.Apply(claude);

        Assert.Single(_store.ReadResets(ProviderIds.Codex));
        Assert.Empty(_store.QueryResetEvents(ProviderIds.Claude, 10));
    }

    [Fact]
    public void MigratesAnExistingHistoryFile()
    {
        var path = _dir.File("old.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
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
                INSERT INTO limit_samples VALUES ('claude', 'claude.session', 'Session', 'Session (5h)', 12, NULL, $observed);
                PRAGMA user_version = 1;
                """;
            command.Parameters.AddWithValue("$observed", Now.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }

        using var store = new HistoryStore(path);

        Assert.Equal(2, store.SchemaVersion);
        Assert.Equal(12, Assert.Single(store.QueryLast(TimeSpan.FromDays(1), Now)).UsedPercent);
        new ResetTracker(store, _time).Apply(Snapshot(Reset("a")));
        Assert.Single(store.QueryResetEvents(ProviderIds.Codex, 10));
    }

    [Fact]
    public void PurgeRemovesOldResetEvents()
    {
        _tracker.Apply(Snapshot(Reset("a")));
        Advance(TimeSpan.FromDays(100));
        _tracker.Apply(Snapshot(Reset("a"), Reset("b", expiresAt: _time.GetUtcNow().AddDays(5))));

        _store.Purge(retentionDays: 90, _time.GetUtcNow());

        // The first event of "a" is 100 days old. Its expiry event, 80 days old, stays.
        Assert.Equal([("b", ResetEventKind.Seen), ("a", ResetEventKind.Expired)], Events().Select(e => (e.ResetId, e.Kind)));
    }

    [Fact]
    public void EventsComeNewestFirstWithALimit()
    {
        _tracker.Apply(Snapshot(Reset("a")));
        Advance(TimeSpan.FromMinutes(5));
        _tracker.Apply(Snapshot(Reset("a"), Reset("b")));

        Assert.Equal("b", Assert.Single(_store.QueryResetEvents(ProviderIds.Codex, 1)).ResetId);
    }
}
