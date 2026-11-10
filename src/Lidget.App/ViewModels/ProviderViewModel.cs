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
    private bool _showResetDot;

    public ProviderViewModel(string id, string displayName, TimeProvider time)
    {
        Id = id;
        DisplayName = displayName;
        Resets = new ResetsViewModel(id, displayName, time);
        Icon = Application.Current.TryFindResource($"ProviderIcon.{id}") as Geometry;
        IconBrush = Application.Current.TryFindResource($"ProviderBrush.{id}") as Brush;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public Geometry? Icon { get; }

    /// <summary>The brand color, or null to use the text color.</summary>
    public Brush? IconBrush { get; }

    public ObservableCollection<LimitViewModel> Limits { get; } = [];

    /// <summary>The banked resets of the provider, for the flyout.</summary>
    public ResetsViewModel Resets { get; }

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

    /// <summary>Whether the Rings widget style has a session limit to show in the inner ring.</summary>
    [ObservableProperty]
    public partial bool HasSessionRing { get; set; }

    /// <summary>The length of the inner ring, from 0 to 100, in the display mode of the widget.</summary>
    [ObservableProperty]
    public partial double SessionRingPercent { get; set; }

    [ObservableProperty]
    public partial UsageSeverity SessionRingSeverity { get; set; }

    /// <summary>Whether the Rings widget style has a weekly limit to show in the outer ring.</summary>
    [ObservableProperty]
    public partial bool HasWeeklyRing { get; set; }

    /// <summary>The length of the outer ring, from 0 to 100, in the display mode of the widget.</summary>
    [ObservableProperty]
    public partial double WeeklyRingPercent { get; set; }

    [ObservableProperty]
    public partial UsageSeverity WeeklyRingSeverity { get; set; }

    /// <summary>Whether the widget shows the dot for an available banked reset (plan decision D66).</summary>
    [ObservableProperty]
    public partial bool HasResetDot { get; set; }

    /// <summary>Whether a reset expires in 24 hours. The dot then has the warning color.</summary>
    [ObservableProperty]
    public partial bool IsResetDotUrgent { get; set; }

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

        Resets.Apply(snapshot.Resets, snapshot.Limits, _display, now);
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

    /// <summary>Applies the widget metric, the display mode, the pace setting, and the reset dot setting, and shows the current data with them.</summary>
    public void ApplySettings(LidgetSettings settings, DateTimeOffset now)
    {
        _metric = settings.WidgetMetric;
        _display = settings.UsageDisplay;
        _showPace = settings.ShowPaceInTaskbar;
        _showResetDot = settings.ShowResetsInTaskbar;
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
        UpdateRings();
        HasResetDot = _showResetDot && IsEnabled && Resets.AvailableCount > 0;
        IsResetDotUrgent = HasResetDot && Resets.IsExpiringSoon;

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

    /// <summary>The Rings style shows the session and weekly limits at the same time, so it does not use the widget metric.</summary>
    private void UpdateRings()
    {
        var session = _snapshot?.Limits.FirstOrDefault(l => l.Kind == LimitKind.Session);
        HasSessionRing = session is not null;
        SessionRingPercent = session is null ? 0 : UsageFormat.DisplayPercent(session.UsedPercent, _display);
        SessionRingSeverity = session is null ? UsageSeverity.Normal : UsageFormat.Severity(session.UsedPercent);

        var weekly = _snapshot?.Limits.FirstOrDefault(l => l.Kind == LimitKind.Weekly);
        HasWeeklyRing = weekly is not null;
        WeeklyRingPercent = weekly is null ? 0 : UsageFormat.DisplayPercent(weekly.UsedPercent, _display);
        WeeklyRingSeverity = weekly is null ? UsageSeverity.Normal : UsageFormat.Severity(weekly.UsedPercent);
    }

    public void Tick(DateTimeOffset now)
    {
        foreach (var limit in Limits)
        {
            limit.Tick(now);
        }

        Resets.Tick(now);
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

        if (Resets.AvailableCount > 0)
        {
            lines.Add($"Resets: {Resets.SummaryText}");
        }

        if (HasProblem)
        {
            lines.Add(StatusMessage!);
        }

        WidgetTooltip = string.Join(Environment.NewLine, lines);
    }
}
