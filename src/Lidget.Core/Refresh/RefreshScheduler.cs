using System.Collections.Concurrent;
using Lidget.Core.Models;
using Lidget.Core.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidget.Core.Refresh;

/// <summary>
/// Refreshes each enabled provider on the configured interval and publishes the results.
/// A failure in one provider never blocks the others.
/// </summary>
public sealed class RefreshScheduler : IAsyncDisposable
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly Func<LidgetSettings> _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, ProviderState> _state = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    public RefreshScheduler(
        IEnumerable<IUsageProvider> providers,
        Func<LidgetSettings> settings,
        TimeProvider time,
        ILogger<RefreshScheduler>? logger = null)
    {
        _providers = providers.ToArray();
        _settings = settings;
        _time = time;
        _logger = logger ?? (ILogger)NullLogger.Instance;

        foreach (var provider in _providers)
        {
            _state[provider.Id] = new ProviderState();
            provider.SnapshotPushed += OnSnapshotPushed;
        }
    }

    /// <summary>Raised on a thread-pool thread for each new snapshot.</summary>
    public event EventHandler<ProviderSnapshot>? SnapshotPublished;

    public IReadOnlyDictionary<string, ProviderSnapshot> Latest =>
        _state.Where(kv => kv.Value.Latest is not null).ToDictionary(kv => kv.Key, kv => kv.Value.Latest!);

    public void Start()
    {
        _loop ??= Task.Run(() => RunLoopAsync(_stopping.Token));
    }

    /// <summary>Refreshes every enabled provider whose interval has passed and whose backoff has ended.</summary>
    public Task RunDueAsync(CancellationToken ct = default) => RefreshWhereAsync(IsDue, ct);

    /// <summary>
    /// Refreshes every enabled provider whose data is older than <paramref name="maxAge"/>.
    /// Rate-limit backoff still applies, because extra requests extend it.
    /// </summary>
    public Task RefreshStaleAsync(TimeSpan maxAge, CancellationToken ct = default) =>
        RefreshWhereAsync((state, now) => !InBackoff(state, now) && (state.LastAttempt is null || now - state.LastAttempt >= maxAge), ct);

    /// <summary>Refreshes every enabled provider that is not in rate-limit backoff.</summary>
    public Task RefreshNowAsync(CancellationToken ct = default) =>
        RefreshWhereAsync((state, now) => !InBackoff(state, now), ct);

    private Task RefreshWhereAsync(Func<ProviderState, DateTimeOffset, bool> predicate, CancellationToken ct)
    {
        var settings = _settings();
        var now = _time.GetUtcNow();
        var due = _providers
            .Where(p => settings.IsProviderEnabled(p.Id) && predicate(_state[p.Id], now))
            .Select(p => RefreshProviderAsync(p, ct));
        return Task.WhenAll(due);
    }

    private bool IsDue(ProviderState state, DateTimeOffset now)
    {
        if (InBackoff(state, now))
        {
            return false;
        }

        var interval = TimeSpan.FromMinutes(_settings().RefreshIntervalMinutes);
        return state.LastAttempt is null || now - state.LastAttempt >= interval;
    }

    private static bool InBackoff(ProviderState state, DateTimeOffset now) =>
        state.NextAllowed is { } next && now < next;

    private async Task RefreshProviderAsync(IUsageProvider provider, CancellationToken ct)
    {
        var state = _state[provider.Id];
        if (!await state.Gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return; // A refresh of this provider is already running.
        }

        try
        {
            state.LastAttempt = _time.GetUtcNow();
            ProviderSnapshot snapshot;
            try
            {
                snapshot = await provider.RefreshAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Refresh of {Provider} failed", provider.Id);
                snapshot = ProviderSnapshot.Failed(provider.Id, ProviderStatus.Error, "Could not read usage. See the log for details.", _time.GetUtcNow());
            }

            UpdateBackoff(state, snapshot);
            Publish(state, snapshot);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private void UpdateBackoff(ProviderState state, ProviderSnapshot snapshot)
    {
        if (snapshot.Status != ProviderStatus.RateLimited)
        {
            state.ConsecutiveRateLimits = 0;
            state.NextAllowed = null;
            return;
        }

        state.ConsecutiveRateLimits++;
        var interval = TimeSpan.FromMinutes(_settings().RefreshIntervalMinutes);
        var exponential = interval * Math.Pow(2, state.ConsecutiveRateLimits - 1);
        var delay = exponential > MaxBackoff ? MaxBackoff : exponential;
        if (snapshot.RetryAfter is { } retryAfter && retryAfter > delay)
        {
            delay = retryAfter;
        }

        state.NextAllowed = state.LastAttempt!.Value + delay;
        _logger.LogWarning("{Provider} is rate limited. Next request after {NextAllowed:o}", snapshot.ProviderId, state.NextAllowed);
    }

    private void Publish(ProviderState state, ProviderSnapshot snapshot)
    {
        lock (state)
        {
            // Keep the last good readings visible when a refresh fails.
            var previous = state.Latest;
            if (snapshot.Limits.Count == 0 && snapshot.Status != ProviderStatus.Ok && previous is { Limits.Count: > 0 })
            {
                snapshot = snapshot with
                {
                    Limits = previous.Limits,
                    PlanName = snapshot.PlanName ?? previous.PlanName,
                    UpdatedAt = previous.UpdatedAt,
                };
            }

            state.Latest = snapshot;
        }

        SnapshotPublished?.Invoke(this, snapshot);
    }

    private void OnSnapshotPushed(object? sender, ProviderSnapshot snapshot)
    {
        if (!_settings().IsProviderEnabled(snapshot.ProviderId) || !_state.TryGetValue(snapshot.ProviderId, out var state))
        {
            return;
        }

        Publish(state, snapshot);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunDueAsync(ct).ConfigureAwait(false);
                await Task.Delay(TickInterval, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Refresh loop iteration failed");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        foreach (var provider in _providers)
        {
            provider.SnapshotPushed -= OnSnapshotPushed;
        }

        _stopping.Dispose();
    }

    private sealed class ProviderState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public DateTimeOffset? LastAttempt { get; set; }

        public DateTimeOffset? NextAllowed { get; set; }

        public int ConsecutiveRateLimits { get; set; }

        public ProviderSnapshot? Latest { get; set; }
    }
}
