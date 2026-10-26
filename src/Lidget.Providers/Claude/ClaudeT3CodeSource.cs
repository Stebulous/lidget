using System.Globalization;
using System.Text;
using System.Text.Json;
using Lidget.Core.Models;

namespace Lidget.Providers.Claude;

/// <summary>
/// Reads the Claude limits that T3 Code logs. T3 Code runs Claude through the Claude Agent SDK, which does
/// not run the statusline command. After each response, the SDK reports the account's session and weekly
/// utilization in a <c>rate_limit_event</c>, and T3 Code writes each event to its provider logs.
/// Lidget reads only those events and ignores the other log content.
/// </summary>
public sealed class ClaudeT3CodeSource : IClaudeLimitFeed, IDisposable
{
    public const string SourceName = "t3code";

    /// <summary>The most that one scan reads from a file, from its end.</summary>
    internal const int MaxReadBytes = 2 * 1024 * 1024;

    /// <summary>Files that did not change for this long do not have current readings.</summary>
    private static readonly TimeSpan MaxFileAge = TimeSpan.FromDays(1);

    /// <summary>T3 Code writes its logs often. Scan at most one time in this interval.</summary>
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(2);

    private static readonly byte[] EventMarker = "\"rate_limit_event\""u8.ToArray();

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, FilePosition> _positions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LimitWindow> _latest = new(StringComparer.Ordinal);
    private FileSystemWatcher? _watcher;
    private ITimer? _throttleTimer;
    private int _scanPending;

    public ClaudeT3CodeSource(string directory, TimeProvider time)
    {
        _directory = directory;
        _time = time;
    }

    /// <summary>The T3 Code provider log folder: <c>~/.t3/userdata/logs/provider</c>.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".t3", "userdata", "logs", "provider");

    public event Action<IReadOnlyList<LimitWindow>>? ReadingsChanged;

