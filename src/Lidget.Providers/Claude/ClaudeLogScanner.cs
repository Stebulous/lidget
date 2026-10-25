using System.Text.Json;
using Lidget.Core.Costs;
using Lidget.Core.Models;
using Microsoft.Extensions.Logging;

namespace Lidget.Providers.Claude;

/// <summary>One usage record from a Claude Code session log.</summary>
public sealed record ClaudeUsageEntry(
    DateTimeOffset Timestamp,
    string? Model,
    long Input,
    long CacheWrite5m,
    long CacheWrite1h,
    long CacheRead,
    long Output,
    bool Fast,
    bool HasSpeed,
    string? MessageId,
    string? RequestId,
    bool IsSidechain,
    double? CostUsd)
{
    public TokenUsage Tokens => new(Input, CacheWrite5m, CacheWrite1h, CacheRead, Output);
}

/// <summary>
/// Estimates Claude costs from Claude Code session logs (<c>projects/**/*.jsonl</c>).
/// The parsing, validity, and deduplication rules are ported from OpenUsage
/// (<c>ClaudeLogUsageScanner.swift</c>, MIT), which ports them from ccusage.
/// </summary>
public sealed class ClaudeLogScanner : ICostSource
{
    public const string SourceName = "claude";

    private readonly IncrementalLogScanner<ClaudeUsageEntry, ClaudeLogParser.State> _scanner;
    private readonly CostCache _cache;
    private readonly PricingTable _pricing;
    private readonly Func<IReadOnlyList<string>> _roots;
    private readonly TimeZoneInfo _zone;

    public ClaudeLogScanner(CostCache cache, PricingTable pricing, Func<IReadOnlyList<string>>? roots = null, TimeZoneInfo? zone = null, ILogger? logger = null)
    {
        _cache = cache;
        _pricing = pricing;
        _roots = roots ?? DefaultRoots;
        _zone = zone ?? TimeZoneInfo.Local;
        _scanner = new IncrementalLogScanner<ClaudeUsageEntry, ClaudeLogParser.State>(SourceName, cache, new ClaudeLogParser(), logger);
    }

    public string ProviderId => ProviderIds.Claude;

    public CostSummary? Scan(DateTimeOffset now, CancellationToken ct)
    {
        var files = _roots()
            .SelectMany(root => EnumerateLogs(Path.Combine(root, "projects")))
            .Order(StringComparer.Ordinal)
            .ToList();

        // Update also with no files, so that the cache forgets deleted logs.
        _scanner.Update(files, ct);
        if (files.Count == 0)
        {
            return null;
        }

        var since = CostAccumulator.WindowStart(now, _zone);
        _cache.Prune((since - TimeSpan.FromDays(2)).ToUnixTimeMilliseconds());
        return Aggregate(Dedup(_scanner.Load(since)), now, _zone, _pricing);
    }

    /// <summary>
    /// Claude Code config folders that contain <c>projects</c>: each entry of <c>CLAUDE_CONFIG_DIR</c>
    /// (comma-separated), or else <c>%XDG_CONFIG_HOME%\claude</c> and <c>~\.claude</c>.
    /// </summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")?.Trim();
        IEnumerable<string> candidates;
        if (!string.IsNullOrEmpty(configured))
        {
            candidates = configured
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(entry => ExpandHome(entry, home))
                .Select(entry => Path.GetFileName(entry.TrimEnd('\\', '/')) == "projects" ? Path.GetDirectoryName(entry.TrimEnd('\\', '/'))! : entry);
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? ExpandHome(x, home) : Path.Combine(home, ".config");
            candidates = [Path.Combine(xdg, "claude"), Path.Combine(home, ".claude")];
        }

