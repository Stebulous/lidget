using Lidget.Core.Models;
using Lidget.Core.Notifications;
using Microsoft.Extensions.Time.Testing;

namespace Lidget.Tests.Core;

public class ResetExpiryEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Expires = Now.AddDays(3);

    private readonly FakeTimeProvider _time = new(Now);
    private LidgetSettings _settings = new();

    private ResetExpiryEvaluator Evaluator(string? path = null) =>
        new(path, () => _settings, id => id == ProviderIds.Claude ? "Claude" : "Codex", _time);

    private ProviderSnapshot Snapshot(params BankedReset[] resets) =>
        new(ProviderIds.Codex, ProviderStatus.Ok, [], null, _time.GetUtcNow())
        {
            Resets = new ResetSummary(resets.Length, resets, _time.GetUtcNow()),
        };

    private static BankedReset Reset(string id = "a", DateTimeOffset? expiresAt = null, bool usable = true) =>
        new(id, "Full reset (Weekly + 5 hr)", 1, null, expiresAt ?? Expires) { IsUsable = usable };

    /// <summary>Moves the clock to <paramref name="before"/> the expiry time.</summary>
    private void GoTo(TimeSpan before) => _time.SetUtcNow(Expires - before);

    [Fact]
    public void WarnsOneTimeAt24HoursAndOneTimeAt2Hours()
    {
        var evaluator = Evaluator();

        Assert.Empty(evaluator.Evaluate(Snapshot(Reset())));
        GoTo(TimeSpan.FromHours(25));
        Assert.Empty(evaluator.Evaluate(Snapshot(Reset())));

        GoTo(TimeSpan.FromHours(24));
        var first = Assert.Single(evaluator.Evaluate(Snapshot(Reset())));
        GoTo(TimeSpan.FromHours(23));
        Assert.Empty(evaluator.Evaluate(Snapshot(Reset())));

        GoTo(TimeSpan.FromMinutes(119));
        var last = Assert.Single(evaluator.Evaluate(Snapshot(Reset())));
        GoTo(TimeSpan.FromMinutes(30));
        Assert.Empty(evaluator.Evaluate(Snapshot(Reset())));

        Assert.Equal("Codex: a reset expires in 1d 0h", first.Title);
        Assert.Equal("Full reset (Weekly + 5 hr). Use it before it expires.", first.Body);
        Assert.Equal("Codex: a reset expires in 1h 59m", last.Title);
        Assert.Equal("codex.reset-expiry", last.Tag);
    }

    [Fact]
    public void ARestartDoesNotRepeatAWarning()
    {
        using var dir = new TempDirectory();
        var path = dir.File("reset-warnings.json");
        Evaluator(path).Evaluate(Snapshot(Reset()));
        GoTo(TimeSpan.FromHours(20));
        Assert.Single(Evaluator(path).Evaluate(Snapshot(Reset())));

        Assert.Empty(Evaluator(path).Evaluate(Snapshot(Reset())));
    }

    [Fact]
    public void AWarningTimeThatPassedWhileLidgetDidNotRunStillWarns()
    {
        using var dir = new TempDirectory();
        var path = dir.File("reset-warnings.json");
        Evaluator(path).Evaluate(Snapshot(Reset()));

        GoTo(TimeSpan.FromHours(10));

        Assert.Single(Evaluator(path).Evaluate(Snapshot(Reset())));
    }

    [Fact]
    public void AResetFirstSeenAfterAWarningTimeGetsNoNoticeForThatTime()
    {
        var evaluator = Evaluator();
        GoTo(TimeSpan.FromHours(20));

        Assert.Empty(evaluator.Evaluate(Snapshot(Reset())));
        GoTo(TimeSpan.FromHours(19));
        Assert.Empty(evaluator.Evaluate(Snapshot(Reset())));

        GoTo(TimeSpan.FromHours(2));
        Assert.Single(evaluator.Evaluate(Snapshot(Reset())));
    }

    [Fact]
    public void AResetFirstSeenInItsLast2HoursGetsNoNotice()
    {
        var evaluator = Evaluator();
        GoTo(TimeSpan.FromHours(1));

        Assert.Empty(evaluator.Evaluate(Snapshot(Reset())));
        GoTo(TimeSpan.FromMinutes(10));
        Assert.Empty(evaluator.Evaluate(Snapshot(Reset())));
    }

    [Fact]
    public void ARedeemedResetGetsNoNotice()
    {
        var evaluator = Evaluator();
        evaluator.Evaluate(Snapshot(Reset("a"), Reset("b", Expires.AddDays(20))));

        // The reset "a" was redeemed, so the provider does not list it.
        GoTo(TimeSpan.FromHours(20));

        Assert.Empty(evaluator.Evaluate(Snapshot(Reset("b", Expires.AddDays(20)))));
    }

    [Fact]
    public void TheSettingStopsTheWarnings()
    {
        _settings = new LidgetSettings { WarnBeforeResetExpires = false };
        var evaluator = Evaluator();
        evaluator.Evaluate(Snapshot(Reset()));
        GoTo(TimeSpan.FromHours(20));

        Assert.Empty(evaluator.Evaluate(Snapshot(Reset())));

        // The setting comes on later. The warning time that passed gets no late notice.
        _settings = new LidgetSettings();
        Assert.Empty(evaluator.Evaluate(Snapshot(Reset())));
        GoTo(TimeSpan.FromHours(1));
        Assert.Single(evaluator.Evaluate(Snapshot(Reset())));
    }

    [Fact]
    public void AResetThatIsNotUsableGetsNoNotice()
    {
        var evaluator = Evaluator();
        evaluator.Evaluate(Snapshot(Reset(usable: false)));
        GoTo(TimeSpan.FromHours(20));

        Assert.Empty(evaluator.Evaluate(Snapshot(Reset(usable: false))));
    }

    [Fact]
    public void ResetsThatExpireTogetherGiveOneNotice()
    {
        var evaluator = Evaluator();
        evaluator.Evaluate(Snapshot(Reset("a"), Reset("b"), Reset("c", Expires.AddHours(1))));
        GoTo(TimeSpan.FromHours(22));

        var notice = Assert.Single(evaluator.Evaluate(Snapshot(Reset("a"), Reset("b"), Reset("c", Expires.AddHours(1)))));

        Assert.Equal("Codex: a reset expires in 22h 0m", notice.Title);
        Assert.Equal("Full reset (Weekly + 5 hr), and 2 resets more. Use them before they expire.", notice.Body);
    }

    [Fact]
    public void ACountWithoutDetailsGivesNoNotice()
    {
        var evaluator = Evaluator();
        var snapshot = new ProviderSnapshot(ProviderIds.Codex, ProviderStatus.Ok, [], null, Now) { Resets = new ResetSummary(2, null, Now) };

        Assert.Empty(evaluator.Evaluate(snapshot));
    }
}
