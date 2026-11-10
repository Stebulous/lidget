using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lidget.Providers.Claude;

/// <summary>
/// Reads single values from the Claude Code configuration file (<c>~/.claude.json</c>). Lidget only reads the file.
/// </summary>
public static partial class ClaudeAccountConfig
{
    /// <summary>
    /// The Claude Code version for the <c>User-Agent</c> header when the configuration file has no version.
    /// It is the version of the 2026-11-10 check in the plan evidence log.
    /// </summary>
    public const string FallbackClientVersion = "2.1.322";

    /// <summary>A configuration file larger than this is not read.</summary>
    private const long MaxFileBytes = 64 * 1024 * 1024;

    /// <summary>The organization of the signed-in account (<c>oauthAccount.organizationUuid</c>), or null.</summary>
    public static string? ReadOrganizationId(string path) => Read(path, root =>
        root.TryGetProperty("oauthAccount", out var account) && account.ValueKind == JsonValueKind.Object
        && account.TryGetProperty("organizationUuid", out var id) && id.ValueKind == JsonValueKind.String
        && id.GetString()!.Trim() is { Length: > 0 } organization
            ? organization
            : null);

    /// <summary>
    /// The Claude Code version, from <c>lastReleaseNotesSeen</c>, which Claude Code updates after an update.
    /// Returns <see cref="FallbackClientVersion"/> if the file has no valid version.
    /// </summary>
    public static string ReadClientVersion(string path) => Read(path, root =>
        root.TryGetProperty("lastReleaseNotesSeen", out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } version && Version().IsMatch(version)
            ? version
            : null) ?? FallbackClientVersion;

    private static string? Read(string path, Func<JsonElement, string?> select)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes)
            {
                return null;
            }

            // Claude Code replaces the file while it runs, so allow other processes to write and delete it.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object ? select(document.RootElement) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,6}$")]
    private static partial Regex Version();
}
