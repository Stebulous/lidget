using Lidget.Core.Models;

namespace Lidget.Tests.Core;

public class ProviderSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    private static LimitWindow Limit(LimitKind kind, double used) =>
        new($"test.{kind}", kind, kind.ToString(), used, null, "test", Now);

    private static ProviderSnapshot Snapshot(params LimitWindow[] limits) =>
        new("test", ProviderStatus.Ok, limits, null, null, Now);

    [Fact]
    public void TightestLimitIsHighestUsedPercent()
    {
        var snapshot = Snapshot(Limit(LimitKind.Session, 38), Limit(LimitKind.Weekly, 52));

        Assert.Equal(LimitKind.Weekly, snapshot.TightestLimit?.Kind);
    }

    [Fact]
    public void TightestLimitIsNullWithoutLimits()
    {
        Assert.Null(Snapshot().TightestLimit);
    }

    [Theory]
    [InlineData(WidgetMetric.Tightest, LimitKind.Weekly)]
    [InlineData(WidgetMetric.Session, LimitKind.Session)]
    [InlineData(WidgetMetric.Weekly, LimitKind.Weekly)]
    public void SelectLimitFollowsMetric(WidgetMetric metric, LimitKind expected)
    {
        var snapshot = Snapshot(Limit(LimitKind.Session, 38), Limit(LimitKind.Weekly, 52));

        Assert.Equal(expected, snapshot.SelectLimit(metric)?.Kind);
    }

    [Fact]
    public void SelectLimitFallsBackToTightestWhenKindIsMissing()
    {
        var snapshot = Snapshot(Limit(LimitKind.Session, 38));

        Assert.Equal(LimitKind.Session, snapshot.SelectLimit(WidgetMetric.Weekly)?.Kind);
    }
}
