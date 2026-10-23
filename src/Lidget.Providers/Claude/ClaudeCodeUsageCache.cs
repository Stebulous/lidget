using System.Text.Json;
using Lidget.Core.Models;

namespace Lidget.Providers.Claude;

/// <summary>A usage response that Claude Code saved, and the time that Claude Code received it.</summary>
public sealed record ClaudeCachedUsage(IReadOnlyList<LimitWindow> Limits, DateTimeOffset FetchedAt);

/// <summary>
/// Reads the usage response that Claude Code saves in its global configuration file, as
/// <c>cachedUsageUtilization</c> in <c>~/.claude.json</c>. Each Claude Code process saves its successful usage
/// requests there, at most one time each minute, and uses the copy for up to 1 hour. This includes the CLI that
/// T3 Code starts to check the limits. The copy lets Lidget show current limits without a request of its own.
/// The key is undocumented: if it is missing or has a different shape, Lidget gets no reading.
/// </summary>
public sealed class ClaudeCodeUsageCache
{
    public const string SourceName = "claude-code";

    /// <summary>Claude Code does not use a copy that is older than this. Lidget uses the same limit.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    /// <summary>A configuration file larger than this is not read.</summary>
    internal const long MaxFileBytes = 64 * 1024 * 1024;

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private DateTime _lastWriteTime;
    private long _lastLength = -1;
    private ClaudeCachedUsage? _parsed;

    public ClaudeCodeUsageCache(string path, TimeProvider time)
    {
        _path = path;
        _time = time;
    }

    /// <summary>
    /// The Claude Code global configuration file: <c>.claude.json</c> in <c>CLAUDE_CONFIG_DIR</c>, or
    /// <c>~/.claude.json</c>.
    /// </summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? Path.Combine(dir, ".claude.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    /// <summary>
    /// Returns the saved response, if it is less than <see cref="MaxAge"/> old. Windows whose reset time has
    /// passed are left out. The file is parsed again only when it changes.
    /// </summary>
    public ClaudeCachedUsage? Read()
    {
        var now = _time.GetUtcNow();
        ClaudeCachedUsage? usage;
        lock (_gate)
        {
            usage = Load();
        }

        if (usage is null || now - usage.FetchedAt > MaxAge || usage.FetchedAt - now > TimeSpan.FromMinutes(5))
        {
            return null;
        }

        var limits = usage.Limits.Where(l => l.ResetsAt is not { } reset || reset > now).ToArray();
        return limits.Length == 0 ? null : usage with { Limits = limits };
    }

    private ClaudeCachedUsage? Load()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length > MaxFileBytes)
        {
            _lastLength = -1;
            _parsed = null;
            return null;
        }

        if (info.LastWriteTimeUtc == _lastWriteTime && info.Length == _lastLength)
        {
            return _parsed;
        }

        try
        {
            // Claude Code replaces the file while it runs, so allow other processes to write and delete it.
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            _parsed = Parse(document.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A partial or locked file keeps the last result. The next read tries again.
            return _parsed;
        }

        _lastWriteTime = info.LastWriteTimeUtc;
        _lastLength = info.Length;
        return _parsed;
    }

    /// <summary>
    /// Maps <c>cachedUsageUtilization</c>. The copy belongs to the account that saved it: like Claude Code,
    /// Lidget ignores it when <c>oauthAccount.accountUuid</c> is different, for example after a sign-out.
    /// </summary>
    internal static ClaudeCachedUsage? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("cachedUsageUtilization", out var cache) || cache.ValueKind != JsonValueKind.Object
            || !cache.TryGetProperty("fetchedAtMs", out var fetched) || fetched.ValueKind != JsonValueKind.Number
            || !fetched.TryGetInt64(out var fetchedAtMs)
            || !cache.TryGetProperty("utilization", out var utilization) || utilization.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (AccountId(cache) != AccountId(root.TryGetProperty("oauthAccount", out var account) ? account : default))
        {
            return null;
        }

        var fetchedAt = DateTimeOffset.FromUnixTimeMilliseconds(fetchedAtMs);
        var limits = ClaudeUsageMapper.Map(utilization, fetchedAt, SourceName);
        return limits.Count == 0 ? null : new ClaudeCachedUsage(limits, fetchedAt);
    }

    private static string? AccountId(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("accountUuid", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
}
