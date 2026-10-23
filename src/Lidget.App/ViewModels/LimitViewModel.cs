using CommunityToolkit.Mvvm.ComponentModel;
using Lidget.Core.Formatting;
using Lidget.Core.Models;

namespace Lidget.App.ViewModels;

public sealed partial class LimitViewModel(string id) : ObservableObject
{
    private double _usedPercent;

    public string Id { get; } = id;

    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    /// <summary>The bar length, from 0 to 100: the used share or the share that is left.</summary>
    [ObservableProperty]
    public partial double BarPercent { get; set; }

    [ObservableProperty]
    public partial string PercentText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResetText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial UsageSeverity Severity { get; set; }

    [ObservableProperty]
    public partial LimitKind Kind { get; set; }

    public DateTimeOffset? ResetsAt { get; private set; }

    public void Apply(LimitWindow window, UsageDisplay display, DateTimeOffset now)
    {
        Label = window.Label;
        Kind = window.Kind;
        BarPercent = UsageFormat.DisplayPercent(window.UsedPercent, display);
        PercentText = UsageFormat.LimitText(window.UsedPercent, display);
        Severity = UsageFormat.Severity(window.UsedPercent);
        ResetsAt = window.ResetsAt;
        _usedPercent = window.UsedPercent;
        Tick(now);
    }

    /// <summary>Claude reports no reset time for a window that no request has started yet.</summary>
    public void Tick(DateTimeOffset now) =>
        ResetText = ResetsAt is null && _usedPercent <= 0 ? "Not started" : UsageFormat.ResetText(ResetsAt, now);
}
