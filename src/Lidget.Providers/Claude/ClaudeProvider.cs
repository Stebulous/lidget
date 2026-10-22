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

    public ClaudeProvider(ClaudeCredentialStore credentials, ClaudeUsageClient client, TimeProvider time, ILogger<ClaudeProvider>? logger = null)
    {
        _credentials = credentials;
        _client = client;
        _time = time;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public string Id => ProviderIds.Claude;

    public string DisplayName => "Claude";

    public event EventHandler<ProviderSnapshot>? SnapshotPushed;

    public async Task<ProviderSnapshot> RefreshAsync(CancellationToken ct)
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

        return new ProviderSnapshot(Id, ProviderStatus.Ok, limits, null, null, now)
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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
