namespace Lidget.Core.Models;

/// <summary>
/// A reset that a provider gave to the account (plan decision D57). The user can redeem it to reset the
/// limits before its expiry time.
/// </summary>
/// <param name="Id">The Codex credit ID or the Claude grant ID. Lidget does not log it.</param>
/// <param name="Title">The provider text, for example "Full reset (Weekly + 5 hr)", or null.</param>
/// <param name="Count">The number of resets in this entry: 1 for Codex, and <c>resets_left</c> for Claude.</param>
public sealed record BankedReset(
    string Id,
    string? Title,
    int Count,
    DateTimeOffset? GrantedAt,
    DateTimeOffset? ExpiresAt)
{
    /// <summary>Whether the provider lets the account use the reset now. A paused Claude grant is not usable.</summary>
    public bool IsUsable { get; init; } = true;

    /// <summary>Whether Lidget can redeem this entry now. Claude redeems only its next grant.</summary>
    public bool CanRedeemNow { get; init; } = true;
}

/// <summary>The banked resets of one provider at one time.</summary>
/// <param name="AvailableCount">The number of resets that the account can use now.</param>
/// <param name="Resets">The entries, or null when the provider gave only the count.</param>
public sealed record ResetSummary(int AvailableCount, IReadOnlyList<BankedReset>? Resets, DateTimeOffset ObservedAt)
{
    /// <summary>The entry that the provider redeems first, or null.</summary>
    public string? NextResetId { get; init; }

    /// <summary>
    /// Whether <see cref="Resets"/> has each available reset. Codex can shorten its list, and then a missing
    /// entry does not mean that the reset is gone.
    /// </summary>
    public bool ListsAllResets => Resets is not null && Resets.Where(r => r.IsUsable).Sum(r => r.Count) >= AvailableCount;

    /// <summary>The usable entry that expires first after <paramref name="now"/>, or null.</summary>
    public BankedReset? NextToExpire(DateTimeOffset now) => Resets?
        .Where(r => r.IsUsable && r.Count > 0 && r.ExpiresAt is { } expires && expires > now)
        .MinBy(r => r.ExpiresAt);
}

/// <summary>The result of one try to redeem a banked reset.</summary>
public enum RedeemOutcome
{
    /// <summary>The provider reset the limits.</summary>
    Reset,

    /// <summary>No limit can be reset now. The reset stays available.</summary>
    NothingToReset,

    /// <summary>The account has no reset to use.</summary>
    NoReset,

    /// <summary>The reset was used before.</summary>
    AlreadyRedeemed,

    /// <summary>The provider does not permit a reset at this time.</summary>
    CoolingDown,

    RateLimited,

    NeedsLogin,

    /// <summary>The provider gave no clear result. The next try must use the same key.</summary>
    Unconfirmed,

    /// <summary>The request failed. The next try must use the same key.</summary>
    Failed,
}
