using System.Text.Json;
using Lidget.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidget.Core.Resets;

/// <summary>
/// Keeps the idempotency key of a redemption that has no final result (plan decision D61). The key is saved
/// before the request, so that a retry after a lost response or a restart cannot spend a second reset.
/// </summary>
public sealed class RedeemKeyStore
{
    private readonly string? _path;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PendingRedemption> _pending;

    /// <param name="path">The state file, or null to keep the keys in memory only.</param>
    public RedeemKeyStore(string? path, ILogger<RedeemKeyStore>? logger = null)
    {
        _path = path;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _pending = Load();
    }

    public static string DefaultPath => Path.Combine(AppInfo.DataDirectory, "reset-redemptions.json");

    /// <summary>
    /// Returns the saved key for this reset, or saves a new one. A key for another reset of the same provider
    /// is replaced.
    /// </summary>
    /// <returns>The key, and whether an earlier try saved it.</returns>
    public (string Key, bool WasPending) GetOrCreate(string providerId, string resetId)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(providerId, out var pending) && pending.ResetId == resetId)
            {
                return (pending.Key, true);
            }

            var key = Guid.NewGuid().ToString("D");
            _pending[providerId] = new PendingRedemption(resetId, key);
            Save();
            return (key, false);
        }
    }

    /// <summary>Removes the key after a final result.</summary>
    public void Clear(string providerId)
    {
        lock (_gate)
        {
            if (_pending.Remove(providerId))
            {
                Save();
            }
        }
    }

    private Dictionary<string, PendingRedemption> Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, PendingRedemption>>(File.ReadAllText(_path), SettingsStore.JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not read the saved redemption keys: {Error}", ex.Message);
            return [];
        }
    }

    /// <summary>A key that is not saved must not be used, so a failed save throws.</summary>
    private void Save()
    {
        if (_path is not null)
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_pending, SettingsStore.JsonOptions));
        }
    }

    private sealed record PendingRedemption(string ResetId, string Key);
}
