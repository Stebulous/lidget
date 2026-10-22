using Lidget.Core.Models;
using Lidget.Core.Notifications;
using Microsoft.Extensions.Time.Testing;

namespace Lidget.Tests.Core;

public class ThresholdEvaluatorTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
    private LidgetSettings _settings = new();

    private DateTimeOffset Reset => new(2026, 10, 22, 15, 0, 0, TimeSpan.Zero);

    private ThresholdEvaluator Evaluator(string? path = null) =>
        new(path, () => _settings, id => id == ProviderIds.Claude ? "Claude" : "Codex", _time);

    private ProviderSnapshot Snapshot(double used, DateTimeOffset? resetsAt, ProviderStatus status = ProviderStatus.Ok, string limitId = "claude.five_hour") =>
        new(ProviderIds.Claude, status,
            [new LimitWindow(limitId, LimitKind.Session, "Session (5h)", used, resetsAt, "endpoint", _time.GetUtcNow())],
            null, _time.GetUtcNow());

    [Fact]
    public void NotifiesOneTimeForEachThresholdInAWindow()
    {
        var evaluator = Evaluator();

        Assert.Empty(evaluator.Evaluate(Snapshot(79, Reset)));
        var first = Assert.Single(evaluator.Evaluate(Snapshot(81, Reset)));
        Assert.Empty(evaluator.Evaluate(Snapshot(85, Reset.AddMinutes(1))));
        var second = Assert.Single(evaluator.Evaluate(Snapshot(96, Reset)));
        Assert.Empty(evaluator.Evaluate(Snapshot(97, Reset)));

        Assert.Equal("Claude: Session (5h) at 81%", first.Title);
        Assert.Equal("Usage passed 80%. Resets in 3h 0m.", first.Body);
        Assert.Equal("Claude: Session (5h) at 96%", second.Title);
        Assert.Equal("claude.claude.five_hour", second.Tag);
    }

    [Fact]
    public void AJumpPastSeveralThresholdsNotifiesOnce()
    {
        var notice = Assert.Single(Evaluator().Evaluate(Snapshot(100, Reset)));
        Assert.Equal("Claude: Session (5h) limit reached", notice.Title);
    }

    [Fact]
    public void ANewWindowNotifiesAgain()
    {
        var evaluator = Evaluator();
        Assert.Single(evaluator.Evaluate(Snapshot(85, Reset)));

        _time.Advance(TimeSpan.FromHours(4));
        Assert.Empty(evaluator.Evaluate(Snapshot(10, Reset.AddHours(5))));
        Assert.Single(evaluator.Evaluate(Snapshot(82, Reset.AddHours(5))));
    }

    [Fact]
    public void ResetNoticeFollowsItsSetting()
    {
        var evaluator = Evaluator();
        Assert.Single(evaluator.Evaluate(Snapshot(90, Reset)));
        _time.Advance(TimeSpan.FromHours(4));
        Assert.Empty(evaluator.Evaluate(Snapshot(5, Reset.AddHours(5))));

        _settings = new LidgetSettings { NotifyOnReset = true };
        Assert.Single(evaluator.Evaluate(Snapshot(90, Reset.AddHours(5))));
        _time.Advance(TimeSpan.FromHours(5));
        var reset = Assert.Single(evaluator.Evaluate(Snapshot(2, Reset.AddHours(10))));
        Assert.Equal("Claude: Session (5h) reset", reset.Title);
        Assert.Equal("Usage is now 2%. Resets in 4h 0m.", reset.Body);

        // A window that did not reach a threshold resets without a notice.
        Assert.Empty(evaluator.Evaluate(Snapshot(3, Reset.AddHours(15))));
    }

    [Fact]
    public void StateSurvivesARestart()
    {
        using var dir = new TempDirectory();
        var path = dir.File("notifications.json");

        Assert.Single(Evaluator(path).Evaluate(Snapshot(85, Reset)));
        Assert.Empty(Evaluator(path).Evaluate(Snapshot(86, Reset)));
        Assert.Single(Evaluator(path).Evaluate(Snapshot(95, Reset)));
    }

    [Fact]
    public void AnUnreadableStateFileStartsEmpty()
    {
        using var dir = new TempDirectory();
        var path = dir.File("notifications.json");
        File.WriteAllText(path, "{ not json");

        Assert.Single(Evaluator(path).Evaluate(Snapshot(85, Reset)));
    }

    [Fact]
    public void IgnoresSnapshotsThatAreNotOk()
    {
        Assert.Empty(Evaluator().Evaluate(Snapshot(99, Reset, ProviderStatus.Stale)));
    }

    [Fact]
    public void AnUnknownResetTimeKeepsTheWindow()
    {
        var evaluator = Evaluator();
        Assert.Single(evaluator.Evaluate(Snapshot(85, null)));
        Assert.Empty(evaluator.Evaluate(Snapshot(86, Reset)));
        Assert.Empty(evaluator.Evaluate(Snapshot(87, null)));
        Assert.Equal("Usage passed 95%. Reset time unknown.", Assert.Single(evaluator.Evaluate(Snapshot(95, null))).Body);
    }

    [Fact]
    public void LimitsAreIndependent()
    {
        var evaluator = Evaluator();
        Assert.Single(evaluator.Evaluate(Snapshot(85, Reset)));
        Assert.Single(evaluator.Evaluate(Snapshot(85, Reset, limitId: "claude.seven_day")));
    }

    [Fact]
    public void NoThresholdsMeansNoNotifications()
    {
        _settings = new LidgetSettings { NotificationThresholds = [] };
        Assert.Empty(Evaluator().Evaluate(Snapshot(100, Reset)));
    }
}
