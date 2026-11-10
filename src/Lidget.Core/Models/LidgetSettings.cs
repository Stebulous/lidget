namespace Lidget.Core.Models;

public enum WidgetMetric
{
    Tightest,
    Session,
    Weekly,
}

/// <summary>The form of the taskbar widget (plan decision D66).</summary>
public enum WidgetStyle
{
    /// <summary>A logo, a bar, and a percentage for each provider.</summary>
    Bars,

    /// <summary>A logo in two rings for each provider: the weekly limit outside, and the session limit inside.</summary>
    Rings,
}

/// <summary>How the widget, the flyout, and notifications show a limit.</summary>
public enum UsageDisplay
{
    /// <summary>The share of the limit that is left, like a fuel gauge.</summary>
    Remaining,

    /// <summary>The share of the limit that is used.</summary>
    Used,
}

public sealed record LidgetSettings
{
    public const int MinRefreshMinutes = 1;
    public const int MaxRefreshMinutes = 30;

    public bool ClaudeEnabled { get; init; } = true;

    public bool CodexEnabled { get; init; } = true;

    public int RefreshIntervalMinutes { get; init; } = 5;

    public WidgetMetric WidgetMetric { get; init; } = WidgetMetric.Tightest;

    public WidgetStyle WidgetStyle { get; init; } = WidgetStyle.Bars;

    /// <summary>Whether the taskbar widget marks a provider that has a banked reset.</summary>
    public bool ShowResetsInTaskbar { get; init; } = true;

    public UsageDisplay UsageDisplay { get; init; } = UsageDisplay.Remaining;

    /// <summary>Whether the taskbar widget marks the even-pace point on its bar. The flyout always shows the pace.</summary>
    public bool ShowPaceInTaskbar { get; init; } = true;

    /// <summary>Used percentages that trigger a notification, in ascending order.</summary>
    public IReadOnlyList<int> NotificationThresholds { get; init; } = [80, 95];

    public bool NotifyOnReset { get; init; }

    /// <summary>Whether Lidget warns 24 hours and 2 hours before a banked reset expires.</summary>
    public bool WarnBeforeResetExpires { get; init; } = true;

    public bool StartWithWindows { get; init; } = true;

    public int HistoryRetentionDays { get; init; } = 90;

    /// <summary>Whether Lidget wraps the Claude Code statusline command to receive limit readings.</summary>
    public bool ClaudeStatusLineFeed { get; init; }

    /// <summary>Whether Lidget reads the Claude rate-limit events that T3 Code writes to its provider logs.</summary>
    public bool ClaudeT3CodeFeed { get; init; } = true;

    /// <summary>Distance between the widget and the system tray, in device-independent pixels.</summary>
    public double WidgetTrayOffset { get; init; } = 4;

    public bool IsProviderEnabled(string providerId) => providerId switch
    {
        ProviderIds.Claude => ClaudeEnabled,
        ProviderIds.Codex => CodexEnabled,
        _ => false,
    };

    /// <summary>Returns a copy with every value in its allowed range.</summary>
    public LidgetSettings Normalize() => this with
    {
        RefreshIntervalMinutes = Math.Clamp(RefreshIntervalMinutes, MinRefreshMinutes, MaxRefreshMinutes),
        NotificationThresholds = (NotificationThresholds ?? [])
            .Where(t => t is > 0 and <= 100)
            .Distinct()
            .Order()
            .ToArray(),
        HistoryRetentionDays = Math.Clamp(HistoryRetentionDays, 1, 3650),
        WidgetTrayOffset = Math.Clamp(WidgetTrayOffset, 0, 400),
    };
}

public static class ProviderIds
{
    public const string Claude = "claude";
    public const string Codex = "codex";
}
