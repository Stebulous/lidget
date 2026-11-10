using Lidget.Core.Formatting;
using Lidget.Core.Models;
using Lidget.Core.Providers;
using Lidget.Core.Resets;
using Lidget.Core.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Lidget.Tests.Core;

public class ResetRedemptionServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly HistoryStore _store;
    private readonly ResetTracker _tracker;
    private readonly FakeRedeemer _codex = new(ProviderIds.Codex);

    public ResetRedemptionServiceTests()
    {
        _store = new HistoryStore(_dir.File("history.db"));
        _tracker = new ResetTracker(_store, _time);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private string KeyPath => _dir.File("reset-redemptions.json");

    private ResetRedemptionService Service(string? keyPath = null) =>
        new([_codex, new PlainProvider()], new RedeemKeyStore(keyPath ?? KeyPath), _tracker);

    private IReadOnlyList<ResetEvent> Events() => _store.QueryResetEvents(ProviderIds.Codex, 50);

    [Theory]
    [InlineData(RedeemOutcome.Reset)]
    [InlineData(RedeemOutcome.NothingToReset)]
    [InlineData(RedeemOutcome.NoReset)]
    [InlineData(RedeemOutcome.AlreadyRedeemed)]
    [InlineData(RedeemOutcome.CoolingDown)]
    [InlineData(RedeemOutcome.RateLimited)]
    [InlineData(RedeemOutcome.NeedsLogin)]
    public async Task AFinalResultDeletesTheKey(RedeemOutcome outcome)
    {
        _codex.Outcomes.Enqueue(outcome);
        _codex.Outcomes.Enqueue(outcome);
        var service = Service();
        var completed = new List<string>();
        service.Completed += (_, id) => completed.Add(id);

        Assert.Equal(outcome, await service.RedeemAsync(ProviderIds.Codex, "a"));
        Assert.Equal(outcome, await service.RedeemAsync(ProviderIds.Codex, "a"));

        Assert.NotEqual(_codex.Calls[0].Key, _codex.Calls[1].Key);
        Assert.Equal([ProviderIds.Codex, ProviderIds.Codex], completed);
        Assert.DoesNotContain("\"key\"", File.ReadAllText(KeyPath));
    }

    [Theory]
    [InlineData(RedeemOutcome.Unconfirmed)]
    [InlineData(RedeemOutcome.Failed)]
    public async Task AResultThatIsNotFinalKeepsTheKeyForTheNextTry(RedeemOutcome outcome)
    {
        _codex.Outcomes.Enqueue(outcome);
        _codex.Outcomes.Enqueue(RedeemOutcome.Reset);
        var service = Service();
        var completed = 0;
        service.Completed += (_, _) => completed++;

        Assert.Equal(outcome, await service.RedeemAsync(ProviderIds.Codex, "a"));
        Assert.Equal(0, completed);
        Assert.Equal(RedeemOutcome.Reset, await service.RedeemAsync(ProviderIds.Codex, "a"));

        Assert.Equal(_codex.Calls[0].Key, _codex.Calls[1].Key);
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task ARestartReadsTheSavedKeyAgain()
    {
        _codex.Outcomes.Enqueue(RedeemOutcome.Unconfirmed);
        _codex.Outcomes.Enqueue(RedeemOutcome.AlreadyRedeemed);
        await Service().RedeemAsync(ProviderIds.Codex, "a");

        // A new service, as after a restart, reads the key from the file.
        var outcome = await Service().RedeemAsync(ProviderIds.Codex, "a");

        Assert.Equal(RedeemOutcome.AlreadyRedeemed, outcome);
        Assert.Equal(_codex.Calls[0].Key, _codex.Calls[1].Key);
        Assert.True(Guid.TryParse(_codex.Calls[0].Key, out _));
        // The earlier try spent the reset, so the history shows a redemption in Lidget.
        Assert.Equal(ResetEventKind.Redeemed, Assert.Single(Events()).Kind);
    }

    [Fact]
    public async Task AnExceptionKeepsTheKey()
    {
        _codex.Throw = new InvalidOperationException("boom");
        var service = Service();

        Assert.Equal(RedeemOutcome.Failed, await service.RedeemAsync(ProviderIds.Codex, "a"));
        _codex.Throw = null;
        _codex.Outcomes.Enqueue(RedeemOutcome.Reset);
        await service.RedeemAsync(ProviderIds.Codex, "a");

        Assert.Equal(_codex.Calls[0].Key, _codex.Calls[1].Key);
    }

    [Fact]
    public async Task AnotherResetGetsANewKey()
    {
        _codex.Outcomes.Enqueue(RedeemOutcome.Unconfirmed);
        _codex.Outcomes.Enqueue(RedeemOutcome.Unconfirmed);
        var service = Service();

        await service.RedeemAsync(ProviderIds.Codex, "a");
        await service.RedeemAsync(ProviderIds.Codex, "b");

        Assert.NotEqual(_codex.Calls[0].Key, _codex.Calls[1].Key);
    }

    [Fact]
    public async Task ASuccessfulRedemptionIsRecordedInTheHistory()
    {
        _codex.Outcomes.Enqueue(RedeemOutcome.Reset);

        await Service().RedeemAsync(ProviderIds.Codex, "a");

        var redeemed = Assert.Single(Events());
        Assert.Equal(ResetEventKind.Redeemed, redeemed.Kind);
        Assert.Equal("a", redeemed.ResetId);
    }

    [Theory]
    [InlineData(RedeemOutcome.AlreadyRedeemed)]
    [InlineData(RedeemOutcome.NothingToReset)]
    [InlineData(RedeemOutcome.NoReset)]
    public async Task OtherResultsOfAFirstTryAreNotRecordedAsARedemption(RedeemOutcome outcome)
    {
        _codex.Outcomes.Enqueue(outcome);

        await Service().RedeemAsync(ProviderIds.Codex, "a");

        Assert.Empty(Events());
    }

    [Fact]
    public async Task ASecondRedemptionForTheProviderWaitsForTheFirst()
    {
        var gate = new TaskCompletionSource<RedeemOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _codex.Pending = gate;
        var service = Service();

        var first = service.RedeemAsync(ProviderIds.Codex, "a");
        var second = service.RedeemAsync(ProviderIds.Codex, "b");
        await Task.Delay(50);

        Assert.Single(_codex.Calls);
        Assert.False(second.IsCompleted);

        _codex.Pending = null;
        _codex.Outcomes.Enqueue(RedeemOutcome.NothingToReset);
        gate.SetResult(RedeemOutcome.Reset);

        Assert.Equal(RedeemOutcome.Reset, await first);
        Assert.Equal(RedeemOutcome.NothingToReset, await second);
        Assert.Equal(2, _codex.Calls.Count);
    }

    [Fact]
    public async Task WithoutASavedKeyNoRequestIsSent()
    {
        // The key file cannot be written, because its folder is a file.
        File.WriteAllText(_dir.File("blocked"), string.Empty);

        var outcome = await Service(Path.Combine(_dir.File("blocked"), "keys.json")).RedeemAsync(ProviderIds.Codex, "a");

        Assert.Equal(RedeemOutcome.Failed, outcome);
        Assert.Empty(_codex.Calls);
    }

    [Fact]
    public async Task AProviderThatCannotRedeemFails()
    {
        var service = Service();

        Assert.True(service.CanRedeem(ProviderIds.Codex));
        Assert.False(service.CanRedeem(ProviderIds.Claude));
        Assert.Equal(RedeemOutcome.Failed, await service.RedeemAsync(ProviderIds.Claude, "a"));
    }

    [Fact]
    public void EachResultHasAMessage()
    {
        var texts = Enum.GetValues<RedeemOutcome>().Select(o => ResetFormat.OutcomeText(o, "Codex")).ToArray();

        Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(texts.Length, texts.Distinct().Count());
        Assert.Equal("No limit can be reset now. The reset is still available.", ResetFormat.OutcomeText(RedeemOutcome.NothingToReset, "Codex"));
    }

    private sealed class FakeRedeemer(string id) : IUsageProvider, IResetRedeemer
    {
        public Queue<RedeemOutcome> Outcomes { get; } = new();

        public List<(string ResetId, string Key)> Calls { get; } = [];

        public Exception? Throw { get; set; }

        public TaskCompletionSource<RedeemOutcome>? Pending { get; set; }

        public string Id => id;

        public string DisplayName => id;

#pragma warning disable CS0067 // The test does not push snapshots.
        public event EventHandler<ProviderSnapshot>? SnapshotPushed;
#pragma warning restore CS0067

        public Task<ProviderSnapshot> RefreshAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<RedeemOutcome> RedeemAsync(string resetId, string idempotencyKey, CancellationToken ct)
        {
            Calls.Add((resetId, idempotencyKey));
            if (Throw is not null)
            {
                throw Throw;
            }

            return Pending?.Task ?? Task.FromResult(Outcomes.Dequeue());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PlainProvider : IUsageProvider
    {
        public string Id => ProviderIds.Claude;

        public string DisplayName => "Claude";

#pragma warning disable CS0067 // The test does not push snapshots.
        public event EventHandler<ProviderSnapshot>? SnapshotPushed;
#pragma warning restore CS0067

        public Task<ProviderSnapshot> RefreshAsync(CancellationToken ct) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
