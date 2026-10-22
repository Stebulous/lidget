using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using Lidget.Core.Models;
using Lidget.Core.Storage;
using SkiaSharp;
using Wpf.Ui.Appearance;

namespace Lidget.App.ViewModels;

/// <summary>One entry of the chart legend.</summary>
public sealed record HistoryLegendItem(string Text, Brush Stroke, bool Dashed);

/// <summary>Usage history chart for the last 24 hours or 7 days (plan task T3.2).</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    /// <summary>A longer pause between readings breaks the line, so that missing data is visible.</summary>
    private static readonly TimeSpan GapThreshold = TimeSpan.FromMinutes(45);

    private readonly HistoryStore _store;
    private readonly TimeProvider _time;
    private readonly Func<string, string> _providerName;
    private readonly Dispatcher _dispatcher;
    private int _loadVersion;

    public HistoryViewModel(HistoryStore store, TimeProvider time, Func<string, string> providerName)
    {
        _store = store;
        _time = time;
        _providerName = providerName;
        _dispatcher = Dispatcher.CurrentDispatcher;
        ApplicationThemeManager.Changed += (_, _) => Reload();
    }

    [ObservableProperty]
    public partial bool ShowWeek { get; set; }

    [ObservableProperty]
    public partial bool HasData { get; set; }

    [ObservableProperty]
    public partial ISeries[] Series { get; set; } = [];

    [ObservableProperty]
    public partial Axis[] XAxes { get; set; } = [new Axis()];

    [ObservableProperty]
    public partial Axis[] YAxes { get; set; } = [new Axis()];

    public ObservableCollection<HistoryLegendItem> Legend { get; } = [];

    /// <summary>Enabled provider IDs. The chart shows only these providers.</summary>
    public IReadOnlySet<string> VisibleProviders { get; set; } = new HashSet<string>();

    partial void OnShowWeekChanged(bool value) => Reload();

    /// <summary>Reads the history again and rebuilds the chart. Safe to call from any thread.</summary>
    public void Reload()
    {
        var version = Interlocked.Increment(ref _loadVersion);
        var range = ShowWeek ? TimeSpan.FromDays(7) : TimeSpan.FromHours(24);
        var now = _time.GetUtcNow();
        var visible = VisibleProviders;

        Task.Run(() => _store.QueryLast(range, now).Where(p => visible.Contains(p.ProviderId)).ToList())
            .ContinueWith(
                task =>
                {
                    if (task.IsCompletedSuccessfully && version == Volatile.Read(ref _loadVersion))
                    {
                        _dispatcher.BeginInvoke(() => Build(task.Result, now, range));
                    }
                },
                TaskScheduler.Default);
    }

    private void Build(IReadOnlyList<HistoryPoint> points, DateTimeOffset now, TimeSpan range)
    {
        var dark = ApplicationThemeManager.GetAppTheme() != ApplicationTheme.Light;
        var labelColor = dark ? new SKColor(255, 255, 255, 150) : new SKColor(0, 0, 0, 150);
        var separatorColor = dark ? new SKColor(255, 255, 255, 24) : new SKColor(0, 0, 0, 20);

        var series = new List<ISeries>();
        Legend.Clear();

        var groups = points
            .GroupBy(p => (p.ProviderId, p.LimitId))
            .OrderBy(g => g.Key.ProviderId)
            .ThenBy(g => g.First().Kind);
        foreach (var group in groups)
        {
            var first = group.First();
            var color = ProviderColor(first.ProviderId, dark);
            var dashed = first.Kind != LimitKind.Session;
            var name = $"{_providerName(first.ProviderId)} {first.Label.ToLowerInvariant()}";

            series.Add(new LineSeries<DateTimePoint>
            {
                Name = name,
                Values = WithGaps(group),
                Fill = null,
                GeometrySize = 0,
                GeometryFill = null,
                GeometryStroke = null,
                LineSmoothness = 0,
                Stroke = new SolidColorPaint(color, 1.5f)
                {
                    PathEffect = dashed ? new DashEffect([4, 3]) : null,
                },
            });

            var brush = new SolidColorBrush(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
            brush.Freeze();
            Legend.Add(new HistoryLegendItem(name, brush, dashed));
        }

        var end = now.LocalDateTime;
        XAxes =
        [
            new DateTimeAxis(range > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : TimeSpan.FromHours(6),
                date => range > TimeSpan.FromDays(1) ? date.ToString("ddd") : date.ToString("t"))
            {
                MinLimit = (end - range).Ticks,
                MaxLimit = end.Ticks,
                TextSize = 10,
                LabelsPaint = new SolidColorPaint(labelColor),
                SeparatorsPaint = null,
            },
        ];
        YAxes =
        [
            new Axis
            {
                MinLimit = 0,
                MaxLimit = 100,
                MinStep = 50,
                ForceStepToMin = true,
                TextSize = 10,
                Labeler = value => $"{value:0}%",
                LabelsPaint = new SolidColorPaint(labelColor),
                SeparatorsPaint = new SolidColorPaint(separatorColor, 1),
                Position = AxisPosition.End,
            },
        ];
        Series = [.. series];
        HasData = series.Count > 0;
    }

    private static List<DateTimePoint> WithGaps(IEnumerable<HistoryPoint> points)
    {
        var values = new List<DateTimePoint>();
        DateTimeOffset? previous = null;
        foreach (var point in points)
        {
            if (previous is { } p && point.ObservedAt - p > GapThreshold)
            {
                values.Add(new DateTimePoint(p.LocalDateTime.AddTicks(1), null));
            }

            values.Add(new DateTimePoint(point.ObservedAt.LocalDateTime, point.UsedPercent));
            previous = point.ObservedAt;
        }

        return values;
    }

    private static SKColor ProviderColor(string providerId, bool dark) => providerId switch
    {
        ProviderIds.Claude => new SKColor(0xD9, 0x77, 0x57),
        ProviderIds.Codex => dark ? new SKColor(0x7F, 0xB4, 0xFF) : new SKColor(0x1F, 0x6F, 0xEB),
        _ => dark ? new SKColor(0xC8, 0xC8, 0xC8) : new SKColor(0x50, 0x50, 0x50),
    };
}
