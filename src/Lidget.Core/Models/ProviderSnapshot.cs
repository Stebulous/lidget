namespace Lidget.Core.Models;

public enum LimitKind
{
    Session,
    Weekly,
    Other,
}

public enum ProviderStatus
{
    Ok,
    Stale,
    NeedsLogin,
    NotInstalled,
    RateLimited,
    Error,
}

/// <summary>One usage limit window, for example the Claude 5-hour session limit.</summary>
/// <param name="Id">Stable identifier, for example <c>claude.session</c>.</param>
/// <param name="UsedPercent">Used share of the limit, from 0 to 100.</param>
/// <param name="Source">Where the reading came from, for example <c>endpoint</c> or <c>statusline</c>.</param>
public sealed record LimitWindow(
    string Id,
    LimitKind Kind,
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    string Source,
    DateTimeOffset ObservedAt);

public sealed record CostSummary(decimal Today, decimal Last7Days, decimal Last30Days, string Currency)
{
    /// <summary>True when some log entries used models that the pricing table does not know.</summary>
    public bool HasUnpricedModels { get; init; }
}

public sealed record ProviderSnapshot(
    string ProviderId,
    ProviderStatus Status,
    IReadOnlyList<LimitWindow> Limits,
    CostSummary? Costs,
    string? StatusMessage,
    DateTimeOffset UpdatedAt)
{
    public string? PlanName { get; init; }

    /// <summary>Server-requested wait before the next request, for example from a <c>Retry-After</c> header.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>The limit with the highest used share: the one that stops work first.</summary>
    public LimitWindow? TightestLimit => Limits.Count == 0 ? null : Limits.MaxBy(l => l.UsedPercent);

    /// <summary>Selects the limit that the taskbar widget shows for <paramref name="metric"/>.</summary>
    public LimitWindow? SelectLimit(WidgetMetric metric) => metric switch
    {
        WidgetMetric.Session => Limits.FirstOrDefault(l => l.Kind == LimitKind.Session) ?? TightestLimit,
        WidgetMetric.Weekly => Limits.FirstOrDefault(l => l.Kind == LimitKind.Weekly) ?? TightestLimit,
        _ => TightestLimit,
    };

    public static ProviderSnapshot Failed(string providerId, ProviderStatus status, string message, DateTimeOffset now) =>
        new(providerId, status, [], null, message, now);
}
