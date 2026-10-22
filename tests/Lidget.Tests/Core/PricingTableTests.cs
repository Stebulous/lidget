using System.Text;
using Lidget.Core.Costs;

namespace Lidget.Tests.Core;

public class PricingTableTests
{
    private static PricingTable Table() => PricingTable.Load(new MemoryStream(Encoding.UTF8.GetBytes("""
        {
          "source": "test",
          "models": {
            "claude-opus-5-5": { "input": 4, "output": 20, "cacheWrite": 5, "cacheRead": 0.2, "fast": 2 },
            "claude-sonnet-4-20250514": { "input": 3, "output": 15, "cacheWrite": 3.75, "cacheRead": 0.3,
              "longContext": { "threshold": 200000, "input": 6, "output": 22.5, "cacheWrite": 7.5, "cacheRead": 0.6 } },
            "claude-haiku-4-5": { "input": 1, "output": 5 },
            "gpt-6-sol": { "input": 2, "output": 10, "cacheWrite": 2.5, "cacheRead": 0.2, "fast": 2 }
          },
          "aliases": [ { "pattern": "^claude-5[.-]5-opus$", "canonical": "claude-opus-5-5" } ]
        }
        """)));

    [Theory]
    [InlineData("claude-opus-5-5", 4)]
    [InlineData("claude-5.5-opus", 4)]
    [InlineData("claude-opus-5-5-fast", 8)]
    [InlineData("anthropic/claude-opus-5-5", 4)]
    [InlineData("claude-opus-5-5-20261001", 4)]
    [InlineData("claude-sonnet-4", 3)]
    [InlineData("claude-sonnet-4-thinking", 3)]
    [InlineData("claude-haiku-4-5-20251001", 1)]
    public void ResolvesModelNames(string model, double input)
    {
        Assert.Equal(input, Table().Resolve(model)?.Input);
    }

    [Theory]
    [InlineData("claude-haiku-4-5-fast")]
    [InlineData("claude-opus-5")]
    [InlineData("gpt-daybreak-blue-latest")]
    [InlineData("gpt-6-solar")]
    public void LeavesUnknownModelsUnpriced(string model)
    {
        Assert.Null(Table().Resolve(model));
    }

    [Fact]
    public void MissingCacheRatesUseTheInputRate()
    {
        var haiku = Table().Resolve("claude-haiku-4-5")!;
        Assert.Equal(1, haiku.CacheWrite);
        Assert.Equal(1, haiku.CacheRead);
    }

    [Fact]
    public void CostAddsEachTokenBucketAtItsRate()
    {
        var opus = Table().Resolve("claude-opus-5-5")!;
        var tokens = new TokenUsage(Input: 1_000_000, CacheWrite5m: 1_000_000, CacheWrite1h: 1_000_000, CacheRead: 1_000_000, Output: 1_000_000);

        // 4 input + 5 cache write + 8 one-hour cache write (2x input) + 0.2 cache read + 20 output.
        Assert.Equal(37.2, opus.Cost(tokens), 6);
        Assert.Equal(74.4, opus.Cost(tokens, fast: true), 6);
    }

    [Fact]
    public void LongContextRatesApplyToTheWholeRequestAboveTheThreshold()
    {
        var sonnet = Table().Resolve("claude-sonnet-4")!;

        Assert.Equal((200_000 * 3 / 1e6) + (1000 * 15 / 1e6), sonnet.Cost(new TokenUsage(200_000, 0, 0, 0, 1000)), 9);
        Assert.Equal((200_001 * 6 / 1e6) + (1000 * 22.5 / 1e6), sonnet.Cost(new TokenUsage(200_001, 0, 0, 0, 1000)), 9);
    }

    [Fact]
    public void BundledTableLoadsAndPricesCurrentModels()
    {
        var table = PricingTable.LoadDefault();

        Assert.NotNull(table.Source);
        Assert.NotNull(table.Resolve("claude-opus-5-5"));
        Assert.NotNull(table.Resolve("claude-sonnet-5-5"));
        Assert.NotNull(table.Resolve("claude-haiku-4-5-20251001"));
        Assert.NotNull(table.Resolve("gpt-6.1-sol"));
        Assert.NotNull(table.Resolve("gpt-6-luna"));
        Assert.Equal(6, table.Resolve("gpt-6-astra")!.Ultrafast);
        Assert.Equal(272_000, table.Resolve("gpt-5.5")!.LongContext!.Threshold);
    }
}
