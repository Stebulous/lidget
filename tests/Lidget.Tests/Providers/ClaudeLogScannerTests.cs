using System.Text;
using System.Text.Json.Nodes;
using Lidget.Core.Costs;
using Lidget.Providers.Claude;

namespace Lidget.Tests.Providers;

public class ClaudeLogScannerTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("UTC-4", TimeSpan.FromHours(-4), "UTC-4", "UTC-4");

    // 2026-10-22 08:00 local time.
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static string Line(
        string timestamp = "2026-10-22T10:00:00Z",
        string model = "claude-opus-5-5",
        string? messageId = "msg_1",
        string? requestId = "req_1",
        long input = 1000,
        long output = 500,
        bool sidechain = false,
        Action<JsonObject>? edit = null)
    {
        var usage = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = output, ["cache_read_input_tokens"] = 0 };
        var message = new JsonObject { ["model"] = model, ["usage"] = usage };
        if (messageId is not null)
        {
            message["id"] = messageId;
        }

        var root = new JsonObject
        {
            ["timestamp"] = timestamp,
            ["version"] = "2.1.300",
            ["sessionId"] = "session",
            ["isSidechain"] = sidechain,
            ["message"] = message,
        };
        if (requestId is not null)
        {
            root["requestId"] = requestId;
        }

        edit?.Invoke(root);
        return root.ToJsonString();
    }

    private static List<ClaudeUsageEntry> Parse(string line)
    {
        var entries = new List<ClaudeUsageEntry>();
        var parser = new ClaudeLogParser();
        var bytes = Encoding.UTF8.GetBytes(line);
        if (parser.IsCandidate(bytes))
        {
            parser.ParseLine(bytes, parser.CreateState(), entries);
        }

        return entries;
    }

    [Fact]
    public void ParsesUsageWithJsonWhitespace()
    {
        var line = Line().Replace("\"usage\":{", "\"usage\" : \t{");
        Assert.Single(Parse(line));
    }

    [Fact]
    public void ParsesTokenBucketsAndSpeed()
    {
        var entry = Assert.Single(Parse(Line(edit: root =>
        {
            var usage = root["message"]!["usage"]!.AsObject();
            usage["cache_read_input_tokens"] = 300;
            usage["cache_creation"] = new JsonObject { ["ephemeral_5m_input_tokens"] = 40, ["ephemeral_1h_input_tokens"] = 60 };
            usage["cache_creation_input_tokens"] = 100;
            usage["speed"] = "fast";
        })));

        Assert.Equal(new TokenUsage(1000, 40, 60, 300, 500), entry.Tokens);
        Assert.True(entry.Fast);
        Assert.True(entry.HasSpeed);
        Assert.Equal("msg_1", entry.MessageId);
        Assert.Equal("req_1", entry.RequestId);
    }

    [Fact]
    public void LegacyCacheCreationCountsAsFiveMinuteWrites()
    {
        var entry = Assert.Single(Parse(Line(edit: root => root["message"]!["usage"]!["cache_creation_input_tokens"] = 70)));
        Assert.Equal(70, entry.CacheWrite5m);
        Assert.Equal(0, entry.CacheWrite1h);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("null-field")]
    [InlineData("speed")]
    [InlineData("empty-request")]
    [InlineData("no-output")]
    public void SkipsInvalidRecords(string problem)
    {
        var line = Line(edit: root =>
        {
            switch (problem)
            {
                case "version": root["version"] = "unknown"; break;
                case "null-field": root["costUSD"] = null; break;
                case "speed": root["message"]!["usage"]!["speed"] = "turbo"; break;
                case "empty-request": root["requestId"] = string.Empty; break;
                case "no-output": root["message"]!["usage"]!.AsObject().Remove("output_tokens"); break;
            }
        });

        Assert.Empty(Parse(line));
    }

    [Fact]
    public void AdvisorIterationsBecomeSeparateEntries()
    {
        var entries = Parse(Line(edit: root => root["message"]!["usage"]!["iterations"] = new JsonArray
        {
            new JsonObject { ["type"] = "message", ["model"] = null, ["input_tokens"] = 5, ["output_tokens"] = 5 },
            new JsonObject { ["type"] = "advisor_message", ["model"] = "claude-fable-5", ["input_tokens"] = 20, ["output_tokens"] = 10 },
        }));

        Assert.Equal(2, entries.Count);
        Assert.Equal("claude-fable-5", entries[1].Model);
        Assert.Equal("msg_1:advisor:0", entries[1].MessageId);
        Assert.Equal(30, entries[1].Tokens.TotalTokens);
    }

    [Fact]
    public void SyntheticModelHasNoModel()
    {
        Assert.Null(Assert.Single(Parse(Line(model: "<synthetic>"))).Model);
    }

    [Fact]
    public void DedupKeepsTheParentAndTheLargerRecord()
    {
        var first = Parse(Line(input: 100))[0];
        var larger = Parse(Line(input: 200))[0];
        var sidechainReplay = Parse(Line(requestId: "req_2", input: 900, sidechain: true))[0];
        var other = Parse(Line(messageId: "msg_2"))[0];
        var noId = Parse(Line(messageId: null))[0];

        var deduped = ClaudeLogScanner.Dedup([first, larger, sidechainReplay, other, noId, noId]);

        Assert.Equal(4, deduped.Count);
        Assert.Equal(200, deduped[0].Input);
        Assert.False(deduped[0].IsSidechain);
    }

    [Fact]
    public void AggregatesByLocalDayAndPrefersCarriedCost()
    {
        var pricing = PricingTable.LoadDefault();
        ClaudeUsageEntry Entry(string timestamp, double? cost = null, string model = "claude-opus-5-5") =>
            Parse(Line(timestamp: timestamp, model: model, messageId: Guid.NewGuid().ToString(), input: 1_000_000, output: 0,
                edit: root => { if (cost is not null) root["costUSD"] = cost; }))[0];

        var summary = ClaudeLogScanner.Aggregate(
            [
                Entry("2026-10-22T04:30:00Z"),            // 00:30 local today: $4
                Entry("2026-10-22T03:30:00Z", cost: 1.5), // 23:30 local yesterday: carried $1.50
                Entry("2026-10-16T05:00:00Z"),            // 6 days ago: $4
                Entry("2026-10-15T05:00:00Z"),            // 7 days ago: 30-day total only
                Entry("2026-09-23T05:00:00Z"),            // 29 days ago: 30-day total only
                Entry("2026-09-22T05:00:00Z"),            // 30 days ago: outside
                Entry("2026-10-22T05:00:00Z", model: "claude-unknown-9"),
            ],
            Now,
            Zone,
            pricing);

        Assert.Equal(4m, summary.Today);
        Assert.Equal(9.5m, summary.Last7Days);
        Assert.Equal(17.5m, summary.Last30Days);
        Assert.Equal(["claude-unknown-9"], summary.UnpricedModels);
    }

    [Fact]
    public void ScanReadsOnlyNewCompleteLines()
    {
        using var dir = new TempDirectory();
        var projects = Directory.CreateDirectory(Path.Combine(dir.Path, "projects", "proj")).FullName;
        var log = Path.Combine(projects, "session.jsonl");
        using var cache = new CostCache(dir.File("cost-cache.db"));
        var scanner = new ClaudeLogScanner(cache, PricingTable.LoadDefault(), () => [dir.Path], Zone);

        string Usage(string id) => Line(timestamp: "2026-10-22T10:00:00Z", messageId: id, input: 1_000_000, output: 0);

        File.WriteAllText(log, Usage("a") + "\n{\"type\":\"user\"}\n" + Usage("b"));
        Assert.Equal(4m, scanner.Scan(Now, default)!.Today);

        // The last line had no newline, so it was not read. Complete it and add one more record.
        File.AppendAllText(log, "\n" + Usage("c") + "\n");
        Assert.Equal(12m, scanner.Scan(Now, default)!.Today);

        // A replaced, shorter file is read again from the start.
        File.WriteAllText(log, Usage("z") + "\n");
        Assert.Equal(4m, scanner.Scan(Now, default)!.Today);

        File.Delete(log);
        Assert.Null(scanner.Scan(Now, default));
        Assert.Empty(cache.GetFiles(ClaudeLogScanner.SourceName));
    }

    [Theory]
    [InlineData("1.0.24", true)]
    [InlineData("2.1.300-beta", true)]
    [InlineData("1.0", false)]
    [InlineData("unknown", false)]
    public void SemverPrefix(string version, bool expected)
    {
        Assert.Equal(expected, ClaudeLogParser.IsSemverPrefix(version));
    }
}
