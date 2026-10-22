namespace Lidget.Core.StatusLine;

/// <summary>
/// Files shared by the app and <c>lidget-statusline.exe</c>. The wrapper links this file, so it must
/// stay free of other dependencies.
/// </summary>
public static class StatusLineFiles
{
    public const string WrapperFileName = "lidget-statusline.exe";

    /// <summary>The latest <c>rate_limits</c> object from Claude Code, with the time it arrived.</summary>
    public static string FeedPath(string dataDirectory) => Path.Combine(dataDirectory, "claude-statusline.json");

    /// <summary>The <c>statusLine</c> setting that the wrapper replaced, and that it runs after it records the feed.</summary>
    public static string OriginalPath(string dataDirectory) => Path.Combine(dataDirectory, "statusline-original.json");
}
