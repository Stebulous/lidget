using System.Net;
using System.Text.Json;
using Lidget.Core.Models;
using Lidget.Core.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidget.Providers.Claude;

public sealed class ClaudeProvider : IUsageProvider
{
    /// <summary>Feed readings newer than this are current. Lidget then shows them without an endpoint request.</summary>
    public static readonly TimeSpan FeedFreshness = TimeSpan.FromMinutes(10);

    /// <summary>While feed readings are current, Lidget still requests the endpoint at this interval, for limits that the feeds do not report.</summary>
    public static readonly TimeSpan EndpointMaxAge = TimeSpan.FromHours(1);

    /// <summary>The wait after HTTP 429 when the response has no <c>Retry-After</c> header.</summary>
    public static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromMinutes(15);

    private readonly ClaudeCredentialStore _credentials;
    private readonly ClaudeUsageClient _client;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<ClaudeLimitFeed> _feeds;
    private ProviderSnapshot? _lastEndpoint;
    private ProviderSnapshot? _lastGoodEndpoint;
    private DateTimeOffset? _endpointRetryAt;

    public ClaudeProvider(
        ClaudeCredentialStore credentials,
        ClaudeUsageClient client,
        TimeProvider time,
        ILogger<ClaudeProvider>? logger = null,
        IEnumerable<ClaudeLimitFeed>? feeds = null)
    {
        _credentials = credentials;
        _client = client;
        _time = time;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _feeds = feeds?.ToArray() ?? [];
        foreach (var feed in _feeds)
        {
            feed.Source.ReadingsChanged += OnFeedReadings;
        }
    }

    public string Id => ProviderIds.Claude;

    public string DisplayName => "Claude";

