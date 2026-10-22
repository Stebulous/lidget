using System.Net;
using System.Text.Json.Nodes;
using Lidget.Providers.Claude;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Lidget.Tests.Providers;

public class ClaudeCredentialStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(Now);

    private string CredentialsPath => _dir.File(".credentials.json");

    public void Dispose() => _dir.Dispose();

    private void WriteCredentials(string accessToken, string refreshToken, DateTimeOffset expiresAt)
    {
        var document = new JsonObject
        {
            ["mcpOAuth"] = new JsonObject { ["server"] = new JsonObject { ["token"] = "mcp-secret" } },
            ["claudeAiOauth"] = new JsonObject
            {
                ["accessToken"] = accessToken,
                ["refreshToken"] = refreshToken,
                ["expiresAt"] = expiresAt.ToUnixTimeMilliseconds(),
                ["scopes"] = new JsonArray("user:profile"),
                ["subscriptionType"] = "pro",
                ["rateLimitTier"] = "default_claude_ai",
                ["futureField"] = 42,
            },
        };
        File.WriteAllText(CredentialsPath, document.ToJsonString());
    }

    private ClaudeCredentialStore CreateStore(FakeHttpHandler handler, ILogger<ClaudeCredentialStore>? logger = null) =>
        new(new HttpClient(handler), _time, logger, _dir.Path);

    private static HttpResponseMessage RefreshSuccess(string access = "access-new", string refresh = "refresh-new") =>
        FakeHttpHandler.Json(HttpStatusCode.OK, $$"""{"access_token":"{{access}}","refresh_token":"{{refresh}}","expires_in":28800}""");

    [Fact]
    public async Task ValidTokenIsUsedWithoutRefresh()
    {
        WriteCredentials("access-1", "refresh-1", Now.AddHours(2));
        var handler = new FakeHttpHandler((_, _) => throw new InvalidOperationException("no request expected"));

        var result = await CreateStore(handler).GetCredentialsAsync(forceRefresh: false, CancellationToken.None);

        Assert.Null(result.Problem);
        Assert.Equal("access-1", result.OAuth!.AccessToken);
        Assert.Equal("pro", result.OAuth.SubscriptionType);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MissingFileIsNotFound()
    {
        var handler = new FakeHttpHandler((_, _) => throw new InvalidOperationException());

        var result = await CreateStore(handler).GetCredentialsAsync(false, CancellationToken.None);

        Assert.Equal(ClaudeCredentialProblem.NotFound, result.Problem);
    }

    [Fact]
    public async Task FileWithoutOAuthIsNotSignedIn()
    {
        File.WriteAllText(CredentialsPath, """{"mcpOAuth":{}}""");
        var handler = new FakeHttpHandler((_, _) => throw new InvalidOperationException());

        var result = await CreateStore(handler).GetCredentialsAsync(false, CancellationToken.None);

        Assert.Equal(ClaudeCredentialProblem.NotSignedIn, result.Problem);
    }

    [Fact]
    public async Task ExpiringTokenIsRefreshedAndWrittenBack()
    {
        WriteCredentials("access-1", "refresh-1", Now.AddMinutes(4));
        var handler = new FakeHttpHandler((_, _) => RefreshSuccess());

        var result = await CreateStore(handler).GetCredentialsAsync(false, CancellationToken.None);

        Assert.Equal("access-new", result.OAuth!.AccessToken);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://platform.claude.com/v1/oauth/token", request.Request.RequestUri!.ToString());
        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal("refresh_token", body["grant_type"]!.GetValue<string>());
        Assert.Equal("refresh-1", body["refresh_token"]!.GetValue<string>());

        var written = JsonNode.Parse(File.ReadAllText(CredentialsPath))!;
        var oauth = written["claudeAiOauth"]!;
        Assert.Equal("access-new", oauth["accessToken"]!.GetValue<string>());
        Assert.Equal("refresh-new", oauth["refreshToken"]!.GetValue<string>());
        Assert.Equal(Now.AddSeconds(28800).ToUnixTimeMilliseconds(), oauth["expiresAt"]!.GetValue<long>());
    }

    [Fact]
    public async Task WriteBackKeepsUnknownFieldsAndCompactFormat()
    {
        WriteCredentials("access-1", "refresh-1", Now.AddMinutes(1));
        var handler = new FakeHttpHandler((_, _) => RefreshSuccess());

        await CreateStore(handler).GetCredentialsAsync(false, CancellationToken.None);

        var text = File.ReadAllText(CredentialsPath);
        Assert.DoesNotContain('\n', text);
        var written = JsonNode.Parse(text)!;
        Assert.Equal("mcp-secret", written["mcpOAuth"]!["server"]!["token"]!.GetValue<string>());
        Assert.Equal(42, written["claudeAiOauth"]!["futureField"]!.GetValue<int>());
        Assert.Equal("default_claude_ai", written["claudeAiOauth"]!["rateLimitTier"]!.GetValue<string>());
        Assert.Single(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task FileChangedDuringRefreshWins()
    {
        WriteCredentials("access-1", "refresh-1", Now.AddMinutes(1));
        var handler = new FakeHttpHandler((_, _) =>
        {
            // Claude Code refreshes at the same time and writes its own tokens.
            WriteCredentials("access-cli", "refresh-cli", Now.AddHours(8));
            return RefreshSuccess();
        });

        var result = await CreateStore(handler).GetCredentialsAsync(false, CancellationToken.None);

        Assert.Equal("access-cli", result.OAuth!.AccessToken);
        Assert.Equal("refresh-cli", JsonNode.Parse(File.ReadAllText(CredentialsPath))!["claudeAiOauth"]!["refreshToken"]!.GetValue<string>());
    }

    [Fact]
    public async Task RejectedRefreshRetriesOnceWithNewerFileToken()
    {
        WriteCredentials("access-1", "refresh-1", Now.AddMinutes(1));
        var calls = 0;
        var handler = new FakeHttpHandler((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                // Claude Code used the same refresh token first, but its new access token is also expiring.
                WriteCredentials("access-cli", "refresh-cli", Now.AddMinutes(2));
                return FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
            }

            return RefreshSuccess("access-retry", "refresh-retry");
        });

        var result = await CreateStore(handler).GetCredentialsAsync(false, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal("access-retry", result.OAuth!.AccessToken);
        Assert.Equal("refresh-cli", JsonNode.Parse(handler.Requests[1].Body!)!["refresh_token"]!.GetValue<string>());
    }

    [Fact]
    public async Task RejectedRefreshWithoutNewerTokenIsSessionExpired()
    {
        WriteCredentials("access-1", "refresh-1", Now.AddMinutes(-1));
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));

        var result = await CreateStore(handler).GetCredentialsAsync(false, CancellationToken.None);

        Assert.Equal(ClaudeCredentialProblem.SessionExpired, result.Problem);
        Assert.Single(handler.Requests);
        Assert.Contains("access-1", File.ReadAllText(CredentialsPath));
    }

    [Fact]
    public async Task TransientFailureKeepsUnexpiredToken()
    {
        WriteCredentials("access-1", "refresh-1", Now.AddMinutes(3));
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.ServiceUnavailable, "{}"));

        var result = await CreateStore(handler).GetCredentialsAsync(false, CancellationToken.None);

        Assert.Equal("access-1", result.OAuth!.AccessToken);
    }

    [Fact]
    public async Task TransientFailureWithExpiredTokenIsRefreshUnavailable()
    {
        WriteCredentials("access-1", "refresh-1", Now.AddMinutes(-3));
        var handler = new FakeHttpHandler((_, _) => throw new HttpRequestException("offline"));

        var result = await CreateStore(handler).GetCredentialsAsync(false, CancellationToken.None);

        Assert.Equal(ClaudeCredentialProblem.RefreshUnavailable, result.Problem);
    }

    [Fact]
    public async Task ForcedRefreshRefreshesValidToken()
    {
        WriteCredentials("access-1", "refresh-1", Now.AddHours(2));
        var handler = new FakeHttpHandler((_, _) => RefreshSuccess());

        var result = await CreateStore(handler).GetCredentialsAsync(forceRefresh: true, CancellationToken.None);

        Assert.Equal("access-new", result.OAuth!.AccessToken);
    }

    [Fact]
    public async Task LogsNeverContainTokens()
    {
        WriteCredentials("access-secret-1", "refresh-secret-1", Now.AddMinutes(-1));
        var logger = new ListLogger<ClaudeCredentialStore>();
        var calls = 0;
        var handler = new FakeHttpHandler((_, _) => ++calls == 1
            ? FakeHttpHandler.Json(HttpStatusCode.ServiceUnavailable, """{"error":"refresh-secret-1"}""")
            : RefreshSuccess("access-secret-2", "refresh-secret-2"));
        var store = CreateStore(handler, logger);

        await store.GetCredentialsAsync(false, CancellationToken.None);
        await store.GetCredentialsAsync(false, CancellationToken.None);

        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, m => Assert.DoesNotContain("secret", m));
    }

    [Fact]
    public void ClaudeConfigDirChangesPath()
    {
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _dir.Path);
            Assert.Equal(_dir.Path, ClaudeCredentialStore.ResolveClaudeHome());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previous);
        }
    }
}
