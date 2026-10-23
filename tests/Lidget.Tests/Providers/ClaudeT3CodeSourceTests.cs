using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Lidget.Core.Models;
using Lidget.Providers.Claude;
using Microsoft.Extensions.Time.Testing;

namespace Lidget.Tests.Providers;

public class ClaudeT3CodeSourceTests : IDisposable
{
    // Log file times are real, so the fake clock starts at the real time.
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(Now);

    public void Dispose() => _dir.Dispose();

    private static string Stamp(DateTimeOffset at) =>
        "[" + at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + "] NTIVE: ";

    private static string EventLine(DateTimeOffset at, double session, double weekly, DateTimeOffset? sessionReset = null)
    {
        var reset = (sessionReset ?? at.AddHours(3)).ToUnixTimeSeconds();
        var entry = new JsonObject
        {
            ["provider"] = "claudeAgent",
            ["event"] = new JsonObject
            {
                ["direction"] = "incoming",
                ["payload"] = new JsonObject
                {
                    ["type"] = "rate_limit_event",
                    ["rate_limit_info"] = new JsonObject
                    {
                        ["status"] = "allowed",
                        ["resetsAt"] = reset,
                        ["rateLimitType"] = "five_hour",
                        ["unifiedWindows"] = new JsonObject
                        {
                            ["five_hour"] = new JsonObject { ["utilization"] = session, ["resetsAt"] = reset },
                            ["seven_day"] = new JsonObject { ["utilization"] = weekly, ["resetsAt"] = at.AddDays(4).ToUnixTimeSeconds() },
                        },
                    },
                },
            },
        };
        return Stamp(at) + entry.ToJsonString();
    }

    private static string OtherLine(DateTimeOffset at) =>
        Stamp(at) + """{"provider":"claudeAgent","event":{"payload":{"type":"assistant","message":{"content":"rate_limit_info is mentioned here"}}}}""";

    private string Log(string name = "events.thread-1.log") => _dir.File(name);

    private static void Append(string path, params string[] lines) =>
        File.AppendAllText(path, string.Concat(lines.Select(l => l + "\n")));

    [Fact]
    public void ReadsTheNewestSessionAndWeeklyReadings()
    {
        Append(Log(), EventLine(Now.AddMinutes(-9), 0.10, 0.30), OtherLine(Now.AddMinutes(-8)), EventLine(Now.AddMinutes(-2), 0.12, 0.31));

        var limits = new ClaudeT3CodeSource(_dir.Path, _time).Read();

        Assert.Equal(2, limits.Count);
        var session = limits.Single(l => l.Kind == LimitKind.Session);
        Assert.Equal(ClaudeUsageMapper.SessionId, session.Id);
        Assert.Equal(12, session.UsedPercent);
        Assert.Equal(Now.AddMinutes(-2), session.ObservedAt);
        Assert.Equal(ClaudeT3CodeSource.SourceName, session.Source);
        Assert.Equal(31, limits.Single(l => l.Kind == LimitKind.Weekly).UsedPercent);
    }

    [Fact]
    public void ReadsOnlyNewLinesAndWaitsForAnUnfinishedLine()
    {
        var source = new ClaudeT3CodeSource(_dir.Path, _time);
        Append(Log(), EventLine(Now.AddMinutes(-5), 0.20, 0.40));
        Assert.Equal(20, source.Read().Single(l => l.Kind == LimitKind.Session).UsedPercent);

        // The writer has not finished the line yet.
        var next = EventLine(Now.AddMinutes(-1), 0.25, 0.41);
        File.AppendAllText(Log(), next[..40]);
        Assert.Equal(20, source.Read().Single(l => l.Kind == LimitKind.Session).UsedPercent);

        File.AppendAllText(Log(), next[40..] + "\n");
        Assert.Equal(25, source.Read().Single(l => l.Kind == LimitKind.Session).UsedPercent);
    }

