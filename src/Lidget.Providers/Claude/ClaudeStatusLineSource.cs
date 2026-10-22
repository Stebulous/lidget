using System.Text.Json;
using Lidget.Core.Models;
using Lidget.Core.StatusLine;

namespace Lidget.Providers.Claude;

/// <summary>
/// Reads the limit readings that <c>lidget-statusline.exe</c> records from Claude Code, and reports
/// new readings as the file changes.
/// </summary>
public sealed class ClaudeStatusLineSource : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    private readonly string _path;
    private readonly TimeProvider _time;
    private FileSystemWatcher? _watcher;
    private ITimer? _debounceTimer;

    public ClaudeStatusLineSource(string dataDirectory, TimeProvider time)
    {
        _path = StatusLineFiles.FeedPath(dataDirectory);
        _time = time;
    }

    /// <summary>Raised on a background thread with the current readings.</summary>
    public event Action<IReadOnlyList<LimitWindow>>? ReadingsChanged;

    public void StartWatching()
    {
        if (_watcher is not null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        _debounceTimer = _time.CreateTimer(_ => ReadingsChanged?.Invoke(Read()), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => _debounceTimer?.Change(Debounce, Timeout.InfiniteTimeSpan);

    /// <summary>Reads the recorded windows. Windows whose reset time has passed are left out.</summary>
    public IReadOnlyList<LimitWindow> Read()
    {
        string text;
        try
        {
            text = File.ReadAllText(_path);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return Map(document.RootElement, _time.GetUtcNow());
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<LimitWindow> Map(JsonElement record, DateTimeOffset now)
    {
        if (record.ValueKind != JsonValueKind.Object
            || !record.TryGetProperty("observedAt", out var observedMs) || observedMs.ValueKind != JsonValueKind.Number
            || !record.TryGetProperty("rate_limits", out var rateLimits) || rateLimits.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(observedMs.GetInt64());
        var result = new List<LimitWindow>();
        Add(result, rateLimits, "five_hour", ClaudeUsageMapper.SessionId, LimitKind.Session, "Session (5h)", observedAt, now);
        Add(result, rateLimits, "seven_day", ClaudeUsageMapper.WeeklyId, LimitKind.Weekly, "Weekly", observedAt, now);
        return result;
    }

    private static void Add(List<LimitWindow> result, JsonElement rateLimits, string name, string id, LimitKind kind, string label, DateTimeOffset observedAt, DateTimeOffset now)
    {
        if (!rateLimits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("used_percentage", out var used) || used.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        var resetsAt = ClaudeUsageMapper.ReadDate(window, "resets_at") ?? ClaudeUsageMapper.ReadDate(window, "reset_at");
        if (resetsAt is { } reset && reset <= now)
        {
            return; // The window reset after this reading, so the reading no longer applies.
        }

        result.Add(new LimitWindow(id, kind, label, used.GetDouble(), resetsAt, "statusline", observedAt));
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
    }
}
