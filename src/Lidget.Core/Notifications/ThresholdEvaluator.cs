using System.Text.Json;
using Lidget.Core.Formatting;
using Lidget.Core.Models;
using Lidget.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidget.Core.Notifications;

/// <summary>A notification to show. <see cref="Tag"/> identifies the limit, so that a newer notice replaces an older one.</summary>
public sealed record UsageNotification(string Title, string Body, string Tag);

/// <summary>
/// Decides which limit readings cross a notification threshold (plan decision D28).
/// Each threshold notifies one time for each limit window. A limit window is the pair of a limit ID and its
/// reset time. The state is saved to a file, so that a restart does not repeat a notification.
/// </summary>
public sealed class ThresholdEvaluator
{
    /// <summary>
    /// Sources report the same reset time with small differences. A later reset time counts as a new
    /// window only when it is more than this amount later.
    /// </summary>
    public static readonly TimeSpan SameWindowTolerance = TimeSpan.FromMinutes(30);

    /// <summary>State for windows that reset this long ago is removed.</summary>
    public static readonly TimeSpan StateRetention = TimeSpan.FromDays(14);

    private readonly string? _statePath;
    private readonly Func<LidgetSettings> _settings;
    private readonly Func<string, string> _displayName;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, WindowState> _windows;

    /// <param name="statePath">The state file, or null to keep the state in memory only.</param>
    public ThresholdEvaluator(
        string? statePath,
        Func<LidgetSettings> settings,
        Func<string, string> displayName,
        TimeProvider time,
        ILogger<ThresholdEvaluator>? logger = null)
    {
        _statePath = statePath;
        _settings = settings;
        _displayName = displayName;
        _time = time;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _windows = Load();
    }

    public static string DefaultStatePath => Path.Combine(AppInfo.DataDirectory, "notifications.json");

    /// <summary>Returns the notifications for a new snapshot, and saves the new state.</summary>
    public IReadOnlyList<UsageNotification> Evaluate(ProviderSnapshot snapshot)
    {
        if (snapshot.Status is not ProviderStatus.Ok)
        {
            return [];
        }

        var settings = _settings();
        var now = _time.GetUtcNow();
        var notifications = new List<UsageNotification>();
        var changed = false;

        lock (_gate)
        {
            foreach (var limit in snapshot.Limits)
            {
                var key = $"{snapshot.ProviderId}/{limit.Id}";
                var tag = $"{snapshot.ProviderId}.{limit.Id}";
                var previous = _windows.GetValueOrDefault(key);
                var state = previous;

                if (state is null || IsNewWindow(state, limit.ResetsAt))
                {
                    if (state is { Notified: > 0 } && settings.NotifyOnReset)
                    {
                        notifications.Add(new UsageNotification(
                            $"{_displayName(snapshot.ProviderId)}: {limit.Label} reset",
                            $"Usage is now {UsageFormat.Percent(limit.UsedPercent)}. {UsageFormat.ResetText(limit.ResetsAt, now)}.",
                            tag));
                    }

                    state = new WindowState(limit.ResetsAt, 0);
                }
                else if (state.ResetsAt is null && limit.ResetsAt is not null)
                {
                    state = state with { ResetsAt = limit.ResetsAt };
                }

                var crossed = settings.NotificationThresholds.Where(t => limit.UsedPercent >= t).DefaultIfEmpty(0).Max();
                if (crossed > state.Notified)
                {
                    notifications.Add(ThresholdNotice(snapshot.ProviderId, limit, crossed, now, tag));
                    state = state with { Notified = crossed };
                }

                if (state != previous)
                {
                    _windows[key] = state;
                    changed = true;
                }
            }

            changed |= Prune(now);
            if (changed)
            {
                Save();
            }
        }

        return notifications;
    }

    private UsageNotification ThresholdNotice(string providerId, LimitWindow limit, int threshold, DateTimeOffset now, string tag)
    {
        var name = _displayName(providerId);
        var reset = UsageFormat.ResetText(limit.ResetsAt, now);
        return limit.UsedPercent >= 100
            ? new UsageNotification($"{name}: {limit.Label} limit reached", $"{reset}.", tag)
            : new UsageNotification(
                $"{name}: {limit.Label} at {UsageFormat.Percent(limit.UsedPercent)}",
                $"Usage passed {threshold}%. {reset}.",
                tag);
    }

    private static bool IsNewWindow(WindowState state, DateTimeOffset? resetsAt) =>
        resetsAt is { } reset && state.ResetsAt is { } known && reset - known > SameWindowTolerance;

    private bool Prune(DateTimeOffset now)
    {
        var expired = _windows
            .Where(pair => pair.Value.ResetsAt is { } reset && now - reset > StateRetention)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in expired)
        {
            _windows.Remove(key);
        }

        return expired.Count > 0;
    }

    private Dictionary<string, WindowState> Load()
    {
        if (_statePath is null || !File.Exists(_statePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, WindowState>>(File.ReadAllText(_statePath), SettingsStore.JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Without the state, a notification can show again. That is better than no notifications.
            _logger.LogWarning(ex, "Could not read the notification state");
            return [];
        }
    }

    private void Save()
    {
        if (_statePath is null)
        {
            return;
        }

        try
        {
            AtomicFile.WriteAllText(_statePath, JsonSerializer.Serialize(_windows, SettingsStore.JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the notification state");
        }
    }

    /// <summary>The reset time of the last known window, and the highest threshold notified in it.</summary>
    private sealed record WindowState(DateTimeOffset? ResetsAt, int Notified);
}
