using System.Text.Json;
using Lidget.Core.Models;
using Lidget.Providers.Claude;

namespace Lidget.Tests.Providers;

public class ClaudeUsageMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 6, 22, 0, TimeSpan.Zero);

    private static IReadOnlyList<LimitWindow> Map(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ClaudeUsageMapper.Map(document.RootElement, Now);
    }

    [Fact]
    public void MapsRecordedProResponse()
    {
        var limits = Map(Fixtures.Read("claude/usage-pro.json"));

        Assert.Equal(2, limits.Count);
        var session = limits[0];
        Assert.Equal(ClaudeUsageMapper.SessionId, session.Id);
        Assert.Equal(LimitKind.Session, session.Kind);
        Assert.Equal(38, session.UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 8, 19, 59, TimeSpan.Zero), session.ResetsAt!.Value.AddTicks(-(session.ResetsAt.Value.Ticks % TimeSpan.TicksPerSecond)));
        Assert.Equal("endpoint", session.Source);

        var weekly = limits[1];
        Assert.Equal(LimitKind.Weekly, weekly.Kind);
        Assert.Equal(52, weekly.UsedPercent);
    }

    [Fact]
    public void FallsBackToLegacyKeys()
    {
        var limits = Map("""
            {"five_hour":{"utilization":12.5,"resets_at":"2026-10-22T08:00:00+00:00"},
             "seven_day":{"utilization":40,"resets_at":"2026-10-25T00:00:00+00:00"}}
            """);

        Assert.Equal([LimitKind.Session, LimitKind.Weekly], limits.Select(l => l.Kind));
        Assert.Equal(12.5, limits[0].UsedPercent);
    }

    [Fact]
    public void MapsModelScopedWeeklyLimit()
    {
        var limits = Map("""
            {"limits":[
              {"kind":"weekly_all","percent":20,"resets_at":"2026-10-25T00:00:00+00:00"},
              {"kind":"weekly_scoped","percent":70,"resets_at":"2026-10-25T00:00:00+00:00","scope":{"model":{"display_name":"Fable"}}},
              {"kind":"unknown_kind","percent":99}
            ]}
            """);

        Assert.Equal(2, limits.Count);
        Assert.Equal("claude.weekly.fable", limits[1].Id);
        Assert.Equal("Weekly (Fable)", limits[1].Label);
        Assert.Equal(LimitKind.Other, limits[1].Kind);
    }

    [Fact]
    public void UnknownResponseGivesNoLimits()
    {
        Assert.Empty(Map("""{"something_new":{}}"""));
    }

    [Theory]
    [InlineData("pro", "default_claude_ai", "Pro")]
    [InlineData("max", "default_claude_max_20x", "Max 20x")]
    [InlineData(null, null, null)]
    public void FormatsPlan(string? subscription, string? tier, string? expected)
    {
        Assert.Equal(expected, ClaudeUsageMapper.FormatPlan(subscription, tier));
    }
}