    [Fact]
    public void NewestReadingWinsAcrossFiles()
    {
        Append(Log("events.a.log"), EventLine(Now.AddMinutes(-1), 0.50, 0.60));
        Append(Log("events.b.log"), EventLine(Now.AddMinutes(-4), 0.45, 0.59));
        Append(Log("events.b.log.1"), EventLine(Now, 0.99, 0.99));

        var limits = new ClaudeT3CodeSource(_dir.Path, _time).Read();

        Assert.Equal(50, limits.Single(l => l.Kind == LimitKind.Session).UsedPercent);
    }

    [Fact]
    public void DropsWindowsThatReset()
    {
        Append(Log(), EventLine(Now.AddMinutes(-30), 0.80, 0.20, sessionReset: Now.AddMinutes(10)));
        var source = new ClaudeT3CodeSource(_dir.Path, _time);
        Assert.Equal(2, source.Read().Count);

        _time.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(LimitKind.Weekly, Assert.Single(source.Read()).Kind);
    }

    [Fact]
    public void ReadsOnlyTheEndOfALargeFile()
    {
        var filler = new string('x', 1024);
        var builder = new StringBuilder();
        builder.Append(EventLine(Now.AddMinutes(-20), 0.90, 0.90)).Append('\n');
        for (var i = 0; i < (ClaudeT3CodeSource.MaxReadBytes / 1024) + 10; i++)
        {
            builder.Append(filler).Append('\n');
        }

        builder.Append(EventLine(Now.AddMinutes(-1), 0.05, 0.10)).Append('\n');
        File.WriteAllText(Log(), builder.ToString());

        var limits = new ClaudeT3CodeSource(_dir.Path, _time).Read();

        Assert.Equal(5, limits.Single(l => l.Kind == LimitKind.Session).UsedPercent);
    }

    [Fact]
    public void MissingFolderGivesNoReadings()
    {
        var source = new ClaudeT3CodeSource(_dir.File("missing"), _time);
        source.StartWatching();

        Assert.Empty(source.Read());
    }

    [Fact]
    public void ParsesTheOlderLogFormatAndTheSingleWindowEvent()
    {
        var info = new JsonObject
        {
            ["status"] = "allowed_warning",
            ["rateLimitType"] = "seven_day",
            ["utilization"] = 0.81,
            ["resetsAt"] = Now.AddDays(2).ToUnixTimeSeconds(),
        };
        var older = Stamp(Now) + new JsonObject
        {
            ["observedAt"] = "x",
            ["event"] = new JsonObject
            {
                ["kind"] = "notification",
                ["method"] = "claude/rate_limit_event",
                ["payload"] = new JsonObject { ["type"] = "rate_limit_event", ["rate_limit_info"] = info },
            },
        }.ToJsonString();

        var limits = ClaudeT3CodeSource.ParseLine(Encoding.UTF8.GetBytes(older), Now);

        var weekly = Assert.Single(limits);
        Assert.Equal(ClaudeUsageMapper.WeeklyId, weekly.Id);
        Assert.Equal(81, weekly.UsedPercent);
        Assert.Equal(Now.AddDays(2), weekly.ResetsAt);
    }

    [Fact]
    public void IgnoresLinesWithoutATimestampOrValidJson()
    {
        Assert.Empty(ClaudeT3CodeSource.ParseLine("""{"type":"rate_limit_event"}"""u8, Now));
        Assert.Empty(ClaudeT3CodeSource.ParseLine("""[2026-10-23T03:52:27.364Z] NTIVE: {"type":"rate_limit_event", """u8, Now));
        Assert.Empty(ClaudeT3CodeSource.ParseLine("""[2026-10-23T03:52:27.364Z] NTIVE: {"type":"assistant","input":{"note":"rate_limit_event","rate_limit_info":{"rateLimitType":"five_hour","utilization":0.5}}}"""u8, Now));
    }
}
