namespace Lidget.Providers.Claude;

/// <summary>
/// Undocumented Claude Code OAuth endpoints. Values come from OpenUsage
/// (Sources/OpenUsage/Providers/Claude) and the T0.2 spike in docs/plans/lidget-plan.md.
/// The reset values come from T3 Code (apps/server/src/provider/claudeResetCredits.ts).
/// </summary>
internal static class ClaudeEndpoints
{
    /// <summary>
    /// The usage request. The <c>cedar_ember</c> parameter adds the banked resets of the account to the
    /// response, so that Lidget needs no separate request for them (plan decision D58).
    /// </summary>
    public static readonly Uri Usage = new("https://api.anthropic.com/api/oauth/usage?cedar_ember=1");

    /// <summary>The request that redeems a banked reset for an organization (plan decision D61).</summary>
    public static Uri ResetRateLimits(string organizationId) =>
        new($"https://api.anthropic.com/api/organizations/{Uri.EscapeDataString(organizationId)}/reset_rate_limits");
    public static readonly Uri TokenRefresh = new("https://platform.claude.com/v1/oauth/token");

    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public const string Scopes = "user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";
    public const string BetaHeader = "oauth-2025-04-20";
}
