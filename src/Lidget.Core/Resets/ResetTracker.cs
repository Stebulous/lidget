using Lidget.Core.Models;
using Lidget.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidget.Core.Resets;

/// <summary>
/// Compares the banked resets of each snapshot with the resets that Lidget knows, and records the changes
/// as reset events (plan decision D65). The providers list only the available resets, so a reset that goes
/// away before its expiry time counts as used in another app.
/// </summary>
public sealed class ResetTracker(HistoryStore store, TimeProvider time, ILogger<ResetTracker>? logger = null)
{
    /// <summary>
    /// After a redemption in Lidget, a provider can still report the old count for a short time. A higher
    /// count in this time does not change the stored count.
    /// </summary>
    public static readonly TimeSpan RedeemSettleTime = TimeSpan.FromMinutes(10);

    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastObserved = new(StringComparer.Ordinal);

    /// <summary>Raised after new reset events are stored.</summary>
    public event EventHandler? Changed;

    public void Apply(ProviderSnapshot snapshot)
    {
        var changed = false;
        try
        {
            lock (_gate)
            {
                changed = ApplyLocked(snapshot);
            }
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Could not store the {Provider} resets in the history", snapshot.ProviderId);
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Records that the user redeemed one reset of <paramref name="resetId"/> in Lidget.</summary>
    public void RecordRedeemed(string providerId, string resetId)
    {
        try
        {
            lock (_gate)
            {
                var now = time.GetUtcNow();
                var known = store.ReadResets(providerId).FirstOrDefault(r => r.ResetId == resetId);
                StoredReset[] saved = known is null ? [] : [known with { Count = Math.Max(0, known.Count - 1), RedeemedAt = now }];
                store.ApplyResetChanges(saved, [], [new ResetEvent(providerId, resetId, ResetEventKind.Redeemed, known?.Title, known?.ExpiresAt, now)]);
            }
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Could not store the {Provider} redemption in the history", providerId);
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool ApplyLocked(ProviderSnapshot snapshot)
    {
        var providerId = snapshot.ProviderId;
        var now = time.GetUtcNow();
        var known = store.ReadResets(providerId).ToDictionary(r => r.ResetId, StringComparer.Ordinal);
        var saved = new List<StoredReset>();
        var removed = new List<StoredReset>();
        var events = new List<ResetEvent>();

        // The expiry time is known, so a reset expires without a new reading.
        foreach (var reset in known.Values.Where(r => r.ExpiresAt is { } expires && expires <= now).ToArray())
        {
            AddEvents(events, reset, ResetEventKind.Expired, reset.Count, reset.ExpiresAt!.Value);
            removed.Add(reset);
            known.Remove(reset.ResetId);
        }

        if (snapshot.Resets is { Resets: { } listed } summary
            && (!_lastObserved.TryGetValue(providerId, out var last) || summary.ObservedAt > last))
        {
            _lastObserved[providerId] = summary.ObservedAt;
            var at = summary.ObservedAt;
            var current = listed
                .Where(r => r.Count > 0 && (r.ExpiresAt is not { } expires || expires > now))
                .DistinctBy(r => r.Id)
                .ToDictionary(r => r.Id, StringComparer.Ordinal);

            foreach (var reset in current.Values)
            {
                if (!known.TryGetValue(reset.Id, out var stored))
                {
                    stored = new StoredReset(providerId, reset.Id, reset.Title, reset.Count, reset.GrantedAt, reset.ExpiresAt, at, null);
                    saved.Add(stored);
                    events.Add(new ResetEvent(providerId, reset.Id, ResetEventKind.Seen, reset.Title, reset.ExpiresAt, reset.GrantedAt ?? at));
                    continue;
                }

                var count = reset.Count;
                if (count < stored.Count)
                {
                    AddEvents(events, stored, ResetEventKind.UsedElsewhere, stored.Count - count, at);
                }
                else if (count > stored.Count && stored.RedeemedAt is { } redeemed && at - redeemed < RedeemSettleTime)
                {
                    count = stored.Count;
                }

                var updated = stored with { Title = reset.Title ?? stored.Title, Count = count, GrantedAt = reset.GrantedAt ?? stored.GrantedAt, ExpiresAt = reset.ExpiresAt };
                if (updated != stored)
                {
                    saved.Add(updated);
                }
            }

            if (summary.ListsAllResets)
            {
                foreach (var stored in known.Values.Where(r => !current.ContainsKey(r.ResetId)))
                {
                    AddEvents(events, stored, ResetEventKind.UsedElsewhere, stored.Count, at);
                    removed.Add(stored);
                }
            }
        }

        store.ApplyResetChanges(saved, removed, events);
        return events.Count > 0;
    }

    private static void AddEvents(List<ResetEvent> events, StoredReset reset, ResetEventKind kind, int count, DateTimeOffset at)
    {
        for (var i = 0; i < count; i++)
        {
            events.Add(new ResetEvent(reset.ProviderId, reset.ResetId, kind, reset.Title, reset.ExpiresAt, at));
        }
    }
}
