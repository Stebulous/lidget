using Lidget.Core.Models;

namespace Lidget.Providers.Claude;

/// <summary>A local source of Claude limit readings that arrive while the user works, between endpoint requests.</summary>
public interface IClaudeLimitFeed
{
    /// <summary>Raised on a background thread with the current readings.</summary>
    event Action<IReadOnlyList<LimitWindow>>? ReadingsChanged;

    /// <summary>Returns the current readings. Windows whose reset time has passed are left out.</summary>
    IReadOnlyList<LimitWindow> Read();
}

/// <summary>A feed and the setting that turns it on.</summary>
public sealed record ClaudeLimitFeed(IClaudeLimitFeed Source, Func<bool> IsEnabled);