        return candidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(root => Directory.Exists(Path.Combine(root, "projects")))
            .ToList();
    }

    /// <summary>
    /// Drops replayed records. The key is (message ID, request ID). A second index on the message ID
    /// catches sidechain logs that replay a parent message under a new request ID. On a collision,
    /// the non-sidechain record wins, then the larger token total, then the record with a speed field.
    /// </summary>
    public static List<ClaudeUsageEntry> Dedup(IEnumerable<ClaudeUsageEntry> entries)
    {
        var deduped = new List<ClaudeUsageEntry>();
        var exact = new Dictionary<(string MessageId, string? RequestId), int>();
        var byMessage = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry.MessageId is not { } messageId)
            {
                deduped.Add(entry);
                continue;
            }

            var key = (messageId, entry.RequestId);
            int? collision = exact.TryGetValue(key, out var found)
                ? found
                : byMessage.TryGetValue(messageId, out var indexes)
                    ? indexes.Cast<int?>().FirstOrDefault(i => entry.IsSidechain || deduped[i!.Value].IsSidechain)
                    : null;

            if (collision is { } index)
            {
                if (ShouldReplace(entry, deduped[index]))
                {
                    var old = deduped[index];
                    exact.Remove((old.MessageId!, old.RequestId));
                    deduped[index] = entry;
                    exact[key] = index;
                }

                continue;
            }

            exact[key] = deduped.Count;
            if (!byMessage.TryGetValue(messageId, out var list))
            {
                byMessage[messageId] = list = [];
            }

            list.Add(deduped.Count);
            deduped.Add(entry);
        }

        return deduped;
    }

    private static bool ShouldReplace(ClaudeUsageEntry candidate, ClaudeUsageEntry existing)
    {
        if (candidate.IsSidechain != existing.IsSidechain)
        {
            return existing.IsSidechain;
        }

        if (candidate.Tokens.TotalTokens != existing.Tokens.TotalTokens)
        {
            return candidate.Tokens.TotalTokens > existing.Tokens.TotalTokens;
        }

        return candidate.HasSpeed && !existing.HasSpeed;
    }

    /// <summary>A record's own <c>costUSD</c> wins. Otherwise the tokens are priced by model.</summary>
    public static CostSummary Aggregate(IEnumerable<ClaudeUsageEntry> entries, DateTimeOffset now, TimeZoneInfo zone, PricingTable pricing)
    {
        var accumulator = new CostAccumulator(now, zone);
        foreach (var entry in entries)
        {
            var model = string.IsNullOrWhiteSpace(entry.Model) ? null : entry.Model.Trim();
            if (entry.CostUsd is { } carried)
            {
                accumulator.Add(entry.Timestamp, carried);
            }
            else if (model is not null && pricing.Resolve(model) is { } rates)
            {
                accumulator.Add(entry.Timestamp, rates.Cost(entry.Tokens, entry.Fast));
            }
            else if (model is not null && entry.Tokens.TotalTokens > 0 && entry.Timestamp >= CostAccumulator.WindowStart(now, zone))
            {
                accumulator.AddUnpriced(model);
            }
        }

        return accumulator.Build();
    }

    private static IEnumerable<string> EnumerateLogs(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static string ExpandHome(string path, string home) =>
        path.StartsWith('~') ? Path.Combine(home, path[1..].TrimStart('\\', '/')) : path;
}

/// <summary>Parses Claude Code session log lines into <see cref="ClaudeUsageEntry"/> records.</summary>
public sealed class ClaudeLogParser : LogParser<ClaudeUsageEntry, ClaudeLogParser.State>
{
    private static readonly byte[] UsageMarker = """
        "usage"
        """u8.ToArray();

    /// <summary>Claude records are independent, so the parser keeps no state.</summary>
    public sealed class State;

    public override State CreateState() => new();

    public override bool IsCandidate(ReadOnlySpan<byte> line) => line.IndexOf(UsageMarker) >= 0;

    public override DateTimeOffset TimestampOf(ClaudeUsageEntry entry) => entry.Timestamp;

