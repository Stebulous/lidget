using System.Text.Json;
using System.Text.Json.Serialization;
using Lidget.Core.Models;

namespace Lidget.Core.Storage;

public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private LidgetSettings _current;

    public SettingsStore(string path)
    {
        _path = path;
        _current = Load();
    }

    public static SettingsStore CreateDefault() => new(Path.Combine(AppInfo.DataDirectory, "settings.json"));

    public LidgetSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<LidgetSettings>? Changed;

    public void Save(LidgetSettings settings)
    {
        var normalized = settings.Normalize();
        lock (_gate)
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(normalized, JsonOptions));
            _current = normalized;
        }

        Changed?.Invoke(this, normalized);
    }

    public LidgetSettings Update(Func<LidgetSettings, LidgetSettings> change)
    {
        var updated = change(Current).Normalize();
        Save(updated);
        return updated;
    }

    private LidgetSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new LidgetSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<LidgetSettings>(File.ReadAllText(_path), JsonOptions);
            return (settings ?? new LidgetSettings()).Normalize();
        }
        catch (JsonException)
        {
            // Keep the unreadable file for diagnosis, and start again from the defaults.
            File.Copy(_path, _path + ".invalid", overwrite: true);
            return new LidgetSettings();
        }
    }
}
