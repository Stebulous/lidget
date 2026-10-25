using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidget.Core.Costs;

/// <summary>Turns the lines of one JSONL log format into usage entries.</summary>
/// <typeparam name="TState">Mutable state that carries context from one line to the next in a file.</typeparam>
public abstract class LogParser<TEntry, TState>
    where TState : class
{
    public abstract TState CreateState();

    /// <summary>A fast byte check. Lines that return false are skipped without JSON parsing.</summary>
    public virtual bool IsCandidate(ReadOnlySpan<byte> line) => true;

    public abstract void ParseLine(ReadOnlySpan<byte> line, TState state, List<TEntry> entries);

    public abstract DateTimeOffset TimestampOf(TEntry entry);
}

/// <summary>
/// Reads only the new complete lines of each log file and keeps the parsed entries in a
/// <see cref="CostCache"/>. A file that became shorter or has different first bytes is read again.
/// </summary>
public sealed class IncrementalLogScanner<TEntry, TState>(
    string source,
    CostCache cache,
    LogParser<TEntry, TState> parser,
    ILogger? logger = null)
    where TState : class
{
    private const int FingerprintBytes = 256;
    private const int BufferSize = 1 << 20;

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public string Source => source;

    /// <summary>Reads new lines from <paramref name="files"/> and forgets files that no longer exist.</summary>
    public void Update(IEnumerable<string> files, CancellationToken ct = default)
    {
        var known = cache.GetFiles(source);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            present.Add(path);
            try
            {
                UpdateFile(path, known.GetValueOrDefault(path), ct);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not read the log file {Path}", path);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Could not read the log file {Path}", path);
            }
        }

        var missing = known.Keys.Where(path => !present.Contains(path)).ToList();
        if (missing.Count > 0)
        {
            cache.RemoveFiles(source, missing);
        }
    }

    /// <summary>Returns the cached entries at or after <paramref name="since"/>, in path order and then file order.</summary>
    public IReadOnlyList<TEntry> Load(DateTimeOffset since)
    {
        var entries = new List<TEntry>();
        foreach (var cached in cache.GetEntries(source, since.ToUnixTimeMilliseconds()))
        {
            if (JsonSerializer.Deserialize<TEntry>(cached.Payload, CostJson.Options) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private void UpdateFile(string path, LogFileState? previous, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        var length = stream.Length;
        var lastWrite = File.GetLastWriteTimeUtc(path).Ticks;

        if (previous is not null && previous.Offset == length && previous.LastWriteTicks == lastWrite)
        {
            return;
        }

        var restart = previous is null
            || length < previous.Offset
            || Fingerprint(stream, Math.Min(previous.Offset, FingerprintBytes)) != previous.Fingerprint;

        var offset = restart ? 0 : previous!.Offset;
        var state = restart || previous!.ParserState is null
            ? parser.CreateState()
            : JsonSerializer.Deserialize<TState>(previous.ParserState, CostJson.Options) ?? parser.CreateState();

        var parsed = new List<TEntry>();
        var newOffset = ReadLines(stream, offset, length, state, parsed, ct);
        ct.ThrowIfCancellationRequested();

        var payloads = parsed
            .Select(entry => (parser.TimestampOf(entry).ToUnixTimeMilliseconds(), JsonSerializer.Serialize(entry, CostJson.Options)))
            .ToList();
        var fingerprint = Fingerprint(stream, Math.Min(newOffset, FingerprintBytes));
        cache.Update(
            source,
            path,
            new LogFileState(newOffset, lastWrite, fingerprint, JsonSerializer.Serialize(state, CostJson.Options)),
            payloads,
            restart);
    }

    /// <summary>Parses the complete lines between <paramref name="start"/> and <paramref name="end"/>. Returns the offset after the last newline.</summary>
    private long ReadLines(FileStream stream, long start, long end, TState state, List<TEntry> entries, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            return ReadLines(stream, start, end, state, entries, ct, buffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private long ReadLines(FileStream stream, long start, long end, TState state, List<TEntry> entries, CancellationToken ct, byte[] buffer)
    {
        stream.Position = start;
        var carry = new List<byte>();
        var consumed = start;
        var position = start;

        while (position < end)
        {
            ct.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, end - position));
            if (read <= 0)
            {
                break;
            }

            position += read;
            var span = buffer.AsSpan(0, read);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var newline = span.IndexOf((byte)'\n');
                if (newline < 0)
                {
                    carry.AddRange(span);
                    break;
                }

                if (carry.Count > 0)
                {
                    carry.AddRange(span[..newline]);
                    Parse(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(carry), state, entries);
                    consumed += carry.Count + 1;
                    carry.Clear();
                }
                else
                {
                    Parse(span[..newline], state, entries);
                    consumed += newline + 1;
                }

                span = span[(newline + 1)..];
            }
        }

        // A last line without a newline may still be in progress. The next scan reads it again.
        return consumed;
    }

    private void Parse(ReadOnlySpan<byte> line, TState state, List<TEntry> entries)
    {
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        if (line.IsEmpty || !parser.IsCandidate(line))
        {
            return;
        }

        try
        {
            parser.ParseLine(line, state, entries);
        }
        catch (JsonException)
        {
            // A malformed line is skipped, like the reference scanners do.
        }
    }

    private static string Fingerprint(FileStream stream, long count)
    {
        var bytes = new byte[count];
        stream.Position = 0;
        stream.ReadExactly(bytes);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

internal static class CostJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Text(ReadOnlySpan<byte> utf8) => Encoding.UTF8.GetString(utf8);
}
