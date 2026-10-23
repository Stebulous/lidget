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

        UpdateWidget();
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

    /// <summary>Applies the widget metric and the display mode, and shows the current data with them.</summary>
    public void ApplySettings(WidgetMetric metric, UsageDisplay display, DateTimeOffset now)
    {
        _metric = metric;
        _display = display;
        if (_snapshot is { } snapshot)
        {
            Apply(snapshot, now);
        }
        else
        {
            UpdateWidget();
        }
    }

    private void UpdateWidget()
    {
        var selected = _snapshot?.SelectLimit(_metric);
        if (selected is null)
        {
            WidgetPercent = 0;
            WidgetText = "–";
            WidgetSeverity = UsageSeverity.Normal;
            return;
        }

        WidgetPercent = UsageFormat.DisplayPercent(selected.UsedPercent, _display);
        WidgetText = UsageFormat.Percent(WidgetPercent);
        WidgetSeverity = UsageFormat.Severity(selected.UsedPercent);
    }

    public void Tick(DateTimeOffset now)
    {
        foreach (var limit in Limits)
        {
            limit.Tick(now);
        }

        UpdatedText = _snapshot is { } snapshot && HasData ? $"Updated {UsageFormat.Ago(snapshot.UpdatedAt, now)}" : string.Empty;

        var lines = new List<string> { PlanName is null ? DisplayName : $"{DisplayName} {PlanName}" };
        lines.AddRange(Limits.Select(l => $"{l.Label}: {l.PercentText} · {l.ResetText}"));
        if (HasProblem)
        {
            lines.Add(StatusMessage!);
        }

        WidgetTooltip = string.Join(Environment.NewLine, lines);
    }
}
