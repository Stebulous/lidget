using System.Text.Json;
using Lidget.Providers.Claude;

namespace Lidget.Tests.Providers;

public class ClaudeResetMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 6, 22, 0, TimeSpan.Zero);

    private static Lidget.Core.Models.ResetSummary? Map(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ClaudeResetMapper.Map(document.RootElement, Now);
    }

    [Fact]
    public void MapsTheGrantsOfTheFixture()
    {
        var summary = Map(Fixtures.Read("claude/usage-resets.json"));

        Assert.NotNull(summary);
        Assert.Equal(3, summary.AvailableCount);
        Assert.Equal("grant_a1", summary.NextResetId);
        Assert.Equal(Now, summary.ObservedAt);
        Assert.True(summary.ListsAllResets);

        // Expired grants, grants without a reset, and grants that Lidget cannot read are left out.
        Assert.Equal(["grant_a1", "grant_b2", "grant_paused", "grant_later"], summary.Resets!.Select(r => r.Id));

        var next = summary.Resets![0];
        Assert.Equal(2, next.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 7, 59, 0, TimeSpan.Zero), next.ExpiresAt);
        Assert.True(next.IsUsable);
        Assert.True(next.CanRedeemNow);
        Assert.Null(next.Title);
        Assert.Null(next.GrantedAt);

        // Claude redeems only the next grant.
        var second = summary.Resets[1];
        Assert.True(second.IsUsable);
        Assert.False(second.CanRedeemNow);

        Assert.False(summary.Resets[2].IsUsable);
        Assert.False(summary.Resets[3].IsUsable);
        Assert.Null(summary.Resets[3].ExpiresAt);
        Assert.Equal("grant_a1", summary.NextToExpire(Now)!.Id);
    }

    [Theory]
    [InlineData("""{"five_hour":{"utilization":1}}""")]
    [InlineData("""{"cedar_ember":null}""")]
    [InlineData("""{"cedar_ember":"soon"}""")]
    [InlineData("""{"cedar_ember":{"eligible":false,"grants":[]}}""")]
    [InlineData("""{"cedar_ember":{"grants":[]}}""")]
    [InlineData("""[]""")]
    public void AResponseWithoutTheBlockHasNoResetData(string json)
    {
        Assert.Null(Map(json));
    }

    [Fact]
    public void TheSavedResponseOfClaudeCodeHasNoResetData()
    {
        Assert.Null(Map(Fixtures.Read("claude/usage-pro.json")));
    }

    [Fact]
    public void AnEligibleAccountWithoutGrantsHasNoResets()
    {
        var summary = Map("""{"cedar_ember":{"eligible":true}}""");

        Assert.NotNull(summary);
        Assert.Equal(0, summary.AvailableCount);
        Assert.Empty(summary.Resets!);
        Assert.Null(summary.NextResetId);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"grant_other\"")]
    [InlineData("\"grant_paused\"")]
    public void WithoutAUsableNextGrantNoResetIsAvailable(string nextGrantId)
    {
        var summary = Map($$$"""
            {"cedar_ember":{"eligible":true,"next_grant_id":{{{nextGrantId}}},"grants":[
              {"id":"grant_a1","resets_left":2,"ends_at":"2026-11-05T07:59:00Z","usable_now":true},
              {"id":"grant_paused","resets_left":1,"ends_at":"2026-11-05T07:59:00Z","paused":true,"usable_now":true}]}}
            """);

        Assert.NotNull(summary);
        Assert.Equal(0, summary.AvailableCount);
        Assert.Null(summary.NextResetId);
        Assert.Equal(2, summary.Resets!.Count);
        Assert.All(summary.Resets!, r => Assert.False(r.CanRedeemNow));
    }

    [Theory]
    [InlineData("grant_a1", true)]
    [InlineData("a-b_9", true)]
    [InlineData("", false)]
    [InlineData("Grant", false)]
    [InlineData("grant/../x", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    public void ChecksTheFormOfAGrantId(string id, bool valid)
    {
        Assert.Equal(valid, ClaudeResetMapper.IsGrantId(id));
    }
}
