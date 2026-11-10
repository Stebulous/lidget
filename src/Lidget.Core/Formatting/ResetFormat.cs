using System.Globalization;
using Lidget.Core.Models;
using Lidget.Core.Storage;

namespace Lidget.Core.Formatting;

/// <summary>Texts for banked resets in the widget, the flyout, and notifications.</summary>
public static class ResetFormat
{
    /// <summary>A reset in its last 24 hours shows in amber (plan decision D64).</summary>
    public static readonly TimeSpan ExpiringSoon = TimeSpan.FromHours(24);

    /// <summary>The text for a reset without a provider title.</summary>
    public const string DefaultTitle = "Limit reset";

    public static string Title(string? title) => string.IsNullOrWhiteSpace(title) ? DefaultTitle : title;

    /// <summary>The count, for example "No resets", "1 reset", or "3 resets".</summary>
    public static string Count(int count) => count switch
    {
        <= 0 => "No resets",
        1 => "1 reset",
        _ => $"{count} resets",
    };

    /// <summary>The row text, for example "3 available · next expires in 20d 4h".</summary>
    public static string SummaryText(ResetSummary summary, DateTimeOffset now)
    {
        if (summary.AvailableCount <= 0)
        {
            return "None available";
        }

        var text = $"{summary.AvailableCount} available";
        return summary.NextToExpire(now) is { ExpiresAt: { } expires }
            ? $"{text} · next expires in {UsageFormat.Countdown(expires - now)}"
            : text;
    }

    /// <summary>The expiry of one reset, for example "Expires Nov 30, 2:08 PM" or "Expires in 5h 12m".</summary>
    public static string ExpiryText(DateTimeOffset? expiresAt, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (expiresAt is not { } expires)
        {
            return "No expiry time";
        }

        var remaining = expires - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "Expired";
        }

        return remaining <= ExpiringSoon
            ? $"Expires in {UsageFormat.Countdown(remaining)}"
            : $"Expires {DateText(expires, zone)}";
    }

    /// <summary>A local date and time, for example "Nov 30, 2:08 PM".</summary>
    public static string DateText(DateTimeOffset time, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(time, zone).ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>The result of a redemption in plain words.</summary>
    public static string OutcomeText(RedeemOutcome outcome, string providerName) => outcome switch
    {
        RedeemOutcome.Reset => "The limits were reset.",
        RedeemOutcome.NothingToReset => "No limit can be reset now. The reset is still available.",
        RedeemOutcome.NoReset => $"{providerName} has no reset to use.",
        RedeemOutcome.AlreadyRedeemed => "This reset was already used.",
        RedeemOutcome.CoolingDown => $"{providerName} does not permit a reset now. Try again later.",
        RedeemOutcome.RateLimited => $"{providerName} is limiting requests. Try again later.",
        RedeemOutcome.NeedsLogin => $"Sign in to {providerName} again to use a reset.",
        RedeemOutcome.Unconfirmed => "Could not confirm the reset. If the limits do not change in a moment, try again.",
        _ => "Could not use the reset. Try again.",
    };

    /// <summary>The history text of an event, for example "Used in Lidget".</summary>
    public static string EventText(ResetEventKind kind) => kind switch
    {
        ResetEventKind.Seen => "Received",
        ResetEventKind.Redeemed => "Used in Lidget",
        ResetEventKind.UsedElsewhere => "Used in another app",
        _ => "Expired",
    };
}
