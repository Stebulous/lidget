using System.Collections.Concurrent;
using Lidget.Core.Models;
using Lidget.Core.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidget.Core.Resets;

/// <summary>
/// Redeems a banked reset for the user (plan decision D61). It has no user interface code: the caller must
/// get the confirmation of the user first. One redemption runs at a time for each provider.
/// </summary>
public sealed class ResetRedemptionService
{
    private readonly IReadOnlyDictionary<string, IResetRedeemer> _redeemers;
    private readonly RedeemKeyStore _keys;
    private readonly ResetTracker? _tracker;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public ResetRedemptionService(
        IEnumerable<IUsageProvider> providers,
        RedeemKeyStore keys,
        ResetTracker? tracker = null,
        ILogger<ResetRedemptionService>? logger = null)
    {
        _redeemers = providers
            .Where(p => p is IResetRedeemer)
            .ToDictionary(p => p.Id, p => (IResetRedeemer)p, StringComparer.Ordinal);
        _keys = keys;
        _tracker = tracker;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Raised after a redemption with a final result, with the provider ID, so that the caller can refresh the provider.</summary>
    public event EventHandler<string>? Completed;

    public bool CanRedeem(string providerId) => _redeemers.ContainsKey(providerId);

    /// <summary>
    /// Whether the provider gave a final result. If it did not, the next try for the same reset uses the same key.
    /// </summary>
    public static bool IsFinal(RedeemOutcome outcome) => outcome is not (RedeemOutcome.Unconfirmed or RedeemOutcome.Failed);

    public async Task<RedeemOutcome> RedeemAsync(string providerId, string resetId, CancellationToken ct = default)
    {
        if (!_redeemers.TryGetValue(providerId, out var redeemer))
        {
            return RedeemOutcome.Failed;
        }

        var gate = _gates.GetOrAdd(providerId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string key;
            bool wasPending;
            try
            {
                (key, wasPending) = _keys.GetOrCreate(providerId, resetId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Without a saved key, a lost response could spend a second reset. Do not send the request.
                _logger.LogWarning("Could not save the redemption key, so no request was sent: {Error}", ex.Message);
                return RedeemOutcome.Failed;
            }

            RedeemOutcome outcome;
            try
            {
                outcome = await redeemer.RedeemAsync(resetId, key, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("The {Provider} redemption failed: {Error}", providerId, ex.GetType().Name);
                outcome = RedeemOutcome.Failed;
            }

            _logger.LogInformation("Redemption of a {Provider} reset: {Outcome}", providerId, outcome);
            if (!IsFinal(outcome))
            {
                return outcome;
            }

            ClearKey(providerId);

            // "Already redeemed" for a key from an earlier try means that the earlier try spent the reset.
            if (outcome == RedeemOutcome.Reset || (outcome == RedeemOutcome.AlreadyRedeemed && wasPending))
            {
                _tracker?.RecordRedeemed(providerId, resetId);
            }

            Completed?.Invoke(this, providerId);
            return outcome;
        }
        finally
        {
            gate.Release();
        }
    }

    private void ClearKey(string providerId)
    {
        try
        {
            _keys.Clear(providerId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not remove the redemption key: {Error}", ex.Message);
        }
    }
}
