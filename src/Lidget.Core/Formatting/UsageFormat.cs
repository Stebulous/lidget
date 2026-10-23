using System.Globalization;
using Lidget.Core.Models;

namespace Lidget.Core.Formatting;

public enum UsageSeverity
{
    Normal,
    Warning,
    Critical,
}

/// <summary>Text and severity rules shared by the widget, the flyout, and notifications.</summary>
public static class UsageFormat
{
    public const double WarningPercent = 75;
    public const double CriticalPercent = 90;

    public static UsageSeverity Severity(double usedPercent) => usedPercent switch
    {
        >= CriticalPercent => UsageSeverity.Critical,
        >= WarningPercent => UsageSeverity.Warning,
        _ => UsageSeverity.Normal,
    };

    public static string Percent(double usedPercent) =>
        Math.Round(Math.Clamp(usedPercent, 0, 999), MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>The share to show for a limit, from 0 to 100: the used share, or the share that is left.</summary>
    public static double DisplayPercent(double usedPercent, UsageDisplay display)
    {
        var used = Math.Clamp(usedPercent, 0, 100);
        return display == UsageDisplay.Remaining ? 100 - used : used;
    }

    /// <summary>A percentage with its meaning, for example "88% left" or "12% used".</summary>
    public static string LimitText(double usedPercent, UsageDisplay display) =>
        Percent(DisplayPercent(usedPercent, display)) + (display == UsageDisplay.Remaining ? " left" : " used");

    /// <summary>A short countdown, for example "45m", "3h 12m", or "2d 4h".</summary>
    public static string Countdown(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return "now";
        }

        if (remaining < TimeSpan.FromMinutes(1))
        {
            return "<1m";
        }

        // Round up, so that the text never shows less time than remains.
        var minutes = (long)Math.Ceiling(remaining.TotalMinutes);
        var days = minutes / 1440;
        var hours = minutes % 1440 / 60;
        var mins = minutes % 60;

        return days > 0 ? $"{days}d {hours}h"
            : hours > 0 ? $"{hours}h {mins}m"
            : $"{mins}m";
    }

    /// <summary>A US dollar amount: "$0.00", "&lt;$0.01", "$26.19", or "$1,204" from $1,000.</summary>
    public static string Usd(decimal amount) => amount switch
    {
        <= 0 => "$0.00",
        < 0.01m => "<$0.01",
        >= 1000 => "$" + Math.Round(amount, MidpointRounding.AwayFromZero).ToString("#,0", CultureInfo.InvariantCulture),
        _ => "$" + amount.ToString("0.00", CultureInfo.InvariantCulture),
    };

    public static string ResetText(DateTimeOffset? resetsAt, DateTimeOffset now) =>
        resetsAt is { } reset ? $"Resets in {Countdown(reset - now)}" : "Reset time unknown";

    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var elapsed = now - at;
        return elapsed switch
        {
            _ when elapsed < TimeSpan.FromMinutes(1) => "just now",
            _ when elapsed < TimeSpan.FromHours(1) => $"{(int)elapsed.TotalMinutes} min ago",
            _ when elapsed < TimeSpan.FromDays(1) => $"{(int)elapsed.TotalHours} h ago",
            _ => $"{(int)elapsed.TotalDays} d ago",
        };
    }
}
