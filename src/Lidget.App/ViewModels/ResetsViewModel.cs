using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lidget.Core.Formatting;
using Lidget.Core.Models;
using Lidget.Core.Storage;

namespace Lidget.App.ViewModels;

/// <summary>One banked reset in the flyout list.</summary>
public sealed partial class ResetItemViewModel(BankedReset reset) : ObservableObject
{
    public BankedReset Reset { get; } = reset;

    public string Id => Reset.Id;

    /// <summary>The title, with the number of resets when the entry has more than one, for example "Limit reset × 2".</summary>
    public string Title { get; } = reset.Count > 1 ? $"{ResetFormat.Title(reset.Title)} × {reset.Count}" : ResetFormat.Title(reset.Title);

    [ObservableProperty]
    public partial string ExpiryText { get; set; } = string.Empty;

    /// <summary>Whether the reset is in its last 24 hours. The flyout shows it in amber (plan decision D64).</summary>
    [ObservableProperty]
    public partial bool IsExpiringSoon { get; set; }

    /// <summary>Whether the row shows the **Use reset** button.</summary>
    [ObservableProperty]
    public partial bool CanRedeem { get; set; }

    public void Tick(DateTimeOffset now, TimeZoneInfo zone, bool canRedeem)
    {
        var remaining = Reset.ExpiresAt - now;
        IsExpiringSoon = Reset.IsUsable && remaining is { } left && left > TimeSpan.Zero && left <= ResetFormat.ExpiringSoon;
        ExpiryText = Reset.IsUsable
            ? ResetFormat.ExpiryText(Reset.ExpiresAt, now, zone)
            : $"Not usable now · {ResetFormat.ExpiryText(Reset.ExpiresAt, now, zone)}";
        CanRedeem = canRedeem && Reset.CanRedeemNow && Reset.IsUsable;
    }
}

/// <summary>One line of the reset history, for example "Nov 6, 5:51 PM" and "Expired".</summary>
public sealed record ResetEventViewModel(string TimeText, string Text, string Title);

/// <summary>
/// The **Resets** row of a provider card: the banked resets, the confirmation of a redemption, and the history
/// (plan decisions D62 and D63).
/// </summary>
public sealed partial class ResetsViewModel(string providerId, string displayName, TimeProvider time) : ObservableObject
{
    /// <summary>The history list shows this number of events at most.</summary>
    public const int HistoryLength = 8;

    private ResetSummary? _summary;
    private IReadOnlyList<LimitWindow> _limits = [];
    private UsageDisplay _display;

    /// <summary>Redeems a reset of this provider. The owner sets it when the provider can redeem.</summary>
    public Func<string, Task<RedeemOutcome>>? Redeem { get; set; }

    public ObservableCollection<ResetItemViewModel> Items { get; } = [];

    public ObservableCollection<ResetEventViewModel> History { get; } = [];

    /// <summary>Whether the provider card shows the row: the provider gave reset data, or the history has events.</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    /// <summary>The number of resets that the account can use now. The widget shows a dot when it is more than 0.</summary>
    [ObservableProperty]
    public partial int AvailableCount { get; set; }

