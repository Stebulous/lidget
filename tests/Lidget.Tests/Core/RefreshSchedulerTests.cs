using Lidget.Core.Models;
using Lidget.Core.Providers;
using Lidget.Core.Refresh;
using Microsoft.Extensions.Time.Testing;

namespace Lidget.Tests.Core;

public class RefreshSchedulerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero));
    private LidgetSettings _settings = new();

    private RefreshScheduler CreateScheduler(params IUsageProvider[] providers) => new(providers, () => _settings, _time);

    [Fact]
    public async Task FirstRunRefreshesEveryEnabledProvider()
    {
        var claude = new FakeProvider(ProviderIds.Claude, _time);
        var codex = new FakeProvider(ProviderIds.Codex, _time);
        await using var scheduler = CreateScheduler(claude, codex);

        await scheduler.RunDueAsync();

        Assert.Equal(1, claude.Calls);
        Assert.Equal(1, codex.Calls);
        Assert.Equal(2, scheduler.Latest.Count);
    }

    [Fact]
    public async Task DisabledProviderIsNotRefreshed()
    {
        _settings = _settings with { CodexEnabled = false };
        var codex = new FakeProvider(ProviderIds.Codex, _time);
        await using var scheduler = CreateScheduler(codex);

        await scheduler.RunDueAsync();

        Assert.Equal(0, codex.Calls);
    }

    [Fact]
    public async Task RefreshesAgainOnlyAfterInterval()
    {
        var claude = new FakeProvider(ProviderIds.Claude, _time);
        await using var scheduler = CreateScheduler(claude);

        await scheduler.RunDueAsync();
        _time.Advance(TimeSpan.FromMinutes(4));
        await scheduler.RunDueAsync();
        Assert.Equal(1, claude.Calls);

        _time.Advance(TimeSpan.FromMinutes(1));
        await scheduler.RunDueAsync();
        Assert.Equal(2, claude.Calls);
    }

    [Fact]
    public async Task RefreshStaleUsesMaxAge()
    {
        var claude = new FakeProvider(ProviderIds.Claude, _time);
        await using var scheduler = CreateScheduler(claude);

        await scheduler.RunDueAsync();
        _time.Advance(TimeSpan.FromSeconds(30));
        await scheduler.RefreshStaleAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, claude.Calls);

        _time.Advance(TimeSpan.FromSeconds(31));
        await scheduler.RefreshStaleAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(2, claude.Calls);
    }

    [Fact]
    public async Task RateLimitBacksOffExponentially()
    {
        var claude = new FakeProvider(ProviderIds.Claude, _time) { Status = ProviderStatus.RateLimited };
        await using var scheduler = CreateScheduler(claude);

        await scheduler.RunDueAsync(); // first 429: wait 5 minutes
        _time.Advance(TimeSpan.FromMinutes(5));
        await scheduler.RunDueAsync(); // second 429: wait 10 minutes
        Assert.Equal(2, claude.Calls);

        _time.Advance(TimeSpan.FromMinutes(9));
        await scheduler.RunDueAsync();
        await scheduler.RefreshNowAsync();
        Assert.Equal(2, claude.Calls);

        _time.Advance(TimeSpan.FromMinutes(1));
        await scheduler.RunDueAsync();
        Assert.Equal(3, claude.Calls);
    }

    [Fact]
    public async Task RetryAfterLongerThanBackoffWins()
    {
        var claude = new FakeProvider(ProviderIds.Claude, _time) { Status = ProviderStatus.RateLimited, RetryAfter = TimeSpan.FromMinutes(30) };
        await using var scheduler = CreateScheduler(claude);

        await scheduler.RunDueAsync();
        _time.Advance(TimeSpan.FromMinutes(29));
        await scheduler.RunDueAsync();
        Assert.Equal(1, claude.Calls);

        _time.Advance(TimeSpan.FromMinutes(1));
        await scheduler.RunDueAsync();
        Assert.Equal(2, claude.Calls);
    }

    [Fact]
    public async Task SuccessResetsBackoff()
    {
        var claude = new FakeProvider(ProviderIds.Claude, _time) { Status = ProviderStatus.RateLimited };
        await using var scheduler = CreateScheduler(claude);

        await scheduler.RunDueAsync();
        claude.Status = ProviderStatus.Ok;
        _time.Advance(TimeSpan.FromMinutes(5));
        await scheduler.RunDueAsync();
        _time.Advance(TimeSpan.FromMinutes(5));
        await scheduler.RunDueAsync();

        Assert.Equal(3, claude.Calls);
    }

    [Fact]
    public async Task FailingProviderDoesNotBlockOthers()
    {
        var claude = new FakeProvider(ProviderIds.Claude, _time) { Throw = true };
        var codex = new FakeProvider(ProviderIds.Codex, _time);
        await using var scheduler = CreateScheduler(claude, codex);

        await scheduler.RunDueAsync();

        Assert.Equal(ProviderStatus.Error, scheduler.Latest[ProviderIds.Claude].Status);
        Assert.Equal(ProviderStatus.Ok, scheduler.Latest[ProviderIds.Codex].Status);
    }

    [Fact]
    public async Task FailureKeepsLastGoodLimits()
    {
        var claude = new FakeProvider(ProviderIds.Claude, _time);
        await using var scheduler = CreateScheduler(claude);
        await scheduler.RunDueAsync();
        var goodTime = scheduler.Latest[ProviderIds.Claude].UpdatedAt;

        claude.Status = ProviderStatus.Error;
        _time.Advance(TimeSpan.FromMinutes(5));
        await scheduler.RunDueAsync();

        var latest = scheduler.Latest[ProviderIds.Claude];
        Assert.Equal(ProviderStatus.Error, latest.Status);
        Assert.Single(latest.Limits);
        Assert.Equal(goodTime, latest.UpdatedAt);
    }

    [Fact]
    public async Task PushedSnapshotIsPublished()
    {
        var codex = new FakeProvider(ProviderIds.Codex, _time);
        await using var scheduler = CreateScheduler(codex);
        var published = new List<ProviderSnapshot>();
        scheduler.SnapshotPublished += (_, s) => published.Add(s);

        codex.Push(42);

        Assert.Single(published);
        Assert.Equal(42, scheduler.Latest[ProviderIds.Codex].Limits[0].UsedPercent);
    }

    private sealed class FakeProvider(string id, TimeProvider time) : IUsageProvider
    {
        public string Id => id;

        public string DisplayName => id;

        public int Calls { get; private set; }

        public ProviderStatus Status { get; set; } = ProviderStatus.Ok;

        public TimeSpan? RetryAfter { get; set; }

        public bool Throw { get; set; }

        public event EventHandler<ProviderSnapshot>? SnapshotPushed;

        public Task<ProviderSnapshot> RefreshAsync(CancellationToken ct)
        {
            Calls++;
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            var now = time.GetUtcNow();
            var snapshot = Status == ProviderStatus.Ok
                ? Snapshot(10)
                : ProviderSnapshot.Failed(id, Status, "failed", now) with { RetryAfter = RetryAfter };
            return Task.FromResult(snapshot);
        }

        public void Push(double used) => SnapshotPushed?.Invoke(this, Snapshot(used));

        private ProviderSnapshot Snapshot(double used)
        {
            var now = time.GetUtcNow();
            return new ProviderSnapshot(id, ProviderStatus.Ok, [new LimitWindow($"{id}.session", LimitKind.Session, "Session", used, null, "test", now)], null, null, now);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
