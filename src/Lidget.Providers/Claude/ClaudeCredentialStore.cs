using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lidget.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidget.Providers.Claude;

public sealed record ClaudeOAuth(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string? SubscriptionType,
    string? RateLimitTier);

public enum ClaudeCredentialProblem
{
    /// <summary>No credentials file exists.</summary>
    NotFound,

    /// <summary>The file has no Claude.ai OAuth login.</summary>
    NotSignedIn,

    /// <summary>The login expired and cannot be refreshed. The user must sign in to Claude Code again.</summary>
    SessionExpired,

    /// <summary>The token expired, and the refresh request failed for a temporary reason.</summary>
    RefreshUnavailable,
}

public sealed record ClaudeCredentialResult(ClaudeOAuth? OAuth, ClaudeCredentialProblem? Problem)
{
    public static ClaudeCredentialResult Ok(ClaudeOAuth oauth) => new(oauth, null);

    public static ClaudeCredentialResult Fail(ClaudeCredentialProblem problem) => new(null, problem);
}

/// <summary>
/// Reads the Claude Code OAuth login and refreshes it when it expires. Refresh tokens are single-use,
/// so a successful refresh is always written back to the Claude Code credentials file. If Claude Code
/// changes the file during a refresh, the file wins.
/// </summary>
public sealed class ClaudeCredentialStore
{
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> KnownOAuthErrors =
    [
        "invalid_request", "invalid_client", "invalid_grant", "unauthorized_client", "unsupported_grant_type",
        "invalid_scope", "refresh_token_reused", "refresh_token_expired", "refresh_token_invalidated",
    ];

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClaudeCredentialStore(HttpClient http, TimeProvider time, ILogger<ClaudeCredentialStore>? logger = null, string? claudeHome = null)
    {
        _http = http;
        _time = time;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        CredentialsPath = Path.Combine(claudeHome ?? ResolveClaudeHome(), ".credentials.json");
    }

    public string CredentialsPath { get; }

