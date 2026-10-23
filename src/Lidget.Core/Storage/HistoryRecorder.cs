using Lidget.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidget.Core.Storage;

/// <summary>
/// Stores published snapshots in the history and purges old rows once a day. A feed can report a reading
/// after each response, so the recorder skips a reading that repeats the last stored value of its limit.
/// </summary>
public sealed class HistoryRecorder(HistoryStore store, Func<LidgetSettings> settings, TimeProvider time, ILogger<HistoryRecorder>? logger = null)
{
    public static readonly TimeSpan PurgeInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// The longest time between two stored readings with the same value. It is shorter than the chart gap
    /// threshold, so that a steady value still draws a line.
    /// </summary>
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromMinutes(10);

    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;
    private readonly Lock _gate = new();
    private DateTimeOffset? _lastPurge;
    private readonly Dictionary<string, LimitWindow> _lastStored = new(StringComparer.Ordinal);

    /// <summary>Raised after new rows are stored.</summary>
    public event EventHandler? Recorded;

    public void Record(ProviderSnapshot snapshot)
    {
        try
        {
            PurgeIfDue();
            var limits = NewReadings(snapshot);
            if (limits.Count > 0 && store.Record(snapshot with { Limits = limits }) > 0)
            {
                Recorded?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Could not store the {Provider} snapshot in the history", snapshot.ProviderId);
        }
    }

    private List<LimitWindow> NewReadings(ProviderSnapshot snapshot)
    {
        var result = new List<LimitWindow>();
        if (snapshot.Status != ProviderStatus.Ok)
        {
            return result;
        }

        lock (_gate)
        {
            foreach (var limit in snapshot.Limits)
            {
                var key = $"{snapshot.ProviderId}/{limit.Id}";
                if (_lastStored.TryGetValue(key, out var last)
                    && limit.UsedPercent == last.UsedPercent
                    && limit.ObservedAt >= last.ObservedAt
                    && limit.ObservedAt - last.ObservedAt < RepeatInterval)
                {
                    continue;
                }

                if (last is null || limit.ObservedAt > last.ObservedAt)
                {
                    _lastStored[key] = limit;
                }

                result.Add(limit);
            }
        }

        return result;
    }

    private void PurgeIfDue()
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_lastPurge is { } last && now - last < PurgeInterval)
            {
                return;
            }

            _lastPurge = now;
        }

        var deleted = store.Purge(settings().HistoryRetentionDays, now);
        if (deleted > 0)
        {
            _logger.LogInformation("Purged {Count} history rows", deleted);
        }
    }
}
