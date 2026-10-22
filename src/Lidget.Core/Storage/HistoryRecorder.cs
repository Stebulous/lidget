using Lidget.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidget.Core.Storage;

/// <summary>Stores published snapshots in the history and purges old rows once a day.</summary>
public sealed class HistoryRecorder(HistoryStore store, Func<LidgetSettings> settings, TimeProvider time, ILogger<HistoryRecorder>? logger = null)
{
    public static readonly TimeSpan PurgeInterval = TimeSpan.FromDays(1);

    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;
    private readonly Lock _gate = new();
    private DateTimeOffset? _lastPurge;

    /// <summary>Raised after new rows are stored.</summary>
    public event EventHandler? Recorded;

    public void Record(ProviderSnapshot snapshot)
    {
        try
        {
            PurgeIfDue();
            if (store.Record(snapshot) > 0)
            {
                Recorded?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Could not store the {Provider} snapshot in the history", snapshot.ProviderId);
        }
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
