using System.Globalization;
using System.Text.Json;
using Lidget.Core.Costs;
using Lidget.Core.Models;
using Microsoft.Extensions.Logging;

namespace Lidget.Providers.Codex;

/// <summary>Token usage of one Codex turn. <see cref="Input"/> includes <see cref="Cached"/>.</summary>
public sealed record CodexUsageEntry(
    DateTimeOffset Timestamp,
    string Model,
    string? PricingModel,
    long Input,
    long Cached,
    long Output,
    long Reasoning,
    long Total,
    bool Fast,
    bool Ultrafast);

/// <summary>
/// Estimates Codex costs from Codex CLI session rollouts (<c>sessions/**/*.jsonl</c> and
/// <c>archived_sessions/</c>). The rules are ported from OpenUsage (<c>CodexLogUsageScanner.swift</c>,
/// <c>CodexLogFileParser.swift</c>, and <c>CodexUsagePricing.swift</c>, MIT).
/// </summary>
public sealed class CodexLogScanner : ICostSource
{
    public const string SourceName = "codex";
    public const string AutoReviewModel = "codex-auto-review";
    public const string ReserveModel = "gpt-reserve";

    /// <summary>Codex auto-review is free for ChatGPT sign-ins from this time.</summary>
    public static readonly DateTimeOffset AutoReviewFreeSince = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

    // Before auto-review was free, it is priced as the newest model released at the request time.
    private static readonly (DateTimeOffset ReleasedOn, string Model)[] AutoReviewModels =
    [
        (new(2026, 7, 9, 0, 0, 0, TimeSpan.Zero), "gpt-5.6-luna"),
        (new(2026, 4, 23, 0, 0, 0, TimeSpan.Zero), "gpt-5.5"),
        (new(2026, 3, 5, 0, 0, 0, TimeSpan.Zero), "gpt-5.4"),
        (new(2026, 2, 5, 0, 0, 0, TimeSpan.Zero), "gpt-5.3-codex"),
        (new(2025, 12, 11, 0, 0, 0, TimeSpan.Zero), "gpt-5.2-codex"),
        (new(2025, 11, 13, 0, 0, 0, TimeSpan.Zero), "gpt-5.1-codex"),
        (new(2025, 9, 15, 0, 0, 0, TimeSpan.Zero), "gpt-5-codex"),
        (new(2025, 8, 7, 0, 0, 0, TimeSpan.Zero), "gpt-5"),
    ];

    private readonly IncrementalLogScanner<CodexUsageEntry, CodexLogParser.State> _scanner;
    private readonly CostCache _cache;
    private readonly PricingTable _pricing;
    private readonly Func<IReadOnlyList<string>> _homes;
    private readonly TimeZoneInfo _zone;

    public CodexLogScanner(CostCache cache, PricingTable pricing, Func<IReadOnlyList<string>>? homes = null, TimeZoneInfo? zone = null, ILogger? logger = null)
    {
        _cache = cache;
        _pricing = pricing;
        _homes = homes ?? DefaultHomes;
        _zone = zone ?? TimeZoneInfo.Local;
        _scanner = new IncrementalLogScanner<CodexUsageEntry, CodexLogParser.State>(SourceName, cache, new CodexLogParser(), logger);
    }

    public string ProviderId => ProviderIds.Codex;

    public CostSummary? Scan(DateTimeOffset now, CancellationToken ct)
    {
        var files = SessionFiles(_homes());

        // Update also with no files, so that the cache forgets deleted logs.
        _scanner.Update(files, ct);
        if (files.Count == 0)
        {
            return null;
        }

        var since = CostAccumulator.WindowStart(now, _zone);
        _cache.Prune((since - TimeSpan.FromDays(2)).ToUnixTimeMilliseconds());
        return Aggregate(_scanner.Load(since), now, _zone, _pricing);
    }

