using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Lidget.Core.Formatting;
using Lidget.Core.Models;

namespace Lidget.App.ViewModels;

public sealed partial class ProviderViewModel : ObservableObject
{
    private ProviderSnapshot? _snapshot;
    private WidgetMetric _metric;
    private UsageDisplay _display;
    private bool _showPace;

    public ProviderViewModel(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
        Icon = Application.Current.TryFindResource($"ProviderIcon.{id}") as Geometry;
        IconBrush = Application.Current.TryFindResource($"ProviderBrush.{id}") as Brush;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public Geometry? Icon { get; }

    /// <summary>The brand color, or null to use the text color.</summary>
    public Brush? IconBrush { get; }

    public ObservableCollection<LimitViewModel> Limits { get; } = [];

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial ProviderStatus Status { get; set; } = ProviderStatus.Stale;

    [ObservableProperty]
    public partial string? StatusMessage { get; set; } = "Loading…";

    [ObservableProperty]
    public partial string? PlanName { get; set; }

    [ObservableProperty]
    public partial string UpdatedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasData { get; set; }

    [ObservableProperty]
    public partial double WidgetPercent { get; set; }

    [ObservableProperty]
    public partial string WidgetText { get; set; } = "–";

    [ObservableProperty]
    public partial UsageSeverity WidgetSeverity { get; set; }

    /// <summary>Whether the widget bar shows the even-pace marker.</summary>
    [ObservableProperty]
    public partial bool WidgetHasPace { get; set; }

    /// <summary>The position of the widget pace marker, from 0 to 100, in the same display mode as the bar.</summary>
    [ObservableProperty]
    public partial double WidgetPacePercent { get; set; }

    [ObservableProperty]
    public partial string WidgetTooltip { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasCosts { get; set; }

    [ObservableProperty]
    public partial string CostToday { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Cost7Days { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Cost30Days { get; set; } = string.Empty;

    /// <summary>The caption under the cost tiles.</summary>
    [ObservableProperty]
    public partial string CostNote { get; set; } = string.Empty;

    public bool HasProblem => Status is not ProviderStatus.Ok && StatusMessage is not null;

    partial void OnStatusChanged(ProviderStatus value) => OnPropertyChanged(nameof(HasProblem));

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasProblem));

    public void Apply(ProviderSnapshot snapshot, DateTimeOffset now)
    {
        _snapshot = snapshot;
        Status = snapshot.Status;
        StatusMessage = snapshot.StatusMessage;
        PlanName = snapshot.PlanName ?? PlanName;
        HasData = snapshot.Limits.Count > 0;

        // Update rows in place, so that bindings and animations keep their elements.
        for (var i = 0; i < snapshot.Limits.Count; i++)
        {
            var window = snapshot.Limits[i];
            var row = i < Limits.Count && Limits[i].Id == window.Id ? Limits[i] : null;
            if (row is null)
            {
                row = new LimitViewModel(window.Id);
                if (i < Limits.Count)
                {
                    Limits[i] = row;
                }
                else
                {
                    Limits.Add(row);
                }
            }

            row.Apply(window, _display, now);
        }

        while (Limits.Count > snapshot.Limits.Count)
        {
            Limits.RemoveAt(Limits.Count - 1);
        }

        Tick(now);
    }

    /// <summary>Shows the local cost estimate (plan task T3.3).</summary>
    public void ApplyCosts(CostSummary summary)
    {
        HasCosts = true;
        CostToday = UsageFormat.Usd(summary.Today);
        Cost7Days = UsageFormat.Usd(summary.Last7Days);
        Cost30Days = UsageFormat.Usd(summary.Last30Days);
        CostNote = summary.HasUnpricedModels
            ? $"API-equivalent estimate. Some models are not priced: {string.Join(", ", summary.UnpricedModels)}."
            : "API-equivalent estimate from local session logs";
    }

    /// <summary>Applies the widget metric, the display mode, and the pace setting, and shows the current data with them.</summary>
    public void ApplySettings(WidgetMetric metric, UsageDisplay display, bool showPace, DateTimeOffset now)
    {
        _metric = metric;
        _display = display;
        _showPace = showPace;
        if (_snapshot is { } snapshot)
        {
            Apply(snapshot, now);
        }
        else
        {
            UpdateWidget(now);
        }
    }

    /// <summary>Updates the widget values. The pace marker moves with time, so <see cref="Tick"/> calls this too.</summary>
    private void UpdateWidget(DateTimeOffset now)
    {
        var selected = _snapshot?.SelectLimit(_metric);
        if (selected is null)
        {
            WidgetPercent = 0;
            WidgetText = "–";
            WidgetSeverity = UsageSeverity.Normal;
            WidgetHasPace = false;
            return;
        }

        WidgetPercent = UsageFormat.DisplayPercent(selected.UsedPercent, _display);
        WidgetText = UsageFormat.Percent(WidgetPercent);
        WidgetSeverity = UsageFormat.Severity(selected.UsedPercent);

        var pace = _showPace ? UsagePace.Calculate(selected, now) : null;
        WidgetHasPace = pace is not null;
        WidgetPacePercent = pace is null ? 0 : UsageFormat.DisplayPercent(pace.ExpectedPercent, _display);
    }

    public void Tick(DateTimeOffset now)
    {
        foreach (var limit in Limits)
        {
            limit.Tick(now);
        }

        UpdateWidget(now);
        UpdatedText = _snapshot is { } snapshot && HasData ? $"Updated {UsageFormat.Ago(snapshot.UpdatedAt, now)}" : string.Empty;

        var lines = new List<string> { PlanName is null ? DisplayName : $"{DisplayName} {PlanName}" };
        foreach (var limit in Limits)
        {
            lines.Add($"{limit.Label}: {limit.PercentText} · {limit.ResetText}");
            if (limit.HasPace)
            {
                // An indented line of its own, so that the tooltip does not wrap the pace inside a line.
                lines.Add($"    {limit.PaceText}");
            }
        }

        if (HasProblem)
        {
            lines.Add(StatusMessage!);
        }

        WidgetTooltip = string.Join(Environment.NewLine, lines);
    }
}
