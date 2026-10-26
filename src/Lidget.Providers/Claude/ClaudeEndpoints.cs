namespace Lidget.Providers.Claude;

/// <summary>
/// Undocumented Claude Code OAuth endpoints. Values come from OpenUsage
/// (Sources/OpenUsage/Providers/Claude) and the T0.2 spike in docs/plans/lidget-plan.md.
/// </summary>
internal static class ClaudeEndpoints
{
    public static readonly Uri Usage = new("https://api.anthropic.com/api/oauth/usage");
    public static readonly Uri TokenRefresh = new("https://platform.claude.com/v1/oauth/token");

    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public const string Scopes = "user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";
    public const string BetaHeader = "oauth-2025-04-20";
}
