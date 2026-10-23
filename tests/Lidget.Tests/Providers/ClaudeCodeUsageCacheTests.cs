using System.Text.Json.Nodes;
using Lidget.Core.Models;
using Lidget.Providers.Claude;
using Microsoft.Extensions.Time.Testing;

namespace Lidget.Tests.Providers;

public class ClaudeCodeUsageCacheTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 1, 30, 0, TimeSpan.Zero);

    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(Now);

    public void Dispose() => _dir.Dispose();

    /// <summary>A Claude Code global configuration file with a saved usage response, in the shape of Claude Code 2.1.309.</summary>
    internal static string Config(
        DateTimeOffset fetchedAt,
        double session,
        double weekly,
        DateTimeOffset? sessionReset = null,
        string? cacheAccount = "account-1",
        string? signedInAccount = "account-1")
    {
        var weeklyReset = fetchedAt.AddDays(3).ToString("o");
        var cache = new JsonObject
        {
            ["fetchedAtMs"] = fetchedAt.ToUnixTimeMilliseconds(),
            ["utilization"] = new JsonObject
            {
                ["five_hour"] = new JsonObject { ["utilization"] = session, ["resets_at"] = (sessionReset ?? fetchedAt.AddHours(3)).ToString("o") },
                ["seven_day"] = new JsonObject { ["utilization"] = weekly, ["resets_at"] = weeklyReset },
                ["limits"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["kind"] = "session", ["group"] = "session", ["percent"] = session,
                        ["resets_at"] = (sessionReset ?? fetchedAt.AddHours(3)).ToString("o"), ["severity"] = "normal", ["is_active"] = true,
                    },
                    new JsonObject
                    {
                        ["kind"] = "weekly_all", ["group"] = "weekly", ["percent"] = weekly,
                        ["resets_at"] = weeklyReset, ["severity"] = "normal", ["is_active"] = false,
                    },
                },
            },
        };
        if (cacheAccount is not null)
        {
            cache["accountUuid"] = cacheAccount;
        }

        var root = new JsonObject { ["numStartups"] = 12, ["cachedUsageUtilization"] = cache };
        if (signedInAccount is not null)
        {
            root["oauthAccount"] = new JsonObject { ["accountUuid"] = signedInAccount, ["emailAddress"] = "user@example.com" };
        }

        return root.ToJsonString();
    }

    private ClaudeCodeUsageCache CreateCache() => new(_dir.File(".claude.json"), _time);

    private void Write(string content, DateTime? writeTime = null)
    {
        var path = _dir.File(".claude.json");
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, writeTime ?? DateTime.UtcNow);
    }

    [Fact]
    public void ReadsTheSavedResponse()
    {
        var fetchedAt = Now.AddMinutes(-3);
        Write(Config(fetchedAt, session: 37, weekly: 3));

        var usage = CreateCache().Read();

        Assert.NotNull(usage);
        Assert.Equal(fetchedAt, usage.FetchedAt);
        var session = usage.Limits.Single(l => l.Kind == LimitKind.Session);
        Assert.Equal(37, session.UsedPercent);
        Assert.Equal(ClaudeCodeUsageCache.SourceName, session.Source);
        Assert.Equal(fetchedAt, session.ObservedAt);
        Assert.Equal(3, usage.Limits.Single(l => l.Kind == LimitKind.Weekly).UsedPercent);
    }

    [Fact]
    public void IgnoresACopyOlderThanOneHour()
    {
        Write(Config(Now - ClaudeCodeUsageCache.MaxAge - TimeSpan.FromMinutes(1), session: 37, weekly: 3));

        Assert.Null(CreateCache().Read());
    }

    [Theory]
    [InlineData("account-2", "account-1")]
    [InlineData("account-1", null)]
    [InlineData(null, "account-1")]
    public void IgnoresACopyOfAnotherAccount(string? cacheAccount, string? signedInAccount)
    {
        Write(Config(Now.AddMinutes(-3), session: 37, weekly: 3, cacheAccount: cacheAccount, signedInAccount: signedInAccount));

        Assert.Null(CreateCache().Read());
    }

    [Fact]
    public void LeavesOutWindowsThatReset()
    {
        Write(Config(Now.AddMinutes(-40), session: 96, weekly: 30, sessionReset: Now.AddMinutes(-5)));

        var usage = CreateCache().Read();

        Assert.NotNull(usage);
        Assert.DoesNotContain(usage.Limits, l => l.Kind == LimitKind.Session);
        Assert.Contains(usage.Limits, l => l.Kind == LimitKind.Weekly);
    }

    [Theory]
    [InlineData("""{"numStartups":12}""")]
    [InlineData("""{"cachedUsageUtilization":{"fetchedAtMs":"soon","utilization":{}}}""")]
    [InlineData("[]")]
    public void OtherContentGivesNoReading(string content)
    {
        Write(content);

        Assert.Null(CreateCache().Read());
    }

    [Fact]
    public void AMissingFileGivesNoReading() => Assert.Null(CreateCache().Read());

    [Fact]
    public void ReadsTheFileAgainWhenItChanges()
    {
        var cache = CreateCache();
        Write(Config(Now.AddMinutes(-3), session: 37, weekly: 3), DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(37, cache.Read()!.Limits.Single(l => l.Kind == LimitKind.Session).UsedPercent);

        Write(Config(Now.AddMinutes(-1), session: 41, weekly: 4));

        Assert.Equal(41, cache.Read()!.Limits.Single(l => l.Kind == LimitKind.Session).UsedPercent);
    }

    [Fact]
    public void APartialFileKeepsTheLastReading()
    {
        var cache = CreateCache();
        Write(Config(Now.AddMinutes(-3), session: 37, weekly: 3), DateTime.UtcNow.AddMinutes(-1));
        cache.Read();

        Write("""{"cachedUsageUtilization":{"fetchedAtMs":""");

        Assert.Equal(37, cache.Read()!.Limits.Single(l => l.Kind == LimitKind.Session).UsedPercent);
    }
}