    /// <summary>Whether a usable reset is in its last 24 hours.</summary>
    [ObservableProperty]
    public partial bool IsExpiringSoon { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Whether the provider gave the count without the list.</summary>
    [ObservableProperty]
    public partial bool HasCountOnly { get; set; }

    [ObservableProperty]
    public partial bool HasHistory { get; set; }

    /// <summary>The reset that waits for the confirmation of the user, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming))]
    public partial ResetItemViewModel? Pending { get; set; }

    public bool IsConfirming => Pending is not null;

    /// <summary>The used share of the session and weekly limits, so that the user sees what a redemption gives.</summary>
    [ObservableProperty]
    public partial string ConfirmUsageText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRedeeming { get; set; }

    /// <summary>The result of the last redemption, or null.</summary>
    [ObservableProperty]
    public partial string? Message { get; set; }

    public void Apply(ResetSummary? summary, IReadOnlyList<LimitWindow> limits, UsageDisplay display, DateTimeOffset now)
    {
        _limits = limits;
        _display = display;
        if (!ReferenceEquals(summary, _summary))
        {
            _summary = summary;
            Items.Clear();
            foreach (var reset in (summary?.Resets ?? []).Where(r => r.Count > 0).OrderBy(r => r.ExpiresAt ?? DateTimeOffset.MaxValue))
            {
                Items.Add(new ResetItemViewModel(reset));
            }

            // The list changed, so a confirmation for an entry that is gone must not stay.
            if (Pending is { } pending && !IsRedeeming)
            {
                Pending = Items.FirstOrDefault(i => i.Id == pending.Id);
            }
        }

        Tick(now);
    }

    /// <summary>Updates the texts that change with time.</summary>
    public void Tick(DateTimeOffset now)
    {
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].Reset.ExpiresAt is { } expires && expires <= now)
            {
                Items.RemoveAt(i);
            }
        }

        var canRedeem = Redeem is not null && !IsRedeeming;
        foreach (var item in Items)
        {
            item.Tick(now, time.LocalTimeZone, canRedeem);
        }

        var summary = _summary;
        AvailableCount = summary is null
            ? 0
            : summary.Resets is null ? summary.AvailableCount : Math.Min(summary.AvailableCount, Items.Where(i => i.Reset.IsUsable).Sum(i => i.Reset.Count));
        HasCountOnly = summary is { Resets: null, AvailableCount: > 0 };
        IsExpiringSoon = Items.Any(i => i.IsExpiringSoon);
        SummaryText = summary is null
            ? "None available"
            : ResetFormat.SummaryText(summary with { AvailableCount = AvailableCount }, now);
        IsVisible = summary is not null || HasHistory;
        if (Pending is not null)
        {
            ConfirmUsageText = UsageText();
        }
    }

    /// <summary>Shows the newest reset events.</summary>
    public void ApplyHistory(IReadOnlyList<ResetEvent> events)
    {
        History.Clear();
        foreach (var item in events)
        {
            History.Add(new ResetEventViewModel(
                ResetFormat.DateText(item.OccurredAt, time.LocalTimeZone),
                ResetFormat.EventText(item.Kind),
                ResetFormat.Title(item.Title)));
        }

        HasHistory = History.Count > 0;
        IsVisible = _summary is not null || HasHistory;
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    /// <summary>Asks the user to confirm. No request is sent before <see cref="ConfirmAsync"/>.</summary>
    [RelayCommand]
    private void Begin(ResetItemViewModel? item)
    {
        if (item is null || IsRedeeming || Redeem is null)
        {
            return;
        }

        Message = null;
        ConfirmUsageText = UsageText();
        Pending = item;
    }

    [RelayCommand]
    public void Cancel()
    {
        if (!IsRedeeming)
        {
            Pending = null;
        }
    }

    /// <summary>Closes the list, the confirmation, and the last result, for example when the flyout closes.</summary>
    public void Dismiss()
    {
        Cancel();
        if (!IsRedeeming)
        {
            Message = null;
            IsExpanded = false;
        }
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (Pending is not { } item || IsRedeeming || Redeem is not { } redeem)
        {
            return;
        }

        IsRedeeming = true;
        Tick(time.GetUtcNow());
        RedeemOutcome outcome;
        try
        {
            outcome = await redeem(item.Id);
        }
        catch (Exception)
        {
            outcome = RedeemOutcome.Failed;
        }
        finally
        {
            IsRedeeming = false;
        }

        Pending = null;
        Message = ResetFormat.OutcomeText(outcome, displayName);
        Tick(time.GetUtcNow());
    }

    private string UsageText()
    {
        var parts = _limits
            .Where(l => l.Kind is LimitKind.Session or LimitKind.Weekly)
            .Select(l => $"{(l.Kind == LimitKind.Session ? "Session" : "Weekly")} {UsageFormat.LimitText(l.UsedPercent, _display)}")
            .ToArray();
        return parts.Length == 0 ? "not known" : string.Join(" · ", parts);
    }

    public string ProviderId { get; } = providerId;
}
