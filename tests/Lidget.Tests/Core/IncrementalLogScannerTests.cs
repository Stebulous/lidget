using System.Text;
using Lidget.Core.Costs;

namespace Lidget.Tests.Core;

public class IncrementalLogScannerTests
{
    public sealed class State;

    private sealed class Parser(Action? onParse = null) : LogParser<string, State>
    {
        public override State CreateState() => new();
        public override DateTimeOffset TimestampOf(string entry) => DateTimeOffset.UnixEpoch;

        public override void ParseLine(ReadOnlySpan<byte> line, State state, List<string> entries)
        {
            entries.Add(Encoding.UTF8.GetString(line));
            onParse?.Invoke();
        }
    }

    [Fact]
    public void CancellationWithinFileDoesNotCommitPartialProgress()
    {
        using var dir = new TempDirectory();
        using var cache = new CostCache(dir.File("cache.db"));
        using var cancellation = new CancellationTokenSource();
        var path = dir.File("session.jsonl");
        File.WriteAllText(path, "first\nsecond\n");
        var scanner = new IncrementalLogScanner<string, State>("test", cache, new Parser(cancellation.Cancel));

        Assert.Throws<OperationCanceledException>(() => scanner.Update([path], cancellation.Token));
        Assert.Empty(cache.GetFiles("test"));
        Assert.Empty(scanner.Load(DateTimeOffset.UnixEpoch));

        var resumed = new IncrementalLogScanner<string, State>("test", cache, new Parser());
        resumed.Update([path]);
        Assert.Equal(["first", "second"], resumed.Load(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void ReadsAcrossBufferBoundaryAndResumesIncompleteLine()
    {
        using var dir = new TempDirectory();
        using var cache = new CostCache(dir.File("cache.db"));
        var path = dir.File("session.jsonl");
        var longLine = new string('x', (1 << 20) + 17);
        File.WriteAllText(path, longLine + "\r\npartial");
        var scanner = new IncrementalLogScanner<string, State>("test", cache, new Parser());

        scanner.Update([path]);
        Assert.Equal(longLine, Assert.Single(scanner.Load(DateTimeOffset.UnixEpoch)));
        File.AppendAllText(path, " line\n");
        scanner.Update([path]);
        Assert.Equal([longLine, "partial line"], scanner.Load(DateTimeOffset.UnixEpoch));
    }
}