    public override void ParseLine(ReadOnlySpan<byte> line, State state, List<ClaudeUsageEntry> entries)
    {
        using var document = JsonDocument.Parse(line.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !TryString(root, "timestamp", out var timestampText)
            || !DateTimeOffset.TryParse(timestampText, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var timestamp)
            || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object
            || HasUnsupportedNull(root, message, usage)
            || !TryTokens(usage, out var tokens, out var fast, out var hasSpeed)
            || !IsValid(root, message))
        {
            return;
        }

        var model = TryString(message, "model", out var m) && m != "<synthetic>" ? m : null;
        var messageId = TryString(message, "id", out var id) ? id : null;
        var requestId = TryString(root, "requestId", out var rid) ? rid : null;
        var isSidechain = root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True;
        double? cost = root.TryGetProperty("costUSD", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;

        entries.Add(new ClaudeUsageEntry(timestamp, model, tokens.Input, tokens.CacheWrite5m, tokens.CacheWrite1h, tokens.CacheRead, tokens.Output,
            fast, hasSpeed, messageId, requestId, isSidechain, cost));

        // Advisor iterations are separate requests on their own model. Other iterations are already in the parent totals.
        if (!usage.TryGetProperty("iterations", out var iterations) || iterations.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var advisorIndex = 0;
        foreach (var iteration in iterations.EnumerateArray())
        {
            if (iteration.ValueKind != JsonValueKind.Object
                || !TryString(iteration, "type", out var type) || type != "advisor_message"
                || !TryString(iteration, "model", out var advisorModel) || advisorModel.Length == 0
                || !TryTokens(iteration, out var advisorTokens, out var advisorFast, out var advisorHasSpeed))
            {
                continue;
            }

            entries.Add(new ClaudeUsageEntry(timestamp, advisorModel, advisorTokens.Input, advisorTokens.CacheWrite5m, advisorTokens.CacheWrite1h,
                advisorTokens.CacheRead, advisorTokens.Output, advisorFast, advisorHasSpeed,
                messageId is null ? null : $"{messageId}:advisor:{advisorIndex}", requestId, isSidechain, null));
            advisorIndex++;
        }
    }

    private static bool TryTokens(JsonElement usage, out TokenUsage tokens, out bool fast, out bool hasSpeed)
    {
        tokens = default;
        fast = false;
        hasSpeed = false;
        if (!TryLong(usage, "input_tokens", out var input) || !TryLong(usage, "output_tokens", out var output))
        {
            return false;
        }

        // An unknown speed value marks a log shape that the scanner does not understand.
        if (usage.TryGetProperty("speed", out var speed) && speed.ValueKind == JsonValueKind.String)
        {
            var value = speed.GetString();
            if (value is not ("fast" or "standard"))
            {
                return false;
            }

            hasSpeed = true;
            fast = value == "fast";
        }

        long cacheWrite5m;
        long cacheWrite1h = 0;
        if (usage.TryGetProperty("cache_creation", out var creation) && creation.ValueKind == JsonValueKind.Object)
        {
            cacheWrite5m = TryLong(creation, "ephemeral_5m_input_tokens", out var five) ? five : 0;
            cacheWrite1h = TryLong(creation, "ephemeral_1h_input_tokens", out var hour) ? hour : 0;
        }
        else
        {
            cacheWrite5m = TryLong(usage, "cache_creation_input_tokens", out var legacy) ? legacy : 0;
        }

        var cacheRead = TryLong(usage, "cache_read_input_tokens", out var read) ? read : 0;
        tokens = new TokenUsage(input, cacheWrite5m, cacheWrite1h, cacheRead, output);
        return true;
    }

    /// <summary>A version that is not semver-like marks a foreign log format. Present but empty IDs mark a malformed record.</summary>
    private static bool IsValid(JsonElement root, JsonElement message)
    {
        if (TryString(root, "version", out var version) && !IsSemverPrefix(version))
        {
            return false;
        }

        return !(IsEmptyString(root, "sessionId") || IsEmptyString(root, "requestId") || IsEmptyString(message, "id") || IsEmptyString(message, "model"));
    }

    internal static bool IsSemverPrefix(string value)
    {
        var parts = value.Split('.', 3);
        return parts.Length == 3
            && parts[0].Length > 0 && parts[0].All(char.IsAsciiDigit)
            && parts[1].Length > 0 && parts[1].All(char.IsAsciiDigit)
            && parts[2].Length > 0 && char.IsAsciiDigit(parts[2][0]);
    }

    /// <summary>Claude Code never writes null in the fields that the scanner reads. A record that does has a foreign shape.</summary>
    private static bool HasUnsupportedNull(JsonElement root, JsonElement message, JsonElement usage) =>
        AnyNull(root, "cwd", "costUSD", "version", "sessionId", "requestId", "isApiErrorMessage")
        || AnyNull(message, "id", "model")
        || AnyNull(usage, "speed", "cache_read_input_tokens", "cache_creation_input_tokens");

    private static bool AnyNull(JsonElement element, params string[] names) =>
        names.Any(name => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Null);

    private static bool IsEmptyString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0;

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()!;
            return true;
        }

        return false;
    }

    private static bool TryLong(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value);
    }
}