    /// <summary>Watches the log folder, if it exists. Call <see cref="StopWatching"/> to stop.</summary>
    public void StartWatching()
    {
        lock (_gate)
        {
            if (_watcher is not null || !Directory.Exists(_directory))
            {
                return;
            }

            _throttleTimer ??= _time.CreateTimer(_ => OnThrottleElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _watcher = new FileSystemWatcher(_directory, "events.*.log")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.EnableRaisingEvents = true;
        }
    }

    public void StopWatching()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    public IReadOnlyList<LimitWindow> Read()
    {
        Scan();
        return Current(_time.GetUtcNow());
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Throttle, not debounce: T3 Code can write for minutes without a pause.
        if (Interlocked.Exchange(ref _scanPending, 1) == 0)
        {
            _throttleTimer?.Change(Throttle, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnThrottleElapsed()
    {
        Volatile.Write(ref _scanPending, 0);
        if (Scan())
        {
            ReadingsChanged?.Invoke(Current(_time.GetUtcNow()));
        }
    }

    private IReadOnlyList<LimitWindow> Current(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _latest.Values
                .Where(l => l.ResetsAt is not { } reset || reset > now)
                .OrderBy(l => l.Kind)
                .ToArray();
        }
    }

    /// <summary>Reads the new part of each recent log file. Returns true when a newer reading arrived.</summary>
    private bool Scan()
    {
        FileInfo[] files;
        try
        {
            var cutoff = _time.GetUtcNow().UtcDateTime - MaxFileAge;
            files = new DirectoryInfo(_directory).GetFiles("events.*.log")
                .Where(f => f.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && f.LastWriteTimeUtc >= cutoff)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var changed = false;
        lock (_gate)
        {
            foreach (var file in files)
            {
                changed |= ScanFile(file);
            }
        }

        return changed;
    }

    private bool ScanFile(FileInfo file)
    {
        var known = _positions.GetValueOrDefault(file.FullName);
        var created = file.CreationTimeUtc;

        // T3 Code renames a full log to .log.1 and starts a new file with the same name.
        var start = known is not null && known.Created == created && known.Offset <= file.Length ? known.Offset : 0;
        if (start == file.Length)
        {
            return false;
        }

        start = Math.Max(start, file.Length - MaxReadBytes);
        byte[] buffer;
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = start;
            buffer = new byte[(int)(stream.Length - start)];
            stream.ReadExactly(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var changed = false;
        var span = buffer.AsSpan();
        var lineStart = 0;

        // A read that starts inside a line skips that partial line.
        if (start > 0 && (known is null || start != known.Offset))
        {
            var first = span.IndexOf((byte)'\n');
            lineStart = first < 0 ? span.Length : first + 1;
        }

        while (lineStart < span.Length)
        {
            var length = span[lineStart..].IndexOf((byte)'\n');
            if (length < 0)
            {
                break; // T3 Code is still writing this line. Read it at the next scan.
            }

            var line = span.Slice(lineStart, length);
            if (line.IndexOf(EventMarker) >= 0)
            {
                foreach (var window in ParseLine(line, _time.GetUtcNow()))
                {
                    if (!_latest.TryGetValue(window.Id, out var existing) || window.ObservedAt > existing.ObservedAt)
                    {
                        _latest[window.Id] = window;
                        changed = true;
                    }
                }
            }

            lineStart += length + 1;
        }

        _positions[file.FullName] = new FilePosition(created, start + lineStart);
        return changed;
    }

    /// <summary>
    /// Parses one log line: <c>[2026-10-23T03:52:27.364Z] NTIVE: {...}</c>, where the JSON object contains a
    /// <c>rate_limit_info</c> object at some depth.
    /// </summary>
    internal static IReadOnlyList<LimitWindow> ParseLine(ReadOnlySpan<byte> line, DateTimeOffset now)
    {
        var json = line.IndexOf((byte)'{');
        if (json < 0 || !TryReadTimestamp(line, out var observedAt))
        {
            return [];
        }

        try
        {
            var reader = new Utf8JsonReader(line[json..]);
            using var document = JsonDocument.ParseValue(ref reader);
            return FindRateLimitInfo(document.RootElement, depth: 0) is { } info ? Map(info, observedAt, now) : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Maps a Claude Agent SDK <c>rate_limit_info</c> object. Utilization is a share from 0 to 1.</summary>
    internal static IReadOnlyList<LimitWindow> Map(JsonElement info, DateTimeOffset observedAt, DateTimeOffset now)
    {
        var result = new List<LimitWindow>();
        if (info.TryGetProperty("unifiedWindows", out var windows) && windows.ValueKind == JsonValueKind.Object)
        {
            Add(result, windows, "five_hour", observedAt, now);
            Add(result, windows, "seven_day", observedAt, now);
        }
        else if (info.TryGetProperty("rateLimitType", out var type) && type.ValueKind == JsonValueKind.String)
        {
            // Older SDK versions report only the window that is closest to its limit.
            AddWindow(result, type.GetString(), info, observedAt, now);
        }

        return result;
    }

    private static void Add(List<LimitWindow> result, JsonElement windows, string name, DateTimeOffset observedAt, DateTimeOffset now)
    {
        if (windows.TryGetProperty(name, out var window) && window.ValueKind == JsonValueKind.Object)
        {
            AddWindow(result, name, window, observedAt, now);
        }
    }

    private static void AddWindow(List<LimitWindow> result, string? name, JsonElement window, DateTimeOffset observedAt, DateTimeOffset now)
    {
        (string Id, LimitKind Kind, string Label)? target = name switch
        {
            "five_hour" => (ClaudeUsageMapper.SessionId, LimitKind.Session, "Session (5h)"),
            "seven_day" => (ClaudeUsageMapper.WeeklyId, LimitKind.Weekly, "Weekly"),
            _ => null,
        };
        if (target is not { } t
            || !window.TryGetProperty("utilization", out var utilization) || utilization.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        var resetsAt = ClaudeUsageMapper.ReadDate(window, "resetsAt");
        if (resetsAt is { } reset && reset <= now)
        {
            return; // The window reset after this reading, so the reading no longer applies.
        }

        var percent = Math.Round(utilization.GetDouble() * 100, 2);
        result.Add(new LimitWindow(t.Id, t.Kind, t.Label, percent, resetsAt, SourceName, observedAt)
        {
            Duration = t.Kind == LimitKind.Session ? LimitWindow.SessionDuration : LimitWindow.WeeklyDuration,
        });
    }

    private static JsonElement? FindRateLimitInfo(JsonElement element, int depth)
    {
        if (element.ValueKind != JsonValueKind.Object || depth > 6)
        {
            return null;
        }

        // Only the event object counts. Message text and tool input can name the same fields.
        if (element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.ValueEquals("rate_limit_event")
            && element.TryGetProperty("rate_limit_info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            return info;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (FindRateLimitInfo(property.Value, depth + 1) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static bool TryReadTimestamp(ReadOnlySpan<byte> line, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var end = line.IndexOf((byte)']');
        if (line.Length < 2 || line[0] != (byte)'[' || end < 2)
        {
            return false;
        }

        return DateTimeOffset.TryParse(
            Encoding.UTF8.GetString(line[1..end]),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    public void Dispose()
    {
        StopWatching();
        _throttleTimer?.Dispose();
    }

    private sealed record FilePosition(DateTime Created, long Offset);
}
