using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lidget.Core.Models;

namespace Lidget.Providers.Claude;

/// <summary>The HTTP status of a redemption request, and the <c>result</c> value of a successful response.</summary>
public sealed record ClaudeResetResponse(HttpStatusCode Status, string? Result);

/// <summary>
/// Redeems a Claude banked reset (plan decision D61). The request is a port of <c>consumeClaudeResetCredit</c>
/// in T3 Code (MIT): the same URL, the same authorization, <c>anthropic-beta</c>, and <c>User-Agent</c>
/// headers, and the same body fields (plan decision D67).
/// </summary>
public sealed partial class ClaudeResetClient(HttpClient http, string accountConfigPath)
{
    /// <summary>The time limit of one request. T3 Code uses the same value.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(25);

    /// <summary>Whether <paramref name="id"/> has the form that the server accepts for a request ID.</summary>
    public static bool IsRequestId(string? id) => id is not null && RequestId().IsMatch(id);

    /// <summary>
    /// Reads the organization of the signed-in account from the Claude Code configuration file
    /// (<c>oauthAccount.organizationUuid</c> in <c>~/.claude.json</c>), or returns null.
    /// </summary>
    public string? ReadOrganizationId() => ClaudeAccountConfig.ReadOrganizationId(accountConfigPath);

    /// <summary>Sends one redemption request. <paramref name="requestId"/> is the idempotency key.</summary>
    public async Task<ClaudeResetResponse> SendAsync(string accessToken, string organizationId, string grantId, string requestId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, ClaudeEndpoints.ResetRateLimits(organizationId));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("anthropic-beta", ClaudeEndpoints.BetaHeader);
        request.Headers.TryAddWithoutValidation("User-Agent", ClaudeEndpoints.UserAgent(ClaudeAccountConfig.ReadClientVersion(accountConfigPath)));
        request.Content = JsonContent.Create(new Dictionary<string, string>
        {
            ["program"] = ClaudeResetMapper.Program,
            ["grant_id"] = grantId,
            ["request_id"] = requestId,
        });

        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new ClaudeResetResponse(response.StatusCode, null);
        }

        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            var result = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("result", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            return new ClaudeResetResponse(response.StatusCode, result);
        }
        catch (JsonException)
        {
            return new ClaudeResetResponse(response.StatusCode, null);
        }
    }

    /// <summary>Maps a response to an outcome. An unknown result is not final, so the next try keeps the request ID.</summary>
    public static RedeemOutcome Outcome(ClaudeResetResponse response) => response switch
    {
        { Status: HttpStatusCode.TooManyRequests } => RedeemOutcome.RateLimited,
        { Status: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => RedeemOutcome.NeedsLogin,
        { Status: < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices } => RedeemOutcome.Failed,
        { Result: "reset" } => RedeemOutcome.Reset,
        { Result: "already_used" } => RedeemOutcome.AlreadyRedeemed,
        { Result: "not_limited" } => RedeemOutcome.NothingToReset,
        { Result: "ineligible" } => RedeemOutcome.NoReset,
        { Result: "cooldown" } => RedeemOutcome.CoolingDown,
        { Result: "unavailable" } => RedeemOutcome.Unconfirmed,
        _ => RedeemOutcome.Failed,
    };

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex RequestId();
}
