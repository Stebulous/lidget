using Lidget.Core.Formatting;

namespace Lidget.Tests.Core;

public class UsageFormatTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, UsageSeverity.Normal)]
    [InlineData(74.9, UsageSeverity.Normal)]
    [InlineData(75, UsageSeverity.Warning)]
    [InlineData(89.9, UsageSeverity.Warning)]
    [InlineData(90, UsageSeverity.Critical)]
    [InlineData(100, UsageSeverity.Critical)]
    public void SeverityFollowsThresholds(double percent, UsageSeverity expected)
    {
        Assert.Equal(expected, UsageFormat.Severity(percent));
    }

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(61.5, "62%")]
    [InlineData(99.4, "99%")]
    [InlineData(-3, "0%")]
    public void FormatsPercent(double percent, string expected)
    {
        Assert.Equal(expected, UsageFormat.Percent(percent));
    }

    [Theory]
    [InlineData(-5, "now")]
    [InlineData(30, "<1m")]
    [InlineData(61, "2m")]
    [InlineData(45 * 60, "45m")]
    [InlineData(3 * 3600 + 12 * 60, "3h 12m")]
    [InlineData(2 * 86400 + 4 * 3600, "2d 4h")]
    public void FormatsCountdown(int seconds, string expected)
    {
        Assert.Equal(expected, UsageFormat.Countdown(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void ResetTextHandlesUnknownTime()
    {
        Assert.Equal("Resets in 1h 0m", UsageFormat.ResetText(Now.AddHours(1), Now));
        Assert.Equal("Reset time unknown", UsageFormat.ResetText(null, Now));
    }

    [Theory]
    [InlineData("0", "$0.00")]
    [InlineData("0.004", "<$0.01")]
    [InlineData("26.1875", "$26.19")]
    [InlineData("999.994", "$999.99")]
    [InlineData("1204.5", "$1,205")]
    public void FormatsUsd(string amount, string expected)
    {
        Assert.Equal(expected,
            UsageFormat.Usd(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData(10, "just now")]
    [InlineData(5 * 60, "5 min ago")]
    [InlineData(3 * 3600, "3 h ago")]
    [InlineData(2 * 86400, "2 d ago")]
    public void FormatsAgo(int seconds, string expected)
    {
        Assert.Equal(expected, UsageFormat.Ago(Now.AddSeconds(-seconds), Now));
    }
}