    /// <summary>The Claude Code configuration directory: <c>CLAUDE_CONFIG_DIR</c>, or <c>~/.claude</c>.</summary>
    public static string ResolveClaudeHome() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public async Task<ClaudeCredentialResult> GetCredentialsAsync(bool forceRefresh, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = ReadFile();
            if (file is null)
            {
                return ClaudeCredentialResult.Fail(ClaudeCredentialProblem.NotFound);
            }

            if (file.OAuth is null)
            {
                return ClaudeCredentialResult.Fail(ClaudeCredentialProblem.NotSignedIn);
            }

            if (!forceRefresh && !NeedsRefresh(file.OAuth))
            {
                return ClaudeCredentialResult.Ok(file.OAuth);
            }

            return await RefreshAsync(file, retryWithNewerToken: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool NeedsRefresh(ClaudeOAuth oauth) =>
        oauth.ExpiresAt is { } expiresAt && expiresAt - _time.GetUtcNow() <= RefreshMargin;

    private bool IsExpired(ClaudeOAuth oauth) =>
        oauth.ExpiresAt is { } expiresAt && expiresAt <= _time.GetUtcNow();

    private async Task<ClaudeCredentialResult> RefreshAsync(CredentialFile before, bool retryWithNewerToken, CancellationToken ct)
    {
        var oauth = before.OAuth!;
        if (string.IsNullOrEmpty(oauth.RefreshToken))
        {
            return ClaudeCredentialResult.Fail(ClaudeCredentialProblem.SessionExpired);
        }

        var outcome = await SendRefreshAsync(oauth.RefreshToken, ct).ConfigureAwait(false);

        var current = ReadFile();
        if (current?.OAuth is null)
        {
            _logger.LogWarning("Claude credentials disappeared during a token refresh");
            return ClaudeCredentialResult.Fail(current is null ? ClaudeCredentialProblem.NotFound : ClaudeCredentialProblem.NotSignedIn);
        }

        var changedDuringRefresh = !current.Hash.AsSpan().SequenceEqual(before.Hash);

        switch (outcome.Kind)
        {
            case RefreshOutcomeKind.Success when changedDuringRefresh:
                // Claude Code wrote new credentials while our request ran. Keep its tokens.
                _logger.LogInformation("Claude credentials changed during a token refresh; using the file");
                return IsExpired(current.OAuth)
                    ? ClaudeCredentialResult.Fail(ClaudeCredentialProblem.SessionExpired)
                    : ClaudeCredentialResult.Ok(current.OAuth);

            case RefreshOutcomeKind.Success:
                var refreshed = oauth with
                {
                    AccessToken = outcome.AccessToken!,
                    RefreshToken = outcome.RefreshToken ?? oauth.RefreshToken,
                    ExpiresAt = outcome.ExpiresIn is { } seconds ? _time.GetUtcNow().AddSeconds(seconds) : oauth.ExpiresAt,
                };
                WriteBack(current, refreshed);
                _logger.LogInformation("Refreshed the Claude access token");
                return ClaudeCredentialResult.Ok(refreshed);

            case RefreshOutcomeKind.Rejected:
                _logger.LogWarning("Claude token refresh was rejected ({Error})", outcome.Error);
                if (changedDuringRefresh && !NeedsRefresh(current.OAuth))
                {
                    return ClaudeCredentialResult.Ok(current.OAuth);
                }

                if (retryWithNewerToken && changedDuringRefresh && current.OAuth.RefreshToken != oauth.RefreshToken)
                {
                    return await RefreshAsync(current, retryWithNewerToken: false, ct).ConfigureAwait(false);
                }

                return ClaudeCredentialResult.Fail(ClaudeCredentialProblem.SessionExpired);

            default:
                _logger.LogWarning("Claude token refresh failed temporarily ({Error})", outcome.Error);
                return IsExpired(current.OAuth)
                    ? ClaudeCredentialResult.Fail(ClaudeCredentialProblem.RefreshUnavailable)
                    : ClaudeCredentialResult.Ok(current.OAuth);
        }
    }

    private async Task<RefreshOutcome> SendRefreshAsync(string refreshToken, CancellationToken ct)
    {
        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClaudeEndpoints.ClientId,
            ["scope"] = ClaudeEndpoints.Scopes,
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(ClaudeEndpoints.TokenRefresh, body, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new RefreshOutcome(RefreshOutcomeKind.Transient, Error: ex.HttpRequestError.ToString());
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RefreshOutcome(RefreshOutcomeKind.Transient, Error: "timeout");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonObject? json = null;
            try
            {
                json = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
            }

            if (response.IsSuccessStatusCode)
            {
                var accessToken = json?["access_token"]?.GetValue<string>();
                if (string.IsNullOrEmpty(accessToken))
                {
                    return new RefreshOutcome(RefreshOutcomeKind.Transient, Error: "response without access_token");
                }

                return new RefreshOutcome(
                    RefreshOutcomeKind.Success,
                    AccessToken: accessToken,
                    RefreshToken: json?["refresh_token"]?.GetValue<string>(),
                    ExpiresIn: json?["expires_in"]?.GetValue<double>());
            }

            // Log only known OAuth error codes: an unknown value can echo request content.
            var code = json?["error"] is JsonValue value && value.TryGetValue<string>(out var errorCode) ? errorCode : null;
            var error = $"HTTP {(int)response.StatusCode}" + (code is not null && KnownOAuthErrors.Contains(code) ? $" {code}" : string.Empty);
            var rejected = response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            return new RefreshOutcome(rejected ? RefreshOutcomeKind.Rejected : RefreshOutcomeKind.Transient, Error: error);
        }
    }

    private void WriteBack(CredentialFile current, ClaudeOAuth refreshed)
    {
        // Change only the three refreshed fields. Claude Code owns every other field.
        var document = (JsonObject)current.Document.DeepClone();
        var oauth = (JsonObject)document["claudeAiOauth"]!;
        oauth["accessToken"] = refreshed.AccessToken;
        oauth["refreshToken"] = refreshed.RefreshToken;
        if (refreshed.ExpiresAt is { } expiresAt)
        {
            oauth["expiresAt"] = expiresAt.ToUnixTimeMilliseconds();
        }

        var indented = current.Bytes.AsSpan().IndexOf((byte)'\n') >= 0;
        AtomicFile.WriteAllText(CredentialsPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = indented }));
    }

    private CredentialFile? ReadFile()
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(CredentialsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        JsonObject? document;
        try
        {
            document = JsonNode.Parse(bytes) as JsonObject;
        }
        catch (JsonException)
        {
            _logger.LogWarning("The Claude credentials file is not valid JSON");
            return null;
        }

        if (document is null)
        {
            return null;
        }

        return new CredentialFile(bytes, SHA256.HashData(bytes), document, ParseOAuth(document["claudeAiOauth"] as JsonObject));
    }

    private static ClaudeOAuth? ParseOAuth(JsonObject? node)
    {
        var accessToken = ReadString(node, "accessToken");
        if (node is null || string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        DateTimeOffset? expiresAt = node["expiresAt"] is JsonValue value && value.TryGetValue<double>(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms)
            : null;

        return new ClaudeOAuth(
            accessToken.Trim(),
            ReadString(node, "refreshToken"),
            expiresAt,
            ReadString(node, "subscriptionType"),
            ReadString(node, "rateLimitTier"));
    }

    private static string? ReadString(JsonObject? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var errorCode) ? errorCode : null;

    private sealed record CredentialFile(byte[] Bytes, byte[] Hash, JsonObject Document, ClaudeOAuth? OAuth);

    private enum RefreshOutcomeKind
    {
        Success,
        Rejected,
        Transient,
    }

    private sealed record RefreshOutcome(
        RefreshOutcomeKind Kind,
        string? AccessToken = null,
        string? RefreshToken = null,
        double? ExpiresIn = null,
        string? Error = null);
}
