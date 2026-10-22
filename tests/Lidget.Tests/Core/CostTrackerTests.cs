using Lidget.Core.Costs;
using Lidget.Core.Models;
using Microsoft.Extensions.Time.Testing;

namespace Lidget.Tests.Core;

public class CostTrackerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));

    private sealed class FakeSource(string providerId, CostSummary? summary) : ICostSource
    {
        public int Scans { get; private set; }

        public string ProviderId => providerId;

        public CostSummary? Scan(DateTimeOffset now, CancellationToken ct)
        {
            Scans++;
            return summary;
        }
    }

    [Fact]
    public async Task ScansOnlyEnabledProvidersAndPublishesTheirEstimates()
    {
        var claude = new FakeSource(ProviderIds.Claude, new CostSummary(1, 2, 3, "USD"));
        var codex = new FakeSource(ProviderIds.Codex, new CostSummary(4, 5, 6, "USD"));
        await using var tracker = new CostTracker([claude, codex], () => new LidgetSettings { CodexEnabled = false }, _time);
        var updates = new List<CostUpdate>();
        tracker.CostsUpdated += (_, update) => updates.Add(update);

        await tracker.ScanAsync();

        Assert.Equal((1, 0), (claude.Scans, codex.Scans));
        Assert.Equal(ProviderIds.Claude, Assert.Single(updates).ProviderId);
        Assert.Equal(3, tracker.Latest[ProviderIds.Claude].Last30Days);
    }

    [Fact]
    public async Task ScanIfStaleSkipsRecentScans()
    {
        var source = new FakeSource(ProviderIds.Claude, null);
        await using var tracker = new CostTracker([source], () => new LidgetSettings(), _time);

        await tracker.ScanIfStaleAsync(TimeSpan.FromSeconds(60));
        await tracker.ScanIfStaleAsync(TimeSpan.FromSeconds(60));
        _time.Advance(TimeSpan.FromSeconds(61));
        await tracker.ScanIfStaleAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(2, source.Scans);
        Assert.Empty(tracker.Latest);
    }

    [Fact]
    public async Task AFailingSourceDoesNotStopTheOthers()
    {
        var codex = new FakeSource(ProviderIds.Codex, new CostSummary(1, 1, 1, "USD"));
        await using var tracker = new CostTracker([new ThrowingSource(), codex], () => new LidgetSettings(), _time);

        await tracker.ScanAsync();

        Assert.Equal(1, codex.Scans);
    }

    private sealed class ThrowingSource : ICostSource
    {
        public string ProviderId => ProviderIds.Claude;

        public CostSummary? Scan(DateTimeOffset now, CancellationToken ct) => throw new IOException("locked");
    }
}
