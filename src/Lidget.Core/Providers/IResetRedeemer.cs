using Lidget.Core.Models;

namespace Lidget.Core.Providers;

/// <summary>A provider that can redeem a banked reset (plan decision D61).</summary>
public interface IResetRedeemer
{
    /// <summary>
    /// Sends one redemption request. Implementations report failures through the outcome, and throw only
    /// for cancellation.
    /// </summary>
    /// <param name="resetId">The <see cref="BankedReset.Id"/> to redeem.</param>
    /// <param name="idempotencyKey">Identifies this try. A repeated request with the same key spends one reset at most.</param>
    Task<RedeemOutcome> RedeemAsync(string resetId, string idempotencyKey, CancellationToken ct);
}

/// <summary>A provider whose reset details cost a separate lookup, so that it reads them only when necessary.</summary>
public interface IResetDetailSource
{
    /// <summary>Asks for current details at the next refresh, because the user looks at the resets.</summary>
    void RequestResetDetails();
}
