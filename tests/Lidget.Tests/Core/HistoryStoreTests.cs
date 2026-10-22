using Lidget.Core.Models;
using Lidget.Core.Storage;

namespace Lidget.Tests.Core;

public class HistoryStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static ProviderSnapshot Snapshot(DateTimeOffset observedAt, double session, double weekly, ProviderStatus status = ProviderStatus.Ok) =>
        new(ProviderIds.Claude, status,
            [
                new LimitWindow("claude.session", LimitKind.Session, "Session (5h)", session, observedAt.AddHours(3), "endpoint", observedAt),
                new LimitWindow("claude.weekly", LimitKind.Weekly, "Weekly", weekly, null, "endpoint", observedAt),
            ],
            null, observedAt);

    [Fact]
    public void CreatesSchemaAndReopensWithoutChanges()
    {
        using var dir = new TempDirectory();
        var path = dir.File("history.db");

        using (var store = new HistoryStore(path))
        {
            Assert.Equal(1, store.SchemaVersion);
            store.Record(Snapshot(Now, 10, 20));
        }

        using var reopened = new HistoryStore(path);
        Assert.Equal(1, reopened.SchemaVersion);
        Assert.Equal(2, reopened.QueryLast(TimeSpan.FromDays(1), Now).Count);
    }

    [Fact]
    public void RecordsOneRowForEachLimitAndIgnoresRepeats()
    {
        using var dir = new TempDirectory();
        using var store = new HistoryStore(dir.File("history.db"));

        Assert.Equal(2, store.Record(Snapshot(Now, 10, 20)));
        Assert.Equal(0, store.Record(Snapshot(Now, 10, 20)));
        Assert.Equal(2, store.Record(Snapshot(Now.AddMinutes(5), 12, 21)));

        var points = store.QueryLast(TimeSpan.FromDays(1), Now.AddMinutes(5));
        Assert.Equal(4, points.Count);

        var first = points[0];
        Assert.Equal(ProviderIds.Claude, first.ProviderId);
        Assert.Equal("claude.session", first.LimitId);
        Assert.Equal(LimitKind.Session, first.Kind);
        Assert.Equal("Session (5h)", first.Label);
        Assert.Equal(10, first.UsedPercent);
        Assert.Equal(Now.AddHours(3), first.ResetsAt);
        Assert.Equal(Now, first.ObservedAt);
        Assert.Null(points[1].ResetsAt);
    }

    [Theory]
    [InlineData(ProviderStatus.Stale)]
    [InlineData(ProviderStatus.Error)]
    [InlineData(ProviderStatus.RateLimited)]
    public void IgnoresSnapshotsWithoutFreshData(ProviderStatus status)
    {
        using var dir = new TempDirectory();
        using var store = new HistoryStore(dir.File("history.db"));

        Assert.Equal(0, store.Record(Snapshot(Now, 10, 20, status)));
        Assert.Empty(store.QueryLast(TimeSpan.FromDays(1), Now));
    }

    [Fact]
    public void RangeQueriesReturnTheLast24HoursOrSevenDays()
    {
        using var dir = new TempDirectory();
        using var store = new HistoryStore(dir.File("history.db"));
        foreach (var hoursAgo in new[] { 0, 23, 25, 24 * 6, 24 * 8 })
        {
            store.Record(Snapshot(Now.AddHours(-hoursAgo), hoursAgo, hoursAgo));
        }

        var day = store.QueryLast(TimeSpan.FromHours(24), Now);
        var week = store.QueryLast(TimeSpan.FromDays(7), Now);

        Assert.Equal([23.0, 0.0], day.Where(p => p.LimitId == "claude.session").Select(p => p.UsedPercent));
        Assert.Equal([144.0, 25.0, 23.0, 0.0], week.Where(p => p.LimitId == "claude.session").Select(p => p.UsedPercent));
        Assert.Empty(store.QueryLast(TimeSpan.FromDays(7), Now, ProviderIds.Codex));
    }

    [Fact]
    public void PurgeDeletesRowsOlderThanTheRetention()
    {
        using var dir = new TempDirectory();
        using var store = new HistoryStore(dir.File("history.db"));
        store.Record(Snapshot(Now.AddDays(-91), 1, 1));
        store.Record(Snapshot(Now.AddDays(-89), 2, 2));

        Assert.Equal(2, store.Purge(90, Now));

        var remaining = store.QueryLast(TimeSpan.FromDays(365), Now);
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, p => Assert.Equal(Now.AddDays(-89), p.ObservedAt));
    }
}
