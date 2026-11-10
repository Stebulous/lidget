using System.Text.Json;
using Lidget.Core.Formatting;
using Lidget.Core.Models;
using Lidget.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidget.Core.Notifications;

/// <summary>
/// Decides when to warn that a banked reset expires (plan decision D64): one time 24 hours before the expiry
/// time, and one time 2 hours before. The state is saved to a file, so that a restart does not repeat a
/// warning. A reset that Lidget sees for the first time after a warning time gets no notice for that time.
/// </summary>
public sealed class ResetExpiryEvaluator
{
    public static readonly TimeSpan FirstWarning = TimeSpan.FromHours(24);
    public static readonly TimeSpan LastWarning = TimeSpan.FromHours(2);

    /// <summary>State for resets that expired this long ago is removed.</summary>
    public static readonly TimeSpan StateRetention = TimeSpan.FromDays(1);

    private readonly string? _statePath;
    private readonly Func<LidgetSettings> _settings;
    private readonly Func<string, string> _displayName;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, WarningState> _resets;

    /// <param name="statePath">The state file, or null to keep the state in memory only.</param>
    public ResetExpiryEvaluator(
        string? statePath,
        Func<LidgetSettings> settings,
        Func<string, string> displayName,
        TimeProvider time,
        ILogger<ResetExpiryEvaluator>? logger = null)
    {
        _statePath = statePath;
        _settings = settings;
        _displayName = displayName;
        _time = time;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _resets = Load();
    }

    public static string DefaultStatePath => Path.Combine(AppInfo.DataDirectory, "reset-warnings.json");

    /// <summary>Returns the warning for a new snapshot, and saves the new state. One snapshot gives one notice at most.</summary>
    public IReadOnlyList<UsageNotification> Evaluate(ProviderSnapshot snapshot)
    {
        if (snapshot.Resets?.Resets is not { } resets)
        {
            return [];
        }

        var enabled = _settings().WarnBeforeResetExpires;
        var now = _time.GetUtcNow();
        var due = new List<(BankedReset Reset, TimeSpan Remaining)>();
        var changed = false;

        lock (_gate)
        {
            foreach (var reset in resets)
            {
                if (!reset.IsUsable || reset.Count <= 0 || reset.ExpiresAt is not { } expires || expires <= now)
                {
                    continue;
                }

                var remaining = expires - now;
                var stage = remaining <= LastWarning ? 2 : remaining <= FirstWarning ? 1 : 0;
                var key = $"{snapshot.ProviderId}/{reset.Id}";
                if (!_resets.TryGetValue(key, out var state))
                {
                    // Lidget did not know this reset at the earlier warning times, so it gives no notice for them.
                    _resets[key] = new WarningState(expires, stage);
                    changed = true;
                }
                else if (stage > state.Stage)
                {
                    _resets[key] = state with { ExpiresAt = expires, Stage = stage };
                    changed = true;
                    if (enabled)
                    {
                        due.Add((reset, remaining));
                    }
                }
            }

            changed |= Prune(now);
            if (changed)
            {
                Save();
            }
        }

        if (due.Count == 0)
        {
            return [];
        }

        var (first, left) = due.MinBy(d => d.Remaining);
        var name = _displayName(snapshot.ProviderId);
        var title = $"{name}: a reset expires in {UsageFormat.Countdown(left)}";
        var body = due.Count == 1
            ? $"{ResetFormat.Title(first.Title)}. Use it before it expires."
            : $"{ResetFormat.Title(first.Title)}, and {ResetFormat.Count(due.Count - 1)} more. Use them before they expire.";
        return [new UsageNotification(title, body, $"{snapshot.ProviderId}.reset-expiry")];
    }

    private bool Prune(DateTimeOffset now)
    {
        var expired = _resets.Where(pair => now - pair.Value.ExpiresAt > StateRetention).Select(pair => pair.Key).ToList();
        foreach (var key in expired)
        {
            _resets.Remove(key);
        }

        return expired.Count > 0;
    }

    private Dictionary<string, WarningState> Load()
    {
        if (_statePath is null || !File.Exists(_statePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, WarningState>>(File.ReadAllText(_statePath), SettingsStore.JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Without the state, a warning can show again. That is better than no warnings.
            _logger.LogWarning(ex, "Could not read the reset warning state");
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
            AtomicFile.WriteAllText(_statePath, JsonSerializer.Serialize(_resets, SettingsStore.JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the reset warning state");
        }
    }

    /// <summary>The expiry time of a reset, and the last warning that Lidget gave or skipped: 0, 1 (24 hours), or 2 (2 hours).</summary>
    private sealed record WarningState(DateTimeOffset ExpiresAt, int Stage);
}