    /// <summary>Each entry of <c>CODEX_HOME</c> (comma-separated), or else <c>~\.codex</c>.</summary>
    public static IReadOnlyList<string> DefaultHomes()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME")?.Trim();
        return string.IsNullOrEmpty(configured)
            ? [Path.Combine(home, ".codex")]
            : configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(entry => entry.StartsWith('~') ? Path.Combine(home, entry[1..].TrimStart('\\', '/')) : entry)
                .ToList();
    }

    /// <summary>
    /// Rollouts in <c>sessions</c> and <c>archived_sessions</c> of each home. When both folders have the
    /// same relative path, the <c>sessions</c> copy wins. A home without either folder is read directly.
    /// </summary>
    public static IReadOnlyList<string> SessionFiles(IEnumerable<string> homes)
    {
        var files = new List<string>();
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var home in homes)
        {
            var directories = new[] { "sessions", "archived_sessions" }
                .Select(name => Path.GetFullPath(Path.Combine(home, name)))
                .Where(Directory.Exists)
                .ToList();
            if (directories.Count == 0 && Directory.Exists(home))
            {
                directories.Add(Path.GetFullPath(home));
            }

            var seenRelative = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in directories.Where(seenDirectories.Add))
            {
                var found = Directory.EnumerateFiles(directory, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Order(StringComparer.Ordinal);
                foreach (var file in found)
                {
                    if (seenRelative.Add(Path.GetRelativePath(directory, file)))
                    {
                        files.Add(file);
                    }
                }
            }
        }

        return files;
    }

    /// <summary>
    /// Counts identical events (copied rollouts) once, and prices each turn:
    /// non-cached input, cached input, and output, times the service-tier multiplier of the turn.
    /// </summary>
    public static CostSummary Aggregate(IEnumerable<CodexUsageEntry> entries, DateTimeOffset now, TimeZoneInfo zone, PricingTable pricing)
    {
        var accumulator = new CostAccumulator(now, zone);
        var seen = new HashSet<(DateTimeOffset, string, string?, long, long, long, long, long)>();

        foreach (var entry in entries)
        {
            if (!seen.Add((entry.Timestamp, entry.Model, entry.PricingModel, entry.Input, entry.Cached, entry.Output, entry.Reasoning, entry.Total)))
            {
                continue;
            }

            var model = entry.Model.Trim();
            if (model.Length == 0)
            {
                continue;
            }

            if (model == AutoReviewModel && entry.Timestamp >= AutoReviewFreeSince)
            {
                accumulator.Add(entry.Timestamp, 0);
                continue;
            }

            var effective = model == AutoReviewModel
                ? AutoReviewModels.FirstOrDefault(m => entry.Timestamp >= m.ReleasedOn).Model ?? "gpt-5"
                : entry.PricingModel ?? model;

            // Codex speed is a service tier. A "-fast" alias resolves through its base rates, so that the
            // tier multiplier applies once.
            var canonical = pricing.CanonicalName(effective);
            var isFastAlias = canonical.EndsWith("-fast", StringComparison.Ordinal);
            var rateModel = isFastAlias ? canonical[..^"-fast".Length] : canonical;
            var baseRates = pricing.Resolve(rateModel);
            var rates = baseRates ?? pricing.Resolve(effective);
            if (rates is null)
            {
                if (entry.Total > 0)
                {
                    accumulator.AddUnpriced(model);
                }

                continue;
            }

            var fastTier = isFastAlias ? baseRates is not null : entry.Fast;
            var ultrafastTier = entry.Ultrafast && (!isFastAlias || baseRates is not null);
            var priced = rates with { Fast = rates.Fast == 1 ? 2 : rates.Fast };
            var tokens = new TokenUsage(Math.Max(0, entry.Input - entry.Cached), 0, 0, entry.Cached, entry.Output);
            accumulator.Add(entry.Timestamp, priced.Cost(tokens, fastTier, ultrafastTier));
        }

        return accumulator.Build();
    }
}