    public event EventHandler<ProviderSnapshot>? SnapshotPushed;

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken ct)
    {
        var readings = FeedReadings();
        var now = _time.GetUtcNow();
        ProviderSnapshot endpoint;
        if (_lastEndpoint is { } last && SkipEndpoint(readings, now))
        {
            endpoint = last.Status == ProviderStatus.RateLimited && _endpointRetryAt is { } retry
                ? last with { RetryAfter = retry > now ? retry - now : TimeSpan.Zero }
                : last;
        }
        else
        {
            endpoint = await ReadEndpointAsync(ct).ConfigureAwait(false);
            RecordEndpoint(endpoint, now);
        }

        return Finish(Combine(endpoint, readings, now));
    }

    /// <summary>
    /// Anthropic rate-limits the usage endpoint. Skip the request while the server asks Lidget to wait,
    /// and while current feed readings make it unnecessary.
    /// </summary>
    private bool SkipEndpoint(IReadOnlyList<LimitWindow> readings, DateTimeOffset now)
    {
        if (_endpointRetryAt is { } retry && now < retry)
        {
            return true;
        }

        return _lastGoodEndpoint is { } good
            && now - good.UpdatedAt < EndpointMaxAge
            && IsCurrent(readings, LimitKind.Session, now)
            && IsCurrent(readings, LimitKind.Weekly, now);
    }

    private void RecordEndpoint(ProviderSnapshot endpoint, DateTimeOffset now)
    {
        _lastEndpoint = endpoint;
        if (endpoint.Status == ProviderStatus.Ok)
        {
            _lastGoodEndpoint = endpoint;
            _endpointRetryAt = null;
        }
        else if (endpoint.Status == ProviderStatus.RateLimited)
        {
            _endpointRetryAt = now + (endpoint.RetryAfter is { } wait && wait > TimeSpan.Zero ? wait : DefaultRateLimitWait);
            _logger.LogInformation("Anthropic rate-limited the usage request. The next request is after {RetryAt:o}", _endpointRetryAt);
        }
    }

    private static bool IsCurrent(IReadOnlyList<LimitWindow> readings, LimitKind kind, DateTimeOffset now) =>
        readings.Any(l => l.Kind == kind && now - l.ObservedAt <= FeedFreshness);

    private IReadOnlyList<LimitWindow> FeedReadings()
    {
        var readings = new List<LimitWindow>();
        foreach (var feed in _feeds)
        {
            if (feed.IsEnabled())
            {
                readings.AddRange(feed.Source.Read());
            }
        }

        return readings;
    }

    private void OnFeedReadings(IReadOnlyList<LimitWindow> changed)
    {
        if (changed.Count == 0 || !_feeds.Any(f => f.IsEnabled()))
        {
            return;
        }

        var readings = FeedReadings();
        if (readings.Count == 0)
        {
            return;
        }

        var endpoint = _lastEndpoint ?? Failed(ProviderStatus.Stale, "Waiting for the first usage request.");
        Push(Finish(Combine(endpoint, readings, _time.GetUtcNow())));
    }

    /// <summary>Keeps the plan name of the last endpoint result when the result comes from the feeds.</summary>
    private ProviderSnapshot Finish(ProviderSnapshot snapshot) =>
        snapshot.PlanName is null && (_lastEndpoint?.PlanName ?? _lastGoodEndpoint?.PlanName) is { } plan ? snapshot with { PlanName = plan } : snapshot;

    /// <summary>
    /// Combines endpoint and feed readings: for each limit, the newer reading wins. If the endpoint failed,
    /// current feed readings keep the status <see cref="ProviderStatus.Ok"/>. Older feed readings keep the
    /// widget filled, with the status <see cref="ProviderStatus.Stale"/> and the endpoint message.
    /// </summary>
    internal static ProviderSnapshot Combine(ProviderSnapshot endpoint, IReadOnlyList<LimitWindow> feeds, DateTimeOffset now)
    {
        if (feeds.Count == 0)
        {
            return endpoint;
        }

        var merged = endpoint.Limits.ToDictionary(l => l.Id);
        foreach (var reading in feeds)
        {
            if (!merged.TryGetValue(reading.Id, out var existing) || reading.ObservedAt > existing.ObservedAt)
            {
                merged[reading.Id] = reading;
            }
        }

        var order = new Dictionary<LimitKind, int> { [LimitKind.Session] = 0, [LimitKind.Weekly] = 1, [LimitKind.Other] = 2 };
        var limits = merged.Values.OrderBy(l => order[l.Kind]).ThenBy(l => l.Id, StringComparer.Ordinal).ToArray();
        var newest = limits.Max(l => l.ObservedAt);

        if (endpoint.Status == ProviderStatus.Ok)
        {
            return endpoint with { Limits = limits, UpdatedAt = newest > endpoint.UpdatedAt ? newest : endpoint.UpdatedAt };
        }

        return limits.All(l => now - l.ObservedAt <= FeedFreshness)
            ? endpoint with { Status = ProviderStatus.Ok, Limits = limits, StatusMessage = null, RetryAfter = null, UpdatedAt = newest }
            : endpoint with { Status = ProviderStatus.Stale, Limits = limits, UpdatedAt = newest };
    }

    private async Task<ProviderSnapshot> ReadEndpointAsync(CancellationToken ct)
    {
        var credentials = await _credentials.GetCredentialsAsync(forceRefresh: false, ct).ConfigureAwait(false);
        if (credentials.OAuth is null)
        {
            return CredentialFailure(credentials.Problem!.Value);
        }

        try
        {
            var response = await _client.GetUsageAsync(credentials.OAuth.AccessToken, ct).ConfigureAwait(false);
            if (response.Status == HttpStatusCode.Unauthorized)
            {
                // The server rejected a token that looked valid. Refresh one time and try again.
                credentials = await _credentials.GetCredentialsAsync(forceRefresh: true, ct).ConfigureAwait(false);
                if (credentials.OAuth is null)
                {
                    return CredentialFailure(credentials.Problem!.Value);
                }

                response = await _client.GetUsageAsync(credentials.OAuth.AccessToken, ct).ConfigureAwait(false);
            }

            // The plan comes from the login, so a failed request still shows it.
            var snapshot = MapResponse(response, credentials.OAuth);
            return snapshot.PlanName is null
                ? snapshot with { PlanName = ClaudeUsageMapper.FormatPlan(credentials.OAuth.SubscriptionType, credentials.OAuth.RateLimitTier) }
                : snapshot;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Claude usage request failed: {Error}", ex.HttpRequestError);
            return Failed(ProviderStatus.Error, "Could not reach Anthropic. Check the network connection.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Claude usage request timed out");
            return Failed(ProviderStatus.Error, "Anthropic did not respond in time.");
        }
    }

    private ProviderSnapshot MapResponse(ClaudeUsageResponse response, ClaudeOAuth oauth)
    {
        var now = _time.GetUtcNow();
        switch (response.Status)
        {
            case HttpStatusCode.TooManyRequests:
                return Failed(ProviderStatus.RateLimited, "Anthropic is limiting usage requests. Lidget will retry later.") with
                {
                    RetryAfter = response.RetryAfter,
                };
            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.Forbidden:
                _logger.LogWarning("Claude usage request was refused (HTTP {Status})", (int)response.Status);
                return Failed(ProviderStatus.NeedsLogin, "Run `claude` and sign in again to show Claude limits.");
            case var status when (int)status is < 200 or > 299:
                _logger.LogWarning("Claude usage request failed (HTTP {Status})", (int)status);
                return Failed(ProviderStatus.Error, $"Claude usage request failed (HTTP {(int)status}).");
        }

        IReadOnlyList<LimitWindow> limits;
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            limits = ClaudeUsageMapper.Map(document.RootElement, now);
        }
        catch (JsonException)
        {
            _logger.LogWarning("Claude usage response is not valid JSON");
            return Failed(ProviderStatus.Error, "Claude returned a response that Lidget cannot read.");
        }

        if (limits.Count == 0)
        {
            _logger.LogWarning("Claude usage response has no known limits");
            return Failed(ProviderStatus.Error, "Claude returned no usage limits. The response format may have changed.");
        }

        return new ProviderSnapshot(Id, ProviderStatus.Ok, limits, null, now)
        {
            PlanName = ClaudeUsageMapper.FormatPlan(oauth.SubscriptionType, oauth.RateLimitTier),
        };
    }

    private ProviderSnapshot CredentialFailure(ClaudeCredentialProblem problem) => problem switch
    {
        ClaudeCredentialProblem.NotFound or ClaudeCredentialProblem.NotSignedIn =>
            Failed(ProviderStatus.NeedsLogin, "Sign in to Claude Code to show Claude limits."),
        ClaudeCredentialProblem.SessionExpired =>
            Failed(ProviderStatus.NeedsLogin, "Open Claude Code to sign in again."),
        _ => Failed(ProviderStatus.Error, "Could not refresh the Claude login. Lidget will retry."),
    };

    private ProviderSnapshot Failed(ProviderStatus status, string message) =>
        ProviderSnapshot.Failed(Id, status, message, _time.GetUtcNow());

    /// <summary>Publishes a reading that arrived between refreshes.</summary>
    internal void Push(ProviderSnapshot snapshot) => SnapshotPushed?.Invoke(this, snapshot);

    public ValueTask DisposeAsync()
    {
        foreach (var feed in _feeds)
        {
            feed.Source.ReadingsChanged -= OnFeedReadings;
        }

        return ValueTask.CompletedTask;
    }
}
