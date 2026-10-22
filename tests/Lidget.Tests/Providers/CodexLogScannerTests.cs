using System.Text;
using System.Text.Json.Nodes;
using Lidget.Core.Costs;
using Lidget.Providers.Codex;

namespace Lidget.Tests.Providers;

public class CodexLogScannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static string SessionMeta(string timestamp = "2026-10-22T09:00:00Z", string? forkedFrom = null) => new JsonObject
    {
        ["timestamp"] = timestamp,
        ["type"] = "session_meta",
        ["payload"] = new JsonObject { ["id"] = "s", ["forked_from_id"] = forkedFrom },
    }.ToJsonString();

    private static string TurnContext(string model) => new JsonObject
    {
        ["timestamp"] = "2026-10-22T09:00:01Z",
        ["type"] = "turn_context",
        ["payload"] = new JsonObject { ["model"] = model },
    }.ToJsonString();

    private static string Tier(string tier) => new JsonObject
    {
        ["timestamp"] = "2026-10-22T09:00:01Z",
        ["type"] = "event_msg",
        ["payload"] = new JsonObject { ["type"] = "thread_settings_applied", ["thread_settings"] = new JsonObject { ["service_tier"] = tier } },
    }.ToJsonString();

    private static string TaskStarted(double startedAt) => new JsonObject
    {
        ["timestamp"] = "2026-10-22T09:00:02Z",
        ["type"] = "event_msg",
        ["payload"] = new JsonObject { ["type"] = "task_started", ["started_at"] = startedAt },
    }.ToJsonString();

    private static JsonObject Usage(long input, long cached, long output) => new()
    {
        ["input_tokens"] = input,
        ["cached_input_tokens"] = cached,
        ["output_tokens"] = output,
        ["reasoning_output_tokens"] = 0,
        ["total_tokens"] = input + output,
    };

    private static string TokenCount(string timestamp, JsonObject? total, JsonObject? last = null)
    {
        var info = new JsonObject();
        if (total is not null)
        {
            info["total_token_usage"] = total;
        }

        if (last is not null)
        {
            info["last_token_usage"] = last;
        }

        return new JsonObject
        {
            ["timestamp"] = timestamp,
            ["type"] = "event_msg",
            ["payload"] = new JsonObject { ["type"] = "token_count", ["info"] = info },
        }.ToJsonString();
    }

    private static List<CodexUsageEntry> Parse(params string[] lines)
    {
        var parser = new CodexLogParser();
        var state = parser.CreateState();
        var entries = new List<CodexUsageEntry>();
        foreach (var line in lines)
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            if (parser.IsCandidate(bytes))
            {
                parser.ParseLine(bytes, state, entries);
            }
        }

        return entries;
    }

    [Fact]
    public void UsesLastUsageOrTheDeltaOfTotalsAndSkipsRepeatedSnapshots()
    {
        var entries = Parse(
            SessionMeta(),
            TurnContext("gpt-6-sol"),
            TokenCount("2026-10-22T09:01:00Z", Usage(100, 40, 10), Usage(100, 40, 10)),
            TokenCount("2026-10-22T09:02:00Z", Usage(100, 40, 10), Usage(100, 40, 10)),
            TokenCount("2026-10-22T09:03:00Z", Usage(250, 90, 30)));

        Assert.Equal(2, entries.Count);
        Assert.Equal(("gpt-6-sol", 100L, 40L, 10L), (entries[0].Model, entries[0].Input, entries[0].Cached, entries[0].Output));
        Assert.Equal((150L, 50L, 20L), (entries[1].Input, entries[1].Cached, entries[1].Output));
    }

    [Fact]
    public void SessionsWithoutModelUseGpt5()
    {
        Assert.Equal("gpt-5", Assert.Single(Parse(TokenCount("2026-10-22T09:01:00Z", Usage(10, 0, 1)))).Model);
    }

    [Fact]
    public void ChildSessionsSkipTheReplayedParentHistory()
    {
        var created = new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var entries = Parse(
            SessionMeta(forkedFrom: "parent"),
            TurnContext("gpt-6-sol"),
            TaskStarted(created - 3600),
            TokenCount("2026-10-22T09:00:00Z", Usage(5000, 0, 500)),
            TaskStarted(created + 5),
            TokenCount("2026-10-22T09:05:00Z", Usage(5100, 0, 520)));

        var entry = Assert.Single(entries);
        Assert.Equal((100L, 20L), (entry.Input, entry.Output));
    }

    [Fact]
    public void RecordsTheServiceTierOfEachTurn()
    {
        var entries = Parse(
            TurnContext("gpt-6-astra"),
            Tier("fast"),
            TokenCount("2026-10-22T09:01:00Z", null, Usage(10, 0, 1)),
            Tier("ultrafast"),
            TokenCount("2026-10-22T09:02:00Z", null, Usage(20, 0, 2)));

        Assert.Equal((true, false), (entries[0].Fast, entries[0].Ultrafast));
        Assert.Equal((false, true), (entries[1].Fast, entries[1].Ultrafast));
    }

    [Fact]
    public void AggregatePricesTiersAutoReviewAndCopiesOnce()
    {
        var pricing = PricingTable.LoadDefault();
        var at = new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);
        CodexUsageEntry Entry(string model, bool fast = false, bool ultrafast = false, DateTimeOffset? time = null) =>
            new(time ?? at, model, null, Input: 100_000, Cached: 0, Output: 0, Reasoning: 0, Total: 100_000, fast, ultrafast);

        var standard = Price(Entry("gpt-6-sol"));
        Assert.Equal(0.2m, standard.Today);

        // A copied rollout has the same event. It counts once.
        Assert.Equal(0.2m, Price(Entry("gpt-6-sol"), Entry("gpt-6-sol")).Today);
        Assert.Equal(0.4m, Price(Entry("gpt-6-sol", fast: true)).Today);
        Assert.Equal(6m, Price(Entry("gpt-6-astra", ultrafast: true)).Today);
        Assert.Equal(0m, Price(Entry(CodexLogScanner.AutoReviewModel)).Today);

        var unknown = Price(Entry("gpt-unknown-9"));
        Assert.Equal(0m, unknown.Today);
        Assert.Equal(["gpt-unknown-9"], unknown.UnpricedModels);

        CostSummaryShim Price(params CodexUsageEntry[] entries)
        {
            var summary = CodexLogScanner.Aggregate(entries, Now, TimeZoneInfo.Utc, pricing);
            return new CostSummaryShim(summary.Today, summary.UnpricedModels);
        }
    }

    [Fact]
    public void CachedInputUsesTheCacheReadRate()
    {
        var entry = new CodexUsageEntry(Now, "gpt-6-sol", null, Input: 200_000, Cached: 120_000, Output: 20_000, Reasoning: 0, Total: 220_000, false, false);

        // 80k x $2 + 120k x $0.20 + 20k x $10 per million.
        Assert.Equal(0.384m, CodexLogScanner.Aggregate([entry], Now, TimeZoneInfo.Utc, PricingTable.LoadDefault()).Today);
    }

    [Fact]
    public void LongPromptsUseLongContextRates()
    {
        var entry = new CodexUsageEntry(Now, "gpt-6-sol", null, Input: 1_000_000, Cached: 600_000, Output: 100_000, Reasoning: 0, Total: 1_100_000, false, false);

        // Above 272k prompt tokens: 0.4M x $4 + 0.6M x $0.40 + 0.1M x $15.
        Assert.Equal(3.34m, CodexLogScanner.Aggregate([entry], Now, TimeZoneInfo.Utc, PricingTable.LoadDefault()).Today);
    }

    [Fact]
    public void SessionsFolderWinsOverArchivedCopies()
    {
        using var dir = new TempDirectory();
        foreach (var folder in new[] { "sessions", "archived_sessions" })
        {
            var day = Directory.CreateDirectory(Path.Combine(dir.Path, folder, "2026", "10", "22")).FullName;
            File.WriteAllText(Path.Combine(day, "rollout-a.jsonl"), string.Empty);
        }

        File.WriteAllText(Path.Combine(dir.Path, "archived_sessions", "2026", "10", "22", "rollout-b.jsonl"), string.Empty);

        var files = CodexLogScanner.SessionFiles([dir.Path]);

        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.Contains(Path.Combine("sessions", "2026"), StringComparison.Ordinal) && !f.Contains("archived", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("rollout-b.jsonl", StringComparison.Ordinal));
    }

    private sealed record CostSummaryShim(decimal Today, IReadOnlyList<string> UnpricedModels);
}