/// <summary>Parses Codex rollout lines into one <see cref="CodexUsageEntry"/> for each completed turn.</summary>
public sealed class CodexLogParser : LogParser<CodexUsageEntry, CodexLogParser.State>
{
    private static readonly byte[][] Markers =
    [
        "\"turn_context\""u8.ToArray(),
        "\"token_count\""u8.ToArray(),
        "\"session_meta\""u8.ToArray(),
        "\"task_started\""u8.ToArray(),
        "\"thread_settings_applied\""u8.ToArray(),
    ];

    /// <summary>Cumulative token counts of a <c>token_count</c> record.</summary>
    public sealed record RawUsage(long Input, long Cached, long Output, long Reasoning, long Total)
    {
        public static RawUsage From(JsonElement json)
        {
            var input = Long(json, "input_tokens", "prompt_tokens", "input");
            var cached = Long(json, "cached_input_tokens", "cache_read_input_tokens", "cached_tokens");
            var output = Long(json, "output_tokens", "completion_tokens", "output");
            var reasoning = Long(json, "reasoning_output_tokens", "reasoning_tokens");
            var reported = Long(json, "total_tokens");
            var recomputed = input + output + reasoning;
            return new RawUsage(input, cached, output, reasoning, reported > 0 || recomputed == 0 ? reported : recomputed);
        }

        public RawUsage Minus(RawUsage? previous) => new(
            Math.Max(0, Input - (previous?.Input ?? 0)),
            Math.Max(0, Cached - (previous?.Cached ?? 0)),
            Math.Max(0, Output - (previous?.Output ?? 0)),
            Math.Max(0, Reasoning - (previous?.Reasoning ?? 0)),
            Math.Max(0, Total - (previous?.Total ?? 0)));

        private static long Long(JsonElement json, params string[] names)
        {
            foreach (var name in names)
            {
                if (json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                {
                    return number;
                }
            }

            return 0;
        }
    }

    /// <summary>Context from earlier lines of the same rollout file.</summary>
    public sealed class State
    {
        public RawUsage? PreviousTotals { get; set; }

        public string? CurrentModel { get; set; }

        public bool TierFast { get; set; }

        public bool TierUltrafast { get; set; }

        public bool SawSessionMeta { get; set; }

        /// <summary>
        /// Set for a child session (subagent or fork), which first replays the parent's history.
        /// The replay ends at the first <c>task_started</c> that started at or after this Unix time.
        /// </summary>
        public double? ReplayUntil { get; set; }

        /// <summary>Replay gate without a creation time: compare the start time with the line's own time.</summary>
        public bool ReplayUntilSelfTimed { get; set; }

        public bool InReplay => ReplayUntil is not null || ReplayUntilSelfTimed;
    }

    public override State CreateState() => new();

