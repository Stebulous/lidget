using System.Net;
using System.Text.Json;
using Lidget.Core.Models;
using Lidget.Core.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidget.Providers.Claude;

public sealed class ClaudeProvider : IUsageProvider
{
    private readonly ClaudeCredentialStore _credentials;
    private readonly ClaudeUsageClient _client;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ClaudeStatusLineSource? _statusLine;
    private readonly Func<bool> _statusLineEnabled;
    private ProviderSnapshot? _lastEndpoint;

    public ClaudeProvider(
        ClaudeCredentialStore credentials,
        ClaudeUsageClient client,
        TimeProvider time,
        ILogger<ClaudeProvider>? logger = null,
        ClaudeStatusLineSource? statusLine = null,
        Func<bool>? statusLineEnabled = null)
    {
        _credentials = credentials;
        _client = client;
        _time = time;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _statusLine = statusLine;
        _statusLineEnabled = statusLineEnabled ?? (() => statusLine is not null);
        if (_statusLine is not null)
        {
            _statusLine.ReadingsChanged += OnStatusLineReadings;
        }
    }

    public string Id => ProviderIds.Claude;

    public string DisplayName => "Claude";

    public event EventHandler<ProviderSnapshot>? SnapshotPushed;

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken ct)
    {
        var endpoint = await ReadEndpointAsync(ct).ConfigureAwait(false);
        _lastEndpoint = endpoint;
        return Combine(endpoint, StatusLineReadings());
    }

    private IReadOnlyList<LimitWindow> StatusLineReadings() =>
        _statusLine is not null && _statusLineEnabled() ? _statusLine.Read() : [];

    private void OnStatusLineReadings(IReadOnlyList<LimitWindow> readings)
    {
        if (!_statusLineEnabled() || readings.Count == 0)
        {
            return;
        }

        var endpoint = _lastEndpoint ?? Failed(ProviderStatus.Stale, "Waiting for the first usage request.");
        Push(Combine(endpoint, readings));
    }

    /// <summary>
    /// Combines endpoint and statusline readings: for each limit, the newer reading wins. If the endpoint
    /// failed, statusline readings keep the widget current, with the status <see cref="ProviderStatus.Stale"/>.
    /// </summary>
    internal static ProviderSnapshot Combine(ProviderSnapshot endpoint, IReadOnlyList<LimitWindow> statusLine)
    {
        if (statusLine.Count == 0)
        {
            return endpoint;
        }

        var merged = endpoint.Limits.ToDictionary(l => l.Id);
        foreach (var reading in statusLine)
        {
            if (!merged.TryGetValue(reading.Id, out var existing) || reading.ObservedAt > existing.ObservedAt)
            {
                merged[reading.Id] = reading;
            }
        }

        var order = new Dictionary<LimitKind, int> { [LimitKind.Session] = 0, [LimitKind.Weekly] = 1, [LimitKind.Other] = 2 };
        var limits = merged.Values.OrderBy(l => order[l.Kind]).ThenBy(l => l.Id, StringComparer.Ordinal).ToArray();
        var newest = limits.Max(l => l.ObservedAt);

        return endpoint.Status == ProviderStatus.Ok
            ? endpoint with { Limits = limits, UpdatedAt = newest > endpoint.UpdatedAt ? newest : endpoint.UpdatedAt }
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

            return MapResponse(response, credentials.OAuth);
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
        if (_statusLine is not null)
        {
            _statusLine.ReadingsChanged -= OnStatusLineReadings;
        }

        return ValueTask.CompletedTask;
    }
}
