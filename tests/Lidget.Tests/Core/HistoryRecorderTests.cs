using Lidget.Core.Models;
using Lidget.Core.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Lidget.Tests.Core;

public class HistoryRecorderTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));

    private ProviderSnapshot Snapshot(DateTimeOffset observedAt, double used = 40) =>
        new(ProviderIds.Codex, ProviderStatus.Ok,
            [new LimitWindow("codex.session", LimitKind.Session, "Session (5h)", used, null, "app-server", observedAt)],
            null, observedAt);

    [Fact]
    public void RecordsSnapshotsAndRaisesRecordedOnlyForNewRows()
    {
        using var dir = new TempDirectory();
        using var store = new HistoryStore(dir.File("history.db"));
        var recorder = new HistoryRecorder(store, () => new LidgetSettings(), _time);
        var raised = 0;
        recorder.Recorded += (_, _) => raised++;

        recorder.Record(Snapshot(_time.GetUtcNow()));
        recorder.Record(Snapshot(_time.GetUtcNow()));

        Assert.Equal(1, raised);
        Assert.Single(store.QueryLast(TimeSpan.FromDays(1), _time.GetUtcNow()));
    }

    [Fact]
    public void PurgesAtFirstRecordAndThenOnceADay()
    {
        using var dir = new TempDirectory();
        using var store = new HistoryStore(dir.File("history.db"));
        var settings = new LidgetSettings { HistoryRetentionDays = 1 };
        var recorder = new HistoryRecorder(store, () => settings, _time);
        store.Record(Snapshot(_time.GetUtcNow().AddDays(-2)));

        recorder.Record(Snapshot(_time.GetUtcNow()));
        Assert.Single(store.QueryLast(TimeSpan.FromDays(30), _time.GetUtcNow()));

        // Older than the retention, but the next purge is not due yet.
        store.Record(Snapshot(_time.GetUtcNow().AddDays(-3)));
        _time.Advance(TimeSpan.FromHours(23));
        recorder.Record(Snapshot(_time.GetUtcNow()));
        Assert.Equal(3, store.QueryLast(TimeSpan.FromDays(30), _time.GetUtcNow()).Count);

        // The purge is due after 24 hours. It keeps only the readings of the last day.
        _time.Advance(TimeSpan.FromHours(2));
        recorder.Record(Snapshot(_time.GetUtcNow()));
        var remaining = store.QueryLast(TimeSpan.FromDays(30), _time.GetUtcNow());
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, p => Assert.True(p.ObservedAt >= _time.GetUtcNow().AddDays(-1)));
    }

    [Fact]
    public void SkipsRepeatedValuesForTenMinutes()
    {
        using var dir = new TempDirectory();
        using var store = new HistoryStore(dir.File("history.db"));
        var recorder = new HistoryRecorder(store, () => new LidgetSettings(), _time);
        var start = _time.GetUtcNow();

        recorder.Record(Snapshot(start));
        recorder.Record(Snapshot(start.AddMinutes(2)));
        recorder.Record(Snapshot(start.AddMinutes(4), used: 41));
        recorder.Record(Snapshot(start.AddMinutes(9), used: 41));
        recorder.Record(Snapshot(start.AddMinutes(14), used: 41));

        var stored = store.QueryLast(TimeSpan.FromDays(1), start.AddHours(1));
        Assert.Equal([start, start.AddMinutes(4), start.AddMinutes(14)], stored.Select(p => p.ObservedAt));
    }
}