    public override bool IsCandidate(ReadOnlySpan<byte> line)
    {
        foreach (var marker in Markers)
        {
            if (line.IndexOf(marker) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    public override DateTimeOffset TimestampOf(CodexUsageEntry entry) => entry.Timestamp;

    public override void ParseLine(ReadOnlySpan<byte> line, State state, List<CodexUsageEntry> entries)
    {
        using var document = JsonDocument.Parse(line.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var type = String(root, "type");
        var hasPayload = root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object;

        if (type == "turn_context")
        {
            if (hasPayload && ModelName(payload) is { } contextModel)
            {
                state.CurrentModel = contextModel;
            }

            return;
        }

        // A child rollout also replays the parent's metadata. Only its first metadata is its own.
        if (type == "session_meta")
        {
            if (!state.SawSessionMeta)
            {
                state.SawSessionMeta = true;
                if (hasPayload && IsChildSession(payload))
                {
                    if (Timestamp(root) is { } created)
                    {
                        state.ReplayUntil = Math.Floor(created.ToUnixTimeMilliseconds() / 1000.0);
                    }
                    else
                    {
                        state.ReplayUntilSelfTimed = true;
                    }
                }
            }

            return;
        }

        if (type != "event_msg" || !hasPayload)
        {
            return;
        }

        var payloadType = String(payload, "type");
        if (payloadType == "thread_settings_applied")
        {
            if (ServiceTier(payload) is { } tier)
            {
                state.TierUltrafast = tier == "ultrafast";
                state.TierFast = tier is "fast" or "priority";
            }

            return;
        }

        // Replayed task starts keep the parent's older start time. The first live turn ends the replay.
        if (payloadType == "task_started")
        {
            if (state.InReplay && payload.TryGetProperty("started_at", out var startedAt) && startedAt.ValueKind == JsonValueKind.Number)
            {
                var gate = state.ReplayUntil ?? (Timestamp(root) is { } lineTime ? Math.Floor(lineTime.ToUnixTimeMilliseconds() / 1000.0) : double.MaxValue);
                if (startedAt.GetDouble() >= gate)
                {
                    state.ReplayUntil = null;
                    state.ReplayUntilSelfTimed = false;
                }
            }

            return;
        }

        if (payloadType != "token_count" || Timestamp(root) is not { } timestamp)
        {
            return;
        }

        var hasInfo = payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object;
        var totals = hasInfo && info.TryGetProperty("total_token_usage", out var t) && t.ValueKind == JsonValueKind.Object ? RawUsage.From(t) : null;

        // Replayed parent history sets the delta baseline, but it is not usage of this session.
        if (state.InReplay)
        {
            if (totals is not null)
            {
                state.PreviousTotals = totals;
            }

            return;
        }

        // Codex can repeat a cumulative snapshot without another completed turn.
        if (totals is not null && state.PreviousTotals is { } previous && totals == previous)
        {
            return;
        }

        RawUsage usage;
        if (hasInfo && info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object)
        {
            usage = RawUsage.From(last);
        }
        else if (totals is not null)
        {
            usage = totals.Minus(state.PreviousTotals);
        }
        else
        {
            return;
        }

        if (totals is not null)
        {
            state.PreviousTotals = totals;
        }

        if (usage is { Input: 0, Cached: 0, Output: 0, Reasoning: 0 })
        {
            return;
        }

        var parsed = ModelName(payload) ?? (hasInfo ? ModelName(info) : null);
        if (parsed is not null)
        {
            state.CurrentModel = parsed;
        }

        // Early sessions without model metadata used gpt-5.
        var model = parsed ?? (state.CurrentModel ??= "gpt-5");

        entries.Add(new CodexUsageEntry(
            timestamp,
            model,
            model == CodexLogScanner.ReserveModel ? "gpt-5.6-luna" : null,
            usage.Input,
            Math.Min(usage.Cached, usage.Input),
            usage.Output,
            usage.Reasoning,
            usage.Total,
            state.TierFast,
            state.TierUltrafast));
    }

    private static bool IsChildSession(JsonElement payload) =>
        HasValue(payload, "forked_from_id")
        || HasValue(payload, "parent_thread_id")
        || String(payload, "thread_source") == "subagent"
        || (payload.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object && HasValue(source, "subagent"));

    private static bool HasValue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            _ => true,
        };

    private static string? ServiceTier(JsonElement payload)
    {
        if (payload.TryGetProperty("thread_settings", out var settings) && settings.ValueKind == JsonValueKind.Object
            && String(settings, "service_tier") is { Length: > 0 } nested)
        {
            return nested;
        }

        return String(payload, "service_tier") is { Length: > 0 } tier ? tier : null;
    }

    private static string? ModelName(JsonElement json)
    {
        foreach (var name in new[] { "model", "model_name" })
        {
            if (String(json, name)?.Trim() is { Length: > 0 } model)
            {
                return model;
            }
        }

        return json.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            && String(metadata, "model")?.Trim() is { Length: > 0 } fromMetadata
            ? fromMetadata
            : null;
    }

    private static DateTimeOffset? Timestamp(JsonElement root) =>
        String(root, "timestamp")?.Trim() is { Length: > 0 } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
