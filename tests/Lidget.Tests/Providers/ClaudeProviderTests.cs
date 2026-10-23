using System.Net;
using System.Text.Json.Nodes;
using Lidget.Core.Models;
using Lidget.Providers.Claude;
using Microsoft.Extensions.Time.Testing;

namespace Lidget.Tests.Providers;

public class ClaudeProviderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 6, 22, 0, TimeSpan.Zero);

    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(Now);

    public void Dispose() => _dir.Dispose();

    private void WriteCredentials(string accessToken = "access-1", DateTimeOffset? expiresAt = null)
    {
        var document = new JsonObject
        {
            ["claudeAiOauth"] = new JsonObject
            {
                ["accessToken"] = accessToken,
                ["refreshToken"] = "refresh-1",
                ["expiresAt"] = (expiresAt ?? Now.AddHours(2)).ToUnixTimeMilliseconds(),
                ["subscriptionType"] = "pro",
            },
        };
        File.WriteAllText(_dir.File(".credentials.json"), document.ToJsonString());
    }

    private ClaudeProvider CreateProvider(FakeHttpHandler handler, IClaudeLimitFeed? feed = null)
    {
        var http = new HttpClient(handler);
        return new ClaudeProvider(
            new ClaudeCredentialStore(http, _time, claudeHome: _dir.Path),
            new ClaudeUsageClient(http, _time),
            _time,
            feeds: feed is null ? null : [new ClaudeLimitFeed(feed, () => true)]);
    }

    private sealed class FakeFeed : IClaudeLimitFeed
    {
        public List<LimitWindow> Readings { get; } = [];

        public event Action<IReadOnlyList<LimitWindow>>? ReadingsChanged;

        public IReadOnlyList<LimitWindow> Read() => Readings;

        public void Raise() => ReadingsChanged?.Invoke(Readings);
    }

    private void SetFeed(FakeFeed feed, double session, double weekly)
    {
        feed.Readings.Clear();
        feed.Readings.Add(new(ClaudeUsageMapper.SessionId, LimitKind.Session, "Session (5h)", session, Now.AddHours(2), "t3code", _time.GetUtcNow()));
        feed.Readings.Add(new(ClaudeUsageMapper.WeeklyId, LimitKind.Weekly, "Weekly", weekly, Now.AddDays(3), "t3code", _time.GetUtcNow()));
    }

    private static HttpResponseMessage TooManyRequests(TimeSpan? retryAfter)
    {
        var response = FakeHttpHandler.Json(HttpStatusCode.TooManyRequests, "{}");
        if (retryAfter is { } wait)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
        }

        return response;
    }

    private static bool IsUsage(HttpRequestMessage request) => request.RequestUri!.AbsolutePath == "/api/oauth/usage";

    [Fact]
    public async Task SuccessMapsLimitsAndPlan()
    {
        WriteCredentials();
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, Fixtures.Read("claude/usage-pro.json")));

        var snapshot = await CreateProvider(handler).RefreshAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(2, snapshot.Limits.Count);
        Assert.Equal("Pro", snapshot.PlanName);
        var request = Assert.Single(handler.Requests).Request;
        Assert.Equal("Bearer access-1", request.Headers.Authorization!.ToString());
        Assert.Equal("oauth-2025-04-20", Assert.Single(request.Headers.GetValues("anthropic-beta")));
    }

    [Fact]
    public async Task UnauthorizedRefreshesOnceAndRetries()
    {
        WriteCredentials();
        var usageCalls = 0;
        var handler = new FakeHttpHandler((request, _) =>
        {
            if (!IsUsage(request))
            {
                return FakeHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"access-2","refresh_token":"refresh-2","expires_in":3600}""");
            }

            return ++usageCalls == 1
                ? FakeHttpHandler.Json(HttpStatusCode.Unauthorized, "{}")
                : FakeHttpHandler.Json(HttpStatusCode.OK, Fixtures.Read("claude/usage-pro.json"));
        });

        var snapshot = await CreateProvider(handler).RefreshAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(2, usageCalls);
        Assert.Equal("Bearer access-2", handler.Requests[^1].Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task SecondUnauthorizedNeedsLogin()
    {
        WriteCredentials();
        var handler = new FakeHttpHandler((request, _) => IsUsage(request)
            ? FakeHttpHandler.Json(HttpStatusCode.Unauthorized, "{}")
            : FakeHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"access-2","expires_in":3600}"""));

        var snapshot = await CreateProvider(handler).RefreshAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NeedsLogin, snapshot.Status);
    }

    [Fact]
    public async Task TooManyRequestsIsRateLimitedWithRetryAfter()
    {
        WriteCredentials();
        var handler = new FakeHttpHandler((_, _) =>
        {
            var response = FakeHttpHandler.Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(600));
            return response;
        });

        var snapshot = await CreateProvider(handler).RefreshAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Equal(TimeSpan.FromMinutes(10), snapshot.RetryAfter);
    }

    [Fact]
    public async Task MissingCredentialsNeedsLogin()
    {
        var handler = new FakeHttpHandler((_, _) => throw new InvalidOperationException());

        var snapshot = await CreateProvider(handler).RefreshAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.NeedsLogin, snapshot.Status);
        Assert.Contains("Claude Code", snapshot.StatusMessage);
    }

    [Fact]
    public async Task ServerErrorIsError()
    {
        WriteCredentials();
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.InternalServerError, "oops"));

        var snapshot = await CreateProvider(handler).RefreshAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
        Assert.Contains("500", snapshot.StatusMessage);
    }

    [Fact]
    public async Task NetworkFailureIsError()
    {
        WriteCredentials();
        var handler = new FakeHttpHandler((_, _) => throw new HttpRequestException("offline"));

        var snapshot = await CreateProvider(handler).RefreshAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Error, snapshot.Status);
    }

    [Fact]
    public async Task CurrentFeedReadingsSkipTheEndpoint()
    {
        WriteCredentials();
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, Fixtures.Read("claude/usage-pro.json")));
        var feed = new FakeFeed();
        var provider = CreateProvider(handler, feed);
        await provider.RefreshAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(5));
        SetFeed(feed, 40, 60);
        var snapshot = await provider.RefreshAsync(CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal("Pro", snapshot.PlanName);
        Assert.Equal(40, snapshot.Limits.Single(l => l.Kind == LimitKind.Session).UsedPercent);

        // Old feed readings do not stop the next request.
        _time.Advance(ClaudeProvider.FeedFreshness + TimeSpan.FromMinutes(1));
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task TheEndpointIsRequestedHourlyEvenWithCurrentFeedReadings()
    {
        WriteCredentials();
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, Fixtures.Read("claude/usage-pro.json")));
        var feed = new FakeFeed();
        var provider = CreateProvider(handler, feed);
        await provider.RefreshAsync(CancellationToken.None);

        _time.Advance(ClaudeProvider.EndpointMaxAge);
        SetFeed(feed, 40, 60);
        await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RateLimitWithCurrentFeedReadingsIsOkAndWaitsForRetryAfter()
    {
        WriteCredentials();
        var handler = new FakeHttpHandler((_, _) => TooManyRequests(TimeSpan.FromMinutes(50)));
        var feed = new FakeFeed();
        SetFeed(feed, 12, 1);
        var provider = CreateProvider(handler, feed);

        var first = await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, first.Status);
        Assert.Null(first.StatusMessage);
        Assert.Equal("Pro", first.PlanName);

        // Inside the Retry-After time, no request goes out, even when the feed readings are old.
        _time.Advance(TimeSpan.FromMinutes(30));
        var second = await provider.RefreshAsync(CancellationToken.None);
        Assert.Single(handler.Requests);
        Assert.Equal(ProviderStatus.Stale, second.Status);
        Assert.Contains("limiting", second.StatusMessage);

        _time.Advance(TimeSpan.FromMinutes(21));
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RateLimitWithoutFeedsKeepsTheRemainingWait()
    {
        WriteCredentials();
        var handler = new FakeHttpHandler((_, _) => TooManyRequests(retryAfter: null));
        var provider = CreateProvider(handler);
        await provider.RefreshAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(5));
        var snapshot = await provider.RefreshAsync(CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal(ProviderStatus.RateLimited, snapshot.Status);
        Assert.Equal(ClaudeProvider.DefaultRateLimitWait - TimeSpan.FromMinutes(5), snapshot.RetryAfter);
    }

    [Fact]
    public async Task FeedReadingsArePushedWithThePlanName()
    {
        WriteCredentials();
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, Fixtures.Read("claude/usage-pro.json")));
        var feed = new FakeFeed();
        var provider = CreateProvider(handler, feed);
        await provider.RefreshAsync(CancellationToken.None);
        ProviderSnapshot? pushed = null;
        provider.SnapshotPushed += (_, s) => pushed = s;

        _time.Advance(TimeSpan.FromMinutes(1));
        SetFeed(feed, 55, 70);
        feed.Raise();

        Assert.NotNull(pushed);
        Assert.Equal(ProviderStatus.Ok, pushed.Status);
        Assert.Equal("Pro", pushed.PlanName);
        Assert.Equal(55, pushed.Limits.Single(l => l.Kind == LimitKind.Session).UsedPercent);
    }
}
