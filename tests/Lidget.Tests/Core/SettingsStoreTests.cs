using Lidget.Core.Models;
using Lidget.Core.Storage;

namespace Lidget.Tests.Core;

public class SettingsStoreTests
{
    [Fact]
    public void MissingFileGivesDefaults()
    {
        using var dir = new TempDirectory();
        var store = new SettingsStore(dir.File("settings.json"));

        var settings = store.Current;
        Assert.True(settings.ClaudeEnabled);
        Assert.True(settings.CodexEnabled);
        Assert.Equal(5, settings.RefreshIntervalMinutes);
        Assert.Equal(WidgetMetric.Tightest, settings.WidgetMetric);
        Assert.Equal([80, 95], settings.NotificationThresholds);
        Assert.True(settings.StartWithWindows);
        Assert.Equal(90, settings.HistoryRetentionDays);
        Assert.False(settings.ClaudeStatusLineFeed);
        Assert.False(settings.NotifyOnReset);
    }

    [Fact]
    public void SavedSettingsRoundTrip()
    {
        using var dir = new TempDirectory();
        var path = dir.File("settings.json");
        var store = new SettingsStore(path);

        store.Save(store.Current with
        {
            CodexEnabled = false,
            RefreshIntervalMinutes = 10,
            WidgetMetric = WidgetMetric.Weekly,
            NotificationThresholds = [50, 90],
            ClaudeStatusLineFeed = true,
        });

        var reloaded = new SettingsStore(path).Current;
        Assert.False(reloaded.CodexEnabled);
        Assert.Equal(10, reloaded.RefreshIntervalMinutes);
        Assert.Equal(WidgetMetric.Weekly, reloaded.WidgetMetric);
        Assert.Equal([50, 90], reloaded.NotificationThresholds);
        Assert.True(reloaded.ClaudeStatusLineFeed);
        Assert.Contains("\"widgetMetric\": \"weekly\"", File.ReadAllText(path));
    }

    [Fact]
    public void SaveNormalizesOutOfRangeValues()
    {
        using var dir = new TempDirectory();
        var store = new SettingsStore(dir.File("settings.json"));

        store.Save(store.Current with { RefreshIntervalMinutes = 0, NotificationThresholds = [95, 0, 80, 80, 120] });

        Assert.Equal(LidgetSettings.MinRefreshMinutes, store.Current.RefreshIntervalMinutes);
        Assert.Equal([80, 95], store.Current.NotificationThresholds);
    }

    [Fact]
    public void InvalidFileGivesDefaultsAndKeepsCopy()
    {
        using var dir = new TempDirectory();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{ not json");

        var store = new SettingsStore(path);

        Assert.Equal(5, store.Current.RefreshIntervalMinutes);
        Assert.True(File.Exists(path + ".invalid"));
    }

    [Fact]
    public void SaveRaisesChanged()
    {
        using var dir = new TempDirectory();
        var store = new SettingsStore(dir.File("settings.json"));
        LidgetSettings? received = null;
        store.Changed += (_, s) => received = s;

        store.Update(s => s with { ClaudeEnabled = false });

        Assert.NotNull(received);
        Assert.False(received.ClaudeEnabled);
    }
}
