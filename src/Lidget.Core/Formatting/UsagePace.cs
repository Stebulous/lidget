using Lidget.Core.Models;

namespace Lidget.Core.Formatting;

public enum PaceStatus
{
    /// <summary>The used share is near the share of the window that has passed.</summary>
    OnPace,

    /// <summary>Less of the limit is used than an even pace would use.</summary>
    Reserve,

    /// <summary>More of the limit is used than an even pace would use.</summary>
    Deficit,
}

/// <summary>Compares the use of a limit with an even use over its window.</summary>
/// <param name="ExpectedPercent">The used share at an even pace now, from 0 to 100.</param>
/// <param name="DeltaPercent">The used share minus <paramref name="ExpectedPercent"/>, in percentage points.</param>
/// <param name="RunsOutAt">
/// When the limit is used up at the average rate so far, if that is before the reset. The current time if the limit is used up.
/// </param>
public sealed record UsagePace(PaceStatus Status, double ExpectedPercent, double DeltaPercent, DateTimeOffset? RunsOutAt)
{
    /// <summary>A difference of this many percentage points or less is on pace.</summary>
    public const double Tolerance = 5;

    /// <summary>
    /// The share of the window that must pass before Lidget projects a run-out time. Earlier, a few
    /// requests give a rate that is not useful.
    /// </summary>
    public const double MinProjectionShare = 0.05;

    /// <summary>Returns the pace of <paramref name="window"/>, or null if its length or reset time is not known.</summary>
    public static UsagePace? Calculate(LimitWindow window, DateTimeOffset now)
    {
        if (window.Duration is not { } duration || duration <= TimeSpan.Zero || window.ResetsAt is not { } reset || reset <= now)
        {
            return null;
        }

        // A reset time that is farther away than the window length means a clock difference: treat it as the window start.
        var elapsed = Math.Clamp((duration - (reset - now)) / duration, 0, 1);
        var expected = elapsed * 100;
        var used = Math.Clamp(window.UsedPercent, 0, 100);
        var delta = used - expected;

        if (used >= 100)
        {
            return new UsagePace(PaceStatus.Deficit, expected, delta, now);
        }

        var status = delta > Tolerance ? PaceStatus.Deficit
            : delta < -Tolerance ? PaceStatus.Reserve
            : PaceStatus.OnPace;

        DateTimeOffset? runsOutAt = null;
        if (status == PaceStatus.Deficit && elapsed >= MinProjectionShare)
        {
            // At the average rate so far, the rest of the limit lasts (100 - used) / used of the time that has passed.
            var at = now + duration * elapsed * ((100 - used) / used);
            runsOutAt = at < reset ? at : null;
        }

        return new UsagePace(status, expected, delta, runsOutAt);
    }
}
