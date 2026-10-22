using CommunityToolkit.Mvvm.ComponentModel;
using Lidget.Core.Formatting;
using Lidget.Core.Models;

namespace Lidget.App.ViewModels;

public sealed partial class LimitViewModel(string id) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double UsedPercent { get; set; }

    [ObservableProperty]
    public partial string PercentText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResetText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial UsageSeverity Severity { get; set; }

    [ObservableProperty]
    public partial LimitKind Kind { get; set; }

    public DateTimeOffset? ResetsAt { get; private set; }

    public void Apply(LimitWindow window, DateTimeOffset now)
    {
        Label = window.Label;
        Kind = window.Kind;
        UsedPercent = Math.Clamp(window.UsedPercent, 0, 100);
        PercentText = UsageFormat.Percent(window.UsedPercent);
        Severity = UsageFormat.Severity(window.UsedPercent);
        ResetsAt = window.ResetsAt;
        Tick(now);
    }

    public void Tick(DateTimeOffset now) => ResetText = UsageFormat.ResetText(ResetsAt, now);
}
