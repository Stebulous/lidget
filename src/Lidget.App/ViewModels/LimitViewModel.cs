using CommunityToolkit.Mvvm.ComponentModel;
using Lidget.Core.Formatting;
using Lidget.Core.Models;

namespace Lidget.App.ViewModels;

public sealed partial class LimitViewModel(string id) : ObservableObject
{
    private LimitWindow? _window;
    private UsageDisplay _display;

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

    [ObservableProperty]
    public partial bool HasPace { get; set; }

    /// <summary>The position of the even-pace marker on the bar, from 0 to 100, in the same display mode as the bar.</summary>
    [ObservableProperty]
    public partial double PacePercent { get; set; }

    [ObservableProperty]
    public partial string PaceText { get; set; } = string.Empty;

    /// <summary>Whether the limit runs out before it resets at the average rate so far.</summary>
    [ObservableProperty]
    public partial bool RunsOutBeforeReset { get; set; }

    public void Apply(LimitWindow window, UsageDisplay display, DateTimeOffset now)
    {
        _window = window;
        _display = display;
        Label = window.Label;
        Kind = window.Kind;
        BarPercent = UsageFormat.DisplayPercent(window.UsedPercent, display);
        PercentText = UsageFormat.LimitText(window.UsedPercent, display);
        Severity = UsageFormat.Severity(window.UsedPercent);
        Tick(now);
    }

    public void Tick(DateTimeOffset now)
    {
        if (_window is not { } window)
        {
            return;
        }

        // Claude reports no reset time for a window that no request has started yet.
        ResetText = window.ResetsAt is null && window.UsedPercent <= 0 ? "Not started" : UsageFormat.ResetText(window.ResetsAt, now);

        var pace = UsagePace.Calculate(window, now);
        HasPace = pace is not null;
        PacePercent = pace is null ? 0 : UsageFormat.DisplayPercent(pace.ExpectedPercent, _display);
        PaceText = pace is null ? string.Empty : UsageFormat.PaceText(pace, now);
        RunsOutBeforeReset = pace?.RunsOutAt is not null;
    }
}
