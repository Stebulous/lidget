using System.Text.Json;
using System.Text.Json.Nodes;
using Lidget.Core.Models;
using Lidget.Providers.Codex;
using Microsoft.Extensions.Time.Testing;

namespace Lidget.Tests.Providers;

/// <summary>Banked resets of the Codex provider: when it reads the details (D60), and redemption (D61).</summary>
public class CodexProviderResetTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 6, 30, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly ListLogger<CodexProvider> _log = new();
    private readonly List<FakeCodexTransport> _transports = [];

    /// <summary>The reset credits that the fake server has. A read without details gives only their count.</summary>
    private JsonArray _credits = Details()["credits"]!.DeepClone().AsArray();

    /// <summary>Whether the fake server sends <c>rateLimitResetCredits</c>. Older Codex versions do not.</summary>
    private bool _hasResetData = true;

    private Func<JsonElement, JsonNode?> _consume = _ => new JsonObject { ["outcome"] = "reset" };

    private static JsonObject Details() =>
        JsonNode.Parse(Fixtures.Read("codex/rate-limits-reset-details-0.163.0.json"))!["rateLimitResetCredits"]!.AsObject();

    private CodexProvider CreateProvider() => new(() => @"C:\fake\codex.cmd", _ =>
    {
        var transport = new FakeCodexTransport(Respond);
        _transports.Add(transport);
        return transport;
    }, _time, _log);

    private JsonNode? Respond(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "account/rateLimits/read":
                var result = JsonNode.Parse(Fixtures.Read("codex/rate-limits-reset-details-0.163.0.json"))!.AsObject();
                if (!_hasResetData)
                {
                    result.Remove("rateLimitResetCredits");
                    return result;
                }

                var available = _credits.Count(c => c!["status"]!.GetValue<string>() == "available");
                var skipDetails = parameters.GetProperty("excludeResetCreditDetails").GetBoolean();
                result["rateLimitResetCredits"] = new JsonObject
                {
                    ["availableCount"] = available,
                    ["credits"] = skipDetails ? null : _credits.DeepClone(),
                };
                return result;
            case "account/rateLimitResetCredit/consume":
                return _consume(parameters);
            default:
                return FakeCodexTransport.TranscriptResult(method);
        }
    }

    /// <summary>The <c>excludeResetCreditDetails</c> value of each rate-limit read, in order.</summary>
    private bool[] SkippedDetails() => _transports
        .SelectMany(t => t.Sent)
        .Where(m => m["method"]?.GetValue<string>() == "account/rateLimits/read")
        .Select(m => m["params"]!["excludeResetCreditDetails"]!.GetValue<bool>())
        .ToArray();

    private void RemoveCredit(string id) => _credits.Remove(_credits.First(c => c!["id"]!.GetValue<string>() == id));

    [Fact]
    public async Task TheFirstReadGetsTheDetails()
    {
        await using var provider = CreateProvider();

        var snapshot = await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal([false], SkippedDetails());
        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(2, snapshot.Limits.Count);
        var resets = snapshot.Resets!;
        Assert.Equal(3, resets.AvailableCount);
        Assert.True(resets.ListsAllResets);
        Assert.Equal("credit-0001", resets.NextResetId);
        var first = resets.Resets![0];
        Assert.Equal("credit-0001", first.Id);
        Assert.Equal("Full reset (Weekly + 5 hr)", first.Title);
        Assert.Equal(1, first.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791413513), first.GrantedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794005513), first.ExpiresAt);
        Assert.True(first.IsUsable);
        Assert.True(first.CanRedeemNow);
    }

    [Fact]
    public async Task ABackgroundReadSkipsTheDetailsAndKeepsTheLastOnes()
    {
        await using var provider = CreateProvider();
        var first = await provider.RefreshAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(5));
        var second = await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal([false, true], SkippedDetails());
        Assert.Same(first.Resets, second.Resets);
    }

    [Fact]
    public async Task AChangedCountReadsTheDetailsAgain()
    {
        await using var provider = CreateProvider();
        await provider.RefreshAsync(CancellationToken.None);

        RemoveCredit("credit-0001");
        _time.Advance(TimeSpan.FromMinutes(5));
        var snapshot = await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal([false, true, false], SkippedDetails());
        Assert.Equal(2, snapshot.Resets!.AvailableCount);
        Assert.Equal(["credit-0002", "credit-0003"], snapshot.Resets.Resets!.Select(r => r.Id));
        Assert.Equal(_time.GetUtcNow(), snapshot.Resets.ObservedAt);
    }

    [Fact]
    public async Task TheLastResetGoesAwayWithoutADetailRead()
    {
        _credits = [_credits[0]!.DeepClone()];
        await using var provider = CreateProvider();
        await provider.RefreshAsync(CancellationToken.None);

        _credits.Clear();
        _time.Advance(TimeSpan.FromMinutes(5));
        var snapshot = await provider.RefreshAsync(CancellationToken.None);

        // A count of 0 needs no details: the list is empty.
        Assert.Equal([false, true], SkippedDetails());
        Assert.Equal(0, snapshot.Resets!.AvailableCount);
        Assert.Empty(snapshot.Resets.Resets!);
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(10, false)]
    public async Task TheFlyoutReadsDetailsThatAreOlderThan10Minutes(int ageMinutes, bool skipsDetails)
    {
        await using var provider = CreateProvider();
        await provider.RefreshAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(ageMinutes));
        provider.RequestResetDetails();
        await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal([false, skipsDetails], SkippedDetails());

        // The request of the flyout applies to one refresh.
        _time.Advance(TimeSpan.FromMinutes(11));
        await provider.RefreshAsync(CancellationToken.None);
        Assert.True(SkippedDetails()[^1]);
    }

    [Fact]
    public async Task TheDetailsAreReadOneTimeIn6Hours()
    {
        await using var provider = CreateProvider();
        await provider.RefreshAsync(CancellationToken.None);

        _time.Advance(CodexProvider.ResetDetailMaxAge - TimeSpan.FromMinutes(1));
        await provider.RefreshAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(1));
        await provider.RefreshAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(5));
        await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal([false, true, false, true], SkippedDetails());
    }

    [Theory]
    [InlineData("redeeming")]
    [InlineData("redeemed")]
    [InlineData("unknown")]
    public async Task ACreditWithAnotherStatusIsNotAvailable(string status)
    {
        _credits[0]!["status"] = status;
        await using var provider = CreateProvider();

        var snapshot = await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, snapshot.Resets!.AvailableCount);
        Assert.Equal(["credit-0002", "credit-0003"], snapshot.Resets.Resets!.Select(r => r.Id));
    }

    [Fact]
    public async Task AnExpiredCreditIsLeftOut()
    {
        _credits[0]!["expiresAt"] = Now.AddMinutes(-1).ToUnixTimeSeconds();
        await using var provider = CreateProvider();

        var snapshot = await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(["credit-0002", "credit-0003"], snapshot.Resets!.Resets!.Select(r => r.Id));
    }

    [Fact]
    public async Task AnOlderCodexVersionGivesNoResetDataAndNoError()
    {
        _hasResetData = false;
        await using var provider = CreateProvider();

        var snapshot = await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(2, snapshot.Limits.Count);
        Assert.Null(snapshot.Resets);
    }

    [Fact]
    public async Task APushedUpdateKeepsTheResets()
    {
        await using var provider = CreateProvider();
        var pushed = new List<ProviderSnapshot>();
        provider.SnapshotPushed += (_, s) => pushed.Add(s);
        var first = await provider.RefreshAsync(CancellationToken.None);

        _transports[0].Notify("account/rateLimits/updated", """
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":91,"windowDurationMins":300,"resetsAt":1792663869},"secondary":null,"planType":null}}
            """);

        Assert.Same(first.Resets, Assert.Single(pushed).Resets);
    }

    [Fact]
    public async Task TheRedemptionRequestHasTheCreditAndTheKey()
    {
        await using var provider = CreateProvider();
        await provider.RefreshAsync(CancellationToken.None);

        var outcome = await provider.RedeemAsync("credit-0002", "key-1", CancellationToken.None);

        Assert.Equal(RedeemOutcome.Reset, outcome);
        var request = Assert.Single(_transports[0].Sent, m => m["method"]?.GetValue<string>() == "account/rateLimitResetCredit/consume");
        Assert.Equal("""{"creditId":"credit-0002","idempotencyKey":"key-1"}""", request["params"]!.ToJsonString());
    }

    [Theory]
    [InlineData("reset", RedeemOutcome.Reset)]
    [InlineData("nothingToReset", RedeemOutcome.NothingToReset)]
    [InlineData("noCredit", RedeemOutcome.NoReset)]
    [InlineData("alreadyRedeemed", RedeemOutcome.AlreadyRedeemed)]
    [InlineData("somethingNew", RedeemOutcome.Failed)]
    public async Task MapsEachResult(string result, RedeemOutcome expected)
    {
        _consume = _ => new JsonObject { ["outcome"] = result };
        await using var provider = CreateProvider();

        Assert.Equal(expected, await provider.RedeemAsync("credit-0001", "key-1", CancellationToken.None));
    }

    [Fact]
    public async Task AnErrorOfTheServerFails()
    {
        // An older Codex version does not have the method.
        _consume = _ => throw new CodexRpcException(-32601, "Method not found");
        await using var provider = CreateProvider();

        Assert.Equal(RedeemOutcome.Failed, await provider.RedeemAsync("credit-0001", "key-1", CancellationToken.None));
    }

    [Fact]
    public async Task AServerThatStopsDuringTheRequestIsNotConfirmed()
    {
        _consume = _ =>
        {
            _transports[^1].Close();
            return null;
        };
        await using var provider = CreateProvider();

        Assert.Equal(RedeemOutcome.Unconfirmed, await provider.RedeemAsync("credit-0001", "key-1", CancellationToken.None));
    }

    [Fact]
    public async Task ARedemptionShowsTheNewCountAndReadsTheDetailsAtTheNextRefresh()
    {
        await using var provider = CreateProvider();
        var pushed = new List<ProviderSnapshot>();
        provider.SnapshotPushed += (_, s) => pushed.Add(s);
        await provider.RefreshAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(1));
        await provider.RedeemAsync("credit-0001", "key-1", CancellationToken.None);

        // A pushed update before the next refresh shows the count without the redeemed reset.
        _transports[0].Notify("account/rateLimits/updated", """
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":0,"windowDurationMins":300,"resetsAt":1792663869},"secondary":null,"planType":null}}
            """);
        var resets = Assert.Single(pushed).Resets!;
        Assert.Equal(2, resets.AvailableCount);
        Assert.Equal(["credit-0002", "credit-0003"], resets.Resets!.Select(r => r.Id));
        Assert.Equal("credit-0002", resets.NextResetId);

        RemoveCredit("credit-0001");
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal([false, false], SkippedDetails());
    }

    [Theory]
    [InlineData("nothingToReset")]
    [InlineData("somethingNew")]
    public async Task AResultWithoutARedemptionKeepsTheResets(string result)
    {
        _consume = _ => new JsonObject { ["outcome"] = result };
        await using var provider = CreateProvider();
        var first = await provider.RefreshAsync(CancellationToken.None);

        await provider.RedeemAsync("credit-0001", "key-1", CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(5));
        var second = await provider.RefreshAsync(CancellationToken.None);

        Assert.Same(first.Resets, second.Resets);
        Assert.Equal([false, true], SkippedDetails());
    }

    [Fact]
    public async Task TheLogHasNoIdAndNoKey()
    {
        _consume = _ => throw new CodexRpcException(-32603, "internal error");
        await using var provider = CreateProvider();
        await provider.RefreshAsync(CancellationToken.None);

        await provider.RedeemAsync("credit-0001", "key-1", CancellationToken.None);

        Assert.Contains(_log.Messages, m => m.Contains("reset request", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Messages, m => m.Contains("credit-0001", StringComparison.Ordinal) || m.Contains("key-1", StringComparison.Ordinal));
    }

    [Fact]
    public void MapsAResultWithoutResetData()
    {
        using var document = JsonDocument.Parse("""{"rateLimits":{}}""");

        Assert.Null(CodexRateLimitMapper.MapResets(document.RootElement, Now));
    }

    [Fact]
    public void MapsACountWithoutDetails()
    {
        using var document = JsonDocument.Parse("""{"rateLimitResetCredits":{"availableCount":2,"credits":null}}""");

        var summary = CodexRateLimitMapper.MapResets(document.RootElement, Now)!;

        Assert.Equal(2, summary.AvailableCount);
        Assert.Null(summary.Resets);
        Assert.False(summary.ListsAllResets);
    }

    [Fact]
    public void AShortenedListDoesNotListAllResets()
    {
        using var document = JsonDocument.Parse("""
            {"rateLimitResetCredits":{"availableCount":5,"credits":[
              {"id":"a","resetType":"codexRateLimits","status":"available","grantedAt":1791413513,"expiresAt":null}]}}
            """);

        var summary = CodexRateLimitMapper.MapResets(document.RootElement, Now)!;

        Assert.Equal(5, summary.AvailableCount);
        Assert.False(summary.ListsAllResets);
        Assert.Null(Assert.Single(summary.Resets!).ExpiresAt);
        Assert.Equal("a", summary.NextResetId);
    }
}
