namespace Lidget.Core.Costs;

/// <summary>Token counts split into the buckets that have different prices.</summary>
/// <param name="Input">Input tokens at the plain input rate (not written to or read from a cache).</param>
/// <param name="CacheWrite5m">Input tokens written to the 5-minute cache.</param>
/// <param name="CacheWrite1h">Input tokens written to the 1-hour cache (twice the input rate).</param>
public readonly record struct TokenUsage(long Input, long CacheWrite5m, long CacheWrite1h, long CacheRead, long Output)
{
    /// <summary>The prompt size that selects the long-context rates.</summary>
    public long PromptTokens => Input + CacheWrite5m + CacheWrite1h + CacheRead;

    public long TotalTokens => PromptTokens + Output;
}

/// <summary>Rates above a prompt-size threshold. They apply to the whole request.</summary>
public sealed record LongContextRates(long Threshold, double? Input, double? Output, double? CacheWrite, double? CacheRead);

/// <summary>USD rates per million tokens for one model.</summary>
public sealed record ModelRates(double Input, double Output, double CacheWrite, double CacheRead)
{
    private const double CacheWrite1hInputMultiplier = 2;

    public LongContextRates? LongContext { get; init; }

    /// <summary>Price multiplier of the fast (priority) service tier.</summary>
    public double Fast { get; init; } = 1;

    /// <summary>Price multiplier of the ultrafast tier, if the model has a separate one.</summary>
    public double? Ultrafast { get; init; }

    public ModelRates Scaled(double factor) => new(Input * factor, Output * factor, CacheWrite * factor, CacheRead * factor)
    {
        LongContext = LongContext is { } lc
            ? lc with { Input = lc.Input * factor, Output = lc.Output * factor, CacheWrite = lc.CacheWrite * factor, CacheRead = lc.CacheRead * factor }
            : null,
    };

    /// <summary>The USD cost of one request.</summary>
    public double Cost(TokenUsage tokens, bool fast = false, bool ultrafast = false)
    {
        var useLong = LongContext is { } lc && tokens.PromptTokens > lc.Threshold;
        var input = Select(Input, LongContext?.Input, useLong);
        var output = Select(Output, LongContext?.Output, useLong);
        var cacheWrite = Select(CacheWrite, LongContext?.CacheWrite, useLong);
        var cacheRead = Select(CacheRead, LongContext?.CacheRead, useLong);

        var cost = (tokens.Input * input)
            + (tokens.Output * output)
            + (tokens.CacheWrite5m * cacheWrite)
            + (tokens.CacheWrite1h * input * CacheWrite1hInputMultiplier)
            + (tokens.CacheRead * cacheRead);

        var multiplier = ultrafast ? Ultrafast ?? Fast : fast ? Fast : 1;
        return cost / 1_000_000 * multiplier;
    }

    private static double Select(double baseRate, double? longRate, bool useLong) => useLong ? longRate ?? baseRate : baseRate;
}
