using System.Net;
using System.Text.Json.Nodes;
using Lidget.Core.Models;
using Lidget.Providers;
using Lidget.Providers.Claude;
using Microsoft.Extensions.Time.Testing;

namespace Lidget.Tests.Providers;

/// <summary>Banked resets of the Claude provider: the reset block, the reset data age (D59), and redemption (D61).</summary>
public class ClaudeProviderResetTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 6, 22, 0, TimeSpan.Zero);

    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly ListLogger<ClaudeProvider> _log = new();

    public void Dispose() => _dir.Dispose();

    private string ConfigPath => _dir.File(".claude.json");

    private void WriteCredentials() =>
        File.WriteAllText(_dir.File(".credentials.json"), new JsonObject
        {
            ["claudeAiOauth"] = new JsonObject
            {
                ["accessToken"] = "access-1",
                ["refreshToken"] = "refresh-1",
                ["expiresAt"] = Now.AddDays(2).ToUnixTimeMilliseconds(),
                ["subscriptionType"] = "pro",
            },
        }.ToJsonString());

    /// <summary>Writes the Claude Code configuration file, with a saved response from <paramref name="fetchedAt"/> if it is given.</summary>
    private void WriteConfig(DateTimeOffset? fetchedAt = null, string? organization = "org-1")
    {
        var config = fetchedAt is { } at
            ? JsonNode.Parse(ClaudeCodeUsageCacheTests.Config(at, session: 37, weekly: 3))!.AsObject()
            : new JsonObject { ["oauthAccount"] = new JsonObject { ["accountUuid"] = "account-1" } };
        if (organization is not null)
        {
            config["oauthAccount"]!["organizationUuid"] = organization;
        }

        config["lastReleaseNotesSeen"] = "2.1.300";

        File.WriteAllText(ConfigPath, config.ToJsonString());

        // The cache reads the file again only when its time or its length changes. Two fast writes can have the same time.
        File.SetLastWriteTimeUtc(ConfigPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_configWrites));
    }

    private int _configWrites;

    private ClaudeProvider CreateProvider(FakeHttpHandler handler)
    {
        // The app client, with the Lidget User-Agent as a default header. The Claude requests replace it.
        var http = LidgetHttp.Create(handler);
        return new ClaudeProvider(
            new ClaudeCredentialStore(http, _time, claudeHome: _dir.Path),
            new ClaudeUsageClient(http, _time, ConfigPath),
            _time,
            _log,
            cache: new ClaudeCodeUsageCache(ConfigPath, _time),
            waitPath: _dir.File(ClaudeProvider.WaitFileName),
            resetsPath: _dir.File(ClaudeProvider.ResetsFileName),
            resetClient: new ClaudeResetClient(http, ConfigPath));
    }

    private static bool IsUsage(HttpRequestMessage request) => request.RequestUri!.AbsolutePath == "/api/oauth/usage";

    private static bool IsReset(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("/reset_rate_limits", StringComparison.Ordinal);

    private static HttpResponseMessage Usage() => FakeHttpHandler.Json(HttpStatusCode.OK, Fixtures.Read("claude/usage-resets.json"));

    private static FakeHttpHandler UsageHandler() => new((_, _) => Usage());

    private static FakeHttpHandler ResetHandler(Func<HttpResponseMessage> reset) =>
        new((request, _) => IsReset(request) ? reset() : Usage());

    private static int UsageRequests(FakeHttpHandler handler) => handler.Requests.Count(r => IsUsage(r.Request));

    /// <summary>Moves the clock, and keeps the saved response of Claude Code current, so that only the reset rule can ask for a request.</summary>
    private void AdvanceWithACurrentSavedResponse(TimeSpan time)
    {
        _time.Advance(time);
        WriteConfig(_time.GetUtcNow().AddMinutes(-1));
    }

    [Fact]
    public async Task TheUsageRequestAsksForTheResetBlock()
    {
        WriteCredentials();
        var handler = UsageHandler();

        var snapshot = await CreateProvider(handler).RefreshAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests).Request;
        Assert.Equal("https://api.anthropic.com/api/oauth/usage?cedar_ember=1", request.RequestUri!.AbsoluteUri);

        // Without a version in ~/.claude.json, the request uses the fallback version.
        Assert.Equal($"claude-cli/{ClaudeAccountConfig.FallbackClientVersion} (external, cli)", request.Headers.UserAgent.ToString());
        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(2, snapshot.Limits.Count);
        Assert.Equal(3, snapshot.Resets!.AvailableCount);
        Assert.Equal("grant_a1", snapshot.Resets.NextResetId);
    }

    [Fact]
    public async Task AResponseWithoutTheBlockHasNoResetData()
    {
        WriteCredentials();
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, Fixtures.Read("claude/usage-pro.json")));

        var snapshot = await CreateProvider(handler).RefreshAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Null(snapshot.Resets);
    }

    [Fact]
    public async Task ARefreshFromTheSavedResponseKeepsTheLastResets()
    {
        WriteCredentials();
        var handler = UsageHandler();
        var provider = CreateProvider(handler);
        var first = await provider.RefreshAsync(CancellationToken.None);

        AdvanceWithACurrentSavedResponse(TimeSpan.FromMinutes(40));
        var second = await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, UsageRequests(handler));
        Assert.Equal(ClaudeCodeUsageCache.SourceName, second.Limits[0].Source);
        Assert.Same(first.Resets, second.Resets);
    }

    [Fact]
    public async Task AFailedRequestKeepsTheLastResets()
    {
        WriteCredentials();
        var fail = false;
        var handler = new FakeHttpHandler((_, _) => fail ? FakeHttpHandler.Json(HttpStatusCode.InternalServerError, "{}") : Usage());
        var provider = CreateProvider(handler);
        var first = await provider.RefreshAsync(CancellationToken.None);

        fail = true;
        _time.Advance(TimeSpan.FromMinutes(31));
        var second = await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, UsageRequests(handler));
        Assert.Same(first.Resets, second.Resets);
    }

    [Fact]
    public async Task ResetDataOlderThan6HoursAsksForARequest()
    {
        WriteCredentials();
        var handler = UsageHandler();
        var provider = CreateProvider(handler);
        await provider.RefreshAsync(CancellationToken.None);

        // The saved response of Claude Code is current, so only the reset rule can ask for a request.
        for (var i = 0; i < 11; i++)
        {
            AdvanceWithACurrentSavedResponse(TimeSpan.FromMinutes(30));
            await provider.RefreshAsync(CancellationToken.None);
        }

        Assert.Equal(1, UsageRequests(handler));

        AdvanceWithACurrentSavedResponse(TimeSpan.FromMinutes(30));
        var snapshot = await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, UsageRequests(handler));
        Assert.Equal(_time.GetUtcNow(), snapshot.Resets!.ObservedAt);
    }

    [Fact]
    public async Task TheResetRuleObeysTheRetryAfterWait()
    {
        WriteCredentials();
        var limited = false;
        var handler = new FakeHttpHandler((_, _) =>
        {
            if (!limited)
            {
                return Usage();
            }

            var response = FakeHttpHandler.Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(2));
            return response;
        });
        var provider = CreateProvider(handler);
        await provider.RefreshAsync(CancellationToken.None);

        limited = true;
        AdvanceWithACurrentSavedResponse(ClaudeProvider.ResetDataMaxAge);
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, UsageRequests(handler));

        // The reset data is still old, but the server asked Lidget to wait for 2 hours.
        AdvanceWithACurrentSavedResponse(TimeSpan.FromMinutes(119));
        var waiting = await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, UsageRequests(handler));
        Assert.Equal(3, waiting.Resets!.AvailableCount);

        limited = false;
        AdvanceWithACurrentSavedResponse(TimeSpan.FromMinutes(2));
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(3, UsageRequests(handler));
    }

    [Fact]
    public async Task TheResetRuleSendsOneRequestIn30MinutesAtMost()
    {
        WriteCredentials();
        var fail = false;
        var handler = new FakeHttpHandler((_, _) => fail ? FakeHttpHandler.Json(HttpStatusCode.InternalServerError, "{}") : Usage());
        var provider = CreateProvider(handler);
        await provider.RefreshAsync(CancellationToken.None);

        fail = true;
        AdvanceWithACurrentSavedResponse(ClaudeProvider.ResetDataMaxAge);
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, UsageRequests(handler));

        // The request failed, so the reset data is still old. The saved response keeps the limits current.
        for (var i = 0; i < 5; i++)
        {
            AdvanceWithACurrentSavedResponse(TimeSpan.FromMinutes(5));
            await provider.RefreshAsync(CancellationToken.None);
        }

        Assert.Equal(2, UsageRequests(handler));

        AdvanceWithACurrentSavedResponse(TimeSpan.FromMinutes(5));
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(3, UsageRequests(handler));
    }

    [Fact]
    public async Task ARestartKeepsTheResetsAndTheirAge()
    {
        WriteCredentials();
        var handler = UsageHandler();
        await CreateProvider(handler).RefreshAsync(CancellationToken.None);

        AdvanceWithACurrentSavedResponse(TimeSpan.FromHours(1));
        var restarted = CreateProvider(handler);
        var snapshot = await restarted.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, UsageRequests(handler));
        Assert.Equal(3, snapshot.Resets!.AvailableCount);
        Assert.Equal(Now, snapshot.Resets.ObservedAt);
        Assert.Equal(["grant_a1", "grant_b2", "grant_paused", "grant_later"], snapshot.Resets.Resets!.Select(r => r.Id));
        Assert.True(snapshot.Resets.Resets![0].CanRedeemNow);
        Assert.False(snapshot.Resets.Resets[2].IsUsable);

        AdvanceWithACurrentSavedResponse(TimeSpan.FromHours(5));
        await restarted.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, UsageRequests(handler));
    }

    [Fact]
    public async Task TheRedemptionRequestAgreesWithTheT3CodeRequest()
    {
        WriteCredentials();
        WriteConfig();
        var handler = ResetHandler(() => FakeHttpHandler.Json(HttpStatusCode.OK, """{"result":"reset"}"""));

        var outcome = await CreateProvider(handler).RedeemAsync("grant_a1", "3f6c1f0e-7d0b-4c59-9d0e-2b1a6c1f0e7d", CancellationToken.None);

        Assert.Equal(RedeemOutcome.Reset, outcome);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.anthropic.com/api/organizations/org-1/reset_rate_limits", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer access-1", request.Headers.Authorization!.ToString());
        Assert.Equal("oauth-2025-04-20", Assert.Single(request.Headers.GetValues("anthropic-beta")));
        Assert.Equal("claude-cli/2.1.300 (external, cli)", request.Headers.UserAgent.ToString());
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);

        var fields = JsonNode.Parse(body!)!.AsObject();
        Assert.Equal(["grant_id", "program", "request_id"], fields.Select(f => f.Key).Order());
        Assert.Equal("cedar_ember", fields["program"]!.GetValue<string>());
        Assert.Equal("grant_a1", fields["grant_id"]!.GetValue<string>());
        Assert.Equal("3f6c1f0e-7d0b-4c59-9d0e-2b1a6c1f0e7d", fields["request_id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("reset", RedeemOutcome.Reset)]
    [InlineData("already_used", RedeemOutcome.AlreadyRedeemed)]
    [InlineData("not_limited", RedeemOutcome.NothingToReset)]
    [InlineData("ineligible", RedeemOutcome.NoReset)]
    [InlineData("cooldown", RedeemOutcome.CoolingDown)]
    [InlineData("unavailable", RedeemOutcome.Unconfirmed)]
    [InlineData("something_new", RedeemOutcome.Failed)]
    public async Task MapsEachResult(string result, RedeemOutcome expected)
    {
        WriteCredentials();
        WriteConfig();
        var handler = ResetHandler(() => FakeHttpHandler.Json(HttpStatusCode.OK, $$"""{"result":"{{result}}"}"""));

        Assert.Equal(expected, await CreateProvider(handler).RedeemAsync("grant_a1", "key-1", CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, RedeemOutcome.RateLimited)]
    [InlineData(HttpStatusCode.Forbidden, RedeemOutcome.NeedsLogin)]
    [InlineData(HttpStatusCode.InternalServerError, RedeemOutcome.Failed)]
    [InlineData(HttpStatusCode.NotFound, RedeemOutcome.Failed)]
    public async Task MapsEachFailedStatus(HttpStatusCode status, RedeemOutcome expected)
    {
        WriteCredentials();
        WriteConfig();
        var handler = ResetHandler(() => FakeHttpHandler.Json(status, """{"result":"reset"}"""));

        Assert.Equal(expected, await CreateProvider(handler).RedeemAsync("grant_a1", "key-1", CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AResponseThatIsNotJsonIsNotFinal()
    {
        WriteCredentials();
        WriteConfig();
        var handler = ResetHandler(() => FakeHttpHandler.Json(HttpStatusCode.OK, "<html>"));

        Assert.Equal(RedeemOutcome.Failed, await CreateProvider(handler).RedeemAsync("grant_a1", "key-1", CancellationToken.None));
    }

    [Fact]
    public async Task ARefusedLoginRefreshesOneTimeAndSendsTheSameRequestId()
    {
        WriteCredentials();
        WriteConfig();
        var resetCalls = 0;
        var handler = new FakeHttpHandler((request, _) =>
        {
            if (!IsReset(request))
            {
                return FakeHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"access-2","refresh_token":"refresh-2","expires_in":3600}""");
            }

            return ++resetCalls == 1
                ? FakeHttpHandler.Json(HttpStatusCode.Unauthorized, "{}")
                : FakeHttpHandler.Json(HttpStatusCode.OK, """{"result":"reset"}""");
        });

        var outcome = await CreateProvider(handler).RedeemAsync("grant_a1", "key-1", CancellationToken.None);

        Assert.Equal(RedeemOutcome.Reset, outcome);
        var resets = handler.Requests.Where(r => IsReset(r.Request)).ToArray();
        Assert.Equal(2, resets.Length);
        Assert.Equal("Bearer access-2", resets[1].Request.Headers.Authorization!.ToString());
        Assert.Equal(resets[0].Body, resets[1].Body);
    }

    [Fact]
    public async Task ASecondRefusalNeedsLogin()
    {
        WriteCredentials();
        WriteConfig();
        var handler = new FakeHttpHandler((request, _) => IsReset(request)
            ? FakeHttpHandler.Json(HttpStatusCode.Unauthorized, "{}")
            : FakeHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"access-2","expires_in":3600}"""));

        Assert.Equal(RedeemOutcome.NeedsLogin, await CreateProvider(handler).RedeemAsync("grant_a1", "key-1", CancellationToken.None));
    }

    [Fact]
    public async Task ATimeoutIsNotConfirmed()
    {
        WriteCredentials();
        WriteConfig();
        var handler = new FakeHttpHandler((_, _) => throw new TaskCanceledException("The request timed out."));

        Assert.Equal(RedeemOutcome.Unconfirmed, await CreateProvider(handler).RedeemAsync("grant_a1", "key-1", CancellationToken.None));
    }

    [Fact]
    public async Task ANetworkErrorFails()
    {
        WriteCredentials();
        WriteConfig();
        var handler = new FakeHttpHandler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError));

        Assert.Equal(RedeemOutcome.Failed, await CreateProvider(handler).RedeemAsync("grant_a1", "key-1", CancellationToken.None));
    }

    [Fact]
    public async Task WithoutALoginOrAnOrganizationNoRequestIsSent()
    {
        var handler = new FakeHttpHandler((_, _) => throw new InvalidOperationException("No request expected."));
        WriteConfig();

        // No credentials file.
        Assert.Equal(RedeemOutcome.NeedsLogin, await CreateProvider(handler).RedeemAsync("grant_a1", "key-1", CancellationToken.None));

        WriteCredentials();
        WriteConfig(organization: null);
        Assert.Equal(RedeemOutcome.NeedsLogin, await CreateProvider(handler).RedeemAsync("grant_a1", "key-1", CancellationToken.None));

        File.Delete(ConfigPath);
        Assert.Equal(RedeemOutcome.NeedsLogin, await CreateProvider(handler).RedeemAsync("grant_a1", "key-1", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("Not A Grant ID", "key-1")]
    [InlineData("grant/../../x", "key-1")]
    [InlineData("grant_a1", "key with spaces")]
    [InlineData("grant_a1", "")]
    public async Task AnIdWithAnotherFormIsNotSent(string grantId, string requestId)
    {
        WriteCredentials();
        WriteConfig();
        var handler = new FakeHttpHandler((_, _) => throw new InvalidOperationException("No request expected."));

        Assert.Equal(RedeemOutcome.Failed, await CreateProvider(handler).RedeemAsync(grantId, requestId, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ARedemptionShowsTheNewCountAndAsksForOneUsageRequest()
    {
        WriteCredentials();
        WriteConfig();
        var handler = ResetHandler(() => FakeHttpHandler.Json(HttpStatusCode.OK, """{"result":"reset"}"""));
        var provider = CreateProvider(handler);
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, UsageRequests(handler));

        _time.Advance(TimeSpan.FromMinutes(1));
        await provider.RedeemAsync("grant_a1", "key-1", CancellationToken.None);
        var saved = JsonNode.Parse(File.ReadAllText(_dir.File(ClaudeProvider.ResetsFileName)))!;
        Assert.Equal(2, saved["summary"]!["availableCount"]!.GetValue<int>());

        // The limits and the resets changed, so the next refresh requests the endpoint, although its data is new.
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, UsageRequests(handler));

        _time.Advance(TimeSpan.FromMinutes(1));
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, UsageRequests(handler));
    }

    [Fact]
    public async Task TheLogHasNoIdAndNoToken()
    {
        WriteCredentials();
        WriteConfig();
        var handler = ResetHandler(() => FakeHttpHandler.Json(HttpStatusCode.InternalServerError, "{}"));
        var provider = CreateProvider(handler);
        await provider.RefreshAsync(CancellationToken.None);

        await provider.RedeemAsync("grant_a1", "key-1", CancellationToken.None);

        Assert.Contains(_log.Messages, m => m.Contains("Claude reset request failed (HTTP 500)", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("grant_a1", StringComparison.Ordinal)
            || m.Contains("org-1", StringComparison.Ordinal)
            || m.Contains("access-1", StringComparison.Ordinal)
            || m.Contains("key-1", StringComparison.Ordinal));
    }
}
