using Lidget.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidget.Core.Costs;

/// <summary>Computes the local cost estimate of one provider from its session logs.</summary>
public interface ICostSource
{
    string ProviderId { get; }

    /// <summary>Returns the estimate, or null when the provider has no session logs.</summary>
    CostSummary? Scan(DateTimeOffset now, CancellationToken ct);
}

/// <summary>A new cost estimate for one provider.</summary>
public sealed record CostUpdate(string ProviderId, CostSummary Summary);

/// <summary>
/// Runs the cost sources of enabled providers on a background thread, at the refresh interval
/// and on request (plan decision D38). Scans do not overlap.
/// </summary>
public sealed class CostTracker(
    IEnumerable<ICostSource> sources,
    Func<LidgetSettings> settings,
    TimeProvider time,
    ILogger<CostTracker>? logger = null) : IAsyncDisposable
{
    private readonly ICostSource[] _sources = sources.ToArray();
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<string, CostSummary> _latest = [];
    private DateTimeOffset? _lastScan;
    private Task? _loop;

    /// <summary>Raised on a thread-pool thread for each new estimate.</summary>
    public event EventHandler<CostUpdate>? CostsUpdated;

    public IReadOnlyDictionary<string, CostSummary> Latest
    {
        get
        {
            lock (_latest)
            {
                return new Dictionary<string, CostSummary>(_latest);
            }
        }
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stopping.Token));

    /// <summary>Scans when the last scan is older than <paramref name="maxAge"/>.</summary>
    public Task ScanIfStaleAsync(TimeSpan maxAge, CancellationToken ct = default) =>
        _lastScan is { } last && time.GetUtcNow() - last < maxAge ? Task.CompletedTask : ScanAsync(ct);

    public async Task ScanAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = settings();
            foreach (var source in _sources.Where(s => current.IsProviderEnabled(s.ProviderId)))
            {
                ct.ThrowIfCancellationRequested();
                await Task.Run(() => ScanSource(source, ct), ct).ConfigureAwait(false);
            }

            _lastScan = time.GetUtcNow();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ScanSource(ICostSource source, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        CostSummary? summary;
        try
        {
            summary = source.Scan(time.GetUtcNow(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not estimate {Provider} costs", source.ProviderId);
            return;
        }

        _logger.LogDebug("Scanned {Provider} session logs in {Elapsed}", source.ProviderId, time.GetElapsedTime(started));
        if (summary is null)
        {
            return;
        }

        lock (_latest)
        {
            _latest[source.ProviderId] = summary;
        }

        CostsUpdated?.Invoke(this, new CostUpdate(source.ProviderId, summary));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(settings().RefreshIntervalMinutes), time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
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

        _stopping.Dispose();
    }
}
