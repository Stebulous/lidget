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

    private ClaudeProvider CreateProvider(FakeHttpHandler handler)
    {
        var http = new HttpClient(handler);
        return new ClaudeProvider(new ClaudeCredentialStore(http, _time, claudeHome: _dir.Path), new ClaudeUsageClient(http, _time), _time);
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
}
