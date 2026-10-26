using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lidget.Core;
using Lidget.Core.Models;
using Lidget.Core.StatusLine;
using Lidget.Core.Storage;
using Lidget.Providers.Claude;
using Microsoft.Extensions.Logging;

namespace Lidget.App.ViewModels;

public sealed record MetricOption(WidgetMetric Value, string Label);

public sealed record DisplayOption(UsageDisplay Value, string Label);

/// <summary>Edits <see cref="LidgetSettings"/>. Each change saves and applies at once.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly ClaudeStatusLineInstaller _statusLine;
    private readonly string _wrapperPath;
    private readonly ILogger _logger;
    private bool _loading;

    public SettingsViewModel(SettingsStore store, ClaudeStatusLineInstaller statusLine, string wrapperPath, ILogger logger)
    {
        _store = store;
        _statusLine = statusLine;
        _wrapperPath = wrapperPath;
        _logger = logger;
        Load();
    }

    public IReadOnlyList<MetricOption> MetricOptions { get; } =
    [
        new(WidgetMetric.Tightest, "Tightest limit"),
        new(WidgetMetric.Session, "Session (5-hour) limit"),
        new(WidgetMetric.Weekly, "Weekly limit"),
    ];

    public IReadOnlyList<DisplayOption> DisplayOptions { get; } =
    [
        new(UsageDisplay.Remaining, "Remaining, for example 88% left"),
        new(UsageDisplay.Used, "Used, for example 12% used"),
    ];

    public string Version { get; } = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public string DataDirectory => AppInfo.DataDirectory;

    public string ClaudeSettingsPath => _statusLine.SettingsPath;

    [ObservableProperty]
    public partial bool ClaudeEnabled { get; set; }

    [ObservableProperty]
    public partial bool CodexEnabled { get; set; }

    [ObservableProperty]
    public partial MetricOption? SelectedMetric { get; set; }

    [ObservableProperty]
    public partial DisplayOption? SelectedDisplay { get; set; }

    [ObservableProperty]
    public partial bool ShowPaceInTaskbar { get; set; }

    [ObservableProperty]
    public partial double RefreshIntervalMinutes { get; set; }

    [ObservableProperty]
    public partial string ThresholdsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ThresholdsError { get; set; }

    [ObservableProperty]
    public partial bool NotifyOnReset { get; set; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial double HistoryRetentionDays { get; set; }

    [ObservableProperty]
    public partial double WidgetTrayOffset { get; set; }

    [ObservableProperty]
    public partial bool ClaudeStatusLineFeed { get; set; }

    [ObservableProperty]
    public partial bool ClaudeT3CodeFeed { get; set; }

    [ObservableProperty]
    public partial string? StatusLineError { get; set; }

    private void Load()
    {
        _loading = true;
        var s = _store.Current;
        ClaudeEnabled = s.ClaudeEnabled;
        CodexEnabled = s.CodexEnabled;
        SelectedMetric = MetricOptions.First(o => o.Value == s.WidgetMetric);
        SelectedDisplay = DisplayOptions.First(o => o.Value == s.UsageDisplay);
        ShowPaceInTaskbar = s.ShowPaceInTaskbar;
        RefreshIntervalMinutes = s.RefreshIntervalMinutes;
        ThresholdsText = string.Join(", ", s.NotificationThresholds);
        NotifyOnReset = s.NotifyOnReset;
        StartWithWindows = s.StartWithWindows;
        HistoryRetentionDays = s.HistoryRetentionDays;
        WidgetTrayOffset = s.WidgetTrayOffset;
        ClaudeStatusLineFeed = s.ClaudeStatusLineFeed;
        ClaudeT3CodeFeed = s.ClaudeT3CodeFeed;
        _loading = false;
    }

    private void Save(Func<LidgetSettings, LidgetSettings> change)
    {
        if (!_loading)
        {
            _store.Update(change);
        }
    }

    partial void OnClaudeEnabledChanged(bool value) => Save(s => s with { ClaudeEnabled = value });

    partial void OnCodexEnabledChanged(bool value) => Save(s => s with { CodexEnabled = value });

    partial void OnSelectedMetricChanged(MetricOption? value)
    {
        if (value is not null)
        {
            Save(s => s with { WidgetMetric = value.Value });
        }
    }

    partial void OnSelectedDisplayChanged(DisplayOption? value)
    {
        if (value is not null)
        {
            Save(s => s with { UsageDisplay = value.Value });
        }
    }

    partial void OnShowPaceInTaskbarChanged(bool value) => Save(s => s with { ShowPaceInTaskbar = value });

    partial void OnRefreshIntervalMinutesChanged(double value) => Save(s => s with { RefreshIntervalMinutes = (int)Math.Round(value) });

    partial void OnNotifyOnResetChanged(bool value) => Save(s => s with { NotifyOnReset = value });

    partial void OnStartWithWindowsChanged(bool value) => Save(s => s with { StartWithWindows = value });

    partial void OnHistoryRetentionDaysChanged(double value) => Save(s => s with { HistoryRetentionDays = (int)Math.Round(value) });

    partial void OnWidgetTrayOffsetChanged(double value) => Save(s => s with { WidgetTrayOffset = value });

    partial void OnThresholdsTextChanged(string value)
    {
        var parts = value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
        var thresholds = new List<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part.TrimEnd('%'), NumberStyles.Integer, CultureInfo.CurrentCulture, out var number) || number is < 1 or > 100)
            {
                ThresholdsError = "Enter percentages from 1 to 100, separated by commas.";
                return;
            }

            thresholds.Add(number);
        }

        ThresholdsError = null;
        Save(s => s with { NotificationThresholds = thresholds });
    }

    partial void OnClaudeT3CodeFeedChanged(bool value) => Save(s => s with { ClaudeT3CodeFeed = value });

    partial void OnClaudeStatusLineFeedChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        try
        {
            if (value)
            {
                if (!File.Exists(_wrapperPath))
                {
                    throw new FileNotFoundException($"The statusline wrapper is missing: {_wrapperPath}");
                }

                _statusLine.Enable(_wrapperPath);
            }
            else
            {
                _statusLine.Disable();
            }

            StatusLineError = null;
            Save(s => s with { ClaudeStatusLineFeed = value });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Could not change the Claude Code statusline setting");
            StatusLineError = ex is FileNotFoundException
                ? $"Lidget cannot find {StatusLineFiles.WrapperFileName} next to Lidget.exe. Reinstall Lidget."
                : $"Could not change {_statusLine.SettingsPath}: {ex.Message}";
            _loading = true;
            ClaudeStatusLineFeed = !value;
            _loading = false;
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        Directory.CreateDirectory(AppInfo.DataDirectory);
        Process.Start(new ProcessStartInfo(AppInfo.DataDirectory) { UseShellExecute = true });
    }
}
