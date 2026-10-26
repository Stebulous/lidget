using Lidget.Core.Formatting;
using Lidget.Core.Models;

namespace Lidget.Tests.Core;

public class UsagePaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    /// <summary>A 5-hour session window with <paramref name="elapsedHours"/> passed.</summary>
    private static LimitWindow Session(double used, double elapsedHours) =>
        new("claude.session", LimitKind.Session, "Session (5h)", used, Now + TimeSpan.FromHours(5 - elapsedHours), "endpoint", Now)
        {
            Duration = LimitWindow.SessionDuration,
        };

    [Theory]
    [InlineData(42, 2, PaceStatus.OnPace)] // 40% of the window passed
    [InlineData(30, 2, PaceStatus.Reserve)]
    [InlineData(50, 2, PaceStatus.Deficit)]
    [InlineData(45, 2, PaceStatus.OnPace)] // The tolerance is inclusive.
    public void ComparesUseWithEvenPace(double used, double elapsedHours, PaceStatus expected)
    {
        var pace = UsagePace.Calculate(Session(used, elapsedHours), Now)!;

        Assert.Equal(expected, pace.Status);
        Assert.Equal(40, pace.ExpectedPercent, 6);
        Assert.Equal(used - 40, pace.DeltaPercent, 6);
    }

    [Fact]
    public void ProjectsRunOutBeforeReset()
    {
        // 60% in 2 hours: the other 40% lasts 1h 20m at the same rate, before the reset in 3 hours.
        var pace = UsagePace.Calculate(Session(60, 2), Now)!;

        Assert.Equal(PaceStatus.Deficit, pace.Status);
        Assert.Equal(Now + TimeSpan.FromMinutes(80), pace.RunsOutAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal("20% in deficit · Runs out in 1h 20m", UsageFormat.PaceText(pace, Now));
    }

    [Fact]
    public void NoRunOutWhenTheLimitLastsToTheReset()
    {
        // 50% in 4 hours: the other 50% lasts 4 hours at the same rate, after the reset in 1 hour.
        var pace = UsagePace.Calculate(Session(50, 4), Now)!;

        Assert.Equal(PaceStatus.Reserve, pace.Status);
        Assert.Null(pace.RunsOutAt);
        Assert.Equal("30% in reserve", UsageFormat.PaceText(pace, Now));
    }

    [Fact]
    public void NoRunOutEarlyInTheWindow()
    {
        // 10 minutes is less than 5% of a 5-hour window, so the rate is not projected.
        var pace = UsagePace.Calculate(Session(20, 1.0 / 6), Now)!;

        Assert.Equal(PaceStatus.Deficit, pace.Status);
        Assert.Null(pace.RunsOutAt);
        Assert.Equal("17% in deficit", UsageFormat.PaceText(pace, Now));
    }

    [Fact]
    public void UsedUpLimitIsReached()
    {
        var pace = UsagePace.Calculate(Session(100, 4.9), Now)!;

        Assert.Equal(PaceStatus.Deficit, pace.Status);
        Assert.Equal(Now, pace.RunsOutAt);
        Assert.Equal("Limit reached", UsageFormat.PaceText(pace, Now));
    }

    [Fact]
    public void OnPaceText()
    {
        Assert.Equal("On pace", UsageFormat.PaceText(UsagePace.Calculate(Session(40, 2), Now)!, Now));
    }

    [Fact]
    public void NoPaceWithoutDurationOrReset()
    {
        var window = Session(40, 2);

        Assert.Null(UsagePace.Calculate(window with { Duration = null }, Now));
        Assert.Null(UsagePace.Calculate(window with { ResetsAt = null }, Now));
        Assert.Null(UsagePace.Calculate(window with { ResetsAt = Now }, Now));
    }

    [Fact]
    public void ResetFartherThanTheWindowCountsAsItsStart()
    {
        var window = Session(0, 0) with { ResetsAt = Now + TimeSpan.FromHours(6) };

        var pace = UsagePace.Calculate(window, Now)!;

        Assert.Equal(0, pace.ExpectedPercent);
        Assert.Equal(PaceStatus.OnPace, pace.Status);
    }
}
