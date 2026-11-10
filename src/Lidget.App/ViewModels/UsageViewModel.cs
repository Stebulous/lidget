using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lidget.Core.Costs;
using Lidget.Core.Models;
using Lidget.Core.Providers;
using Lidget.Core.Refresh;
using Lidget.Core.Resets;
using Lidget.Core.Storage;

namespace Lidget.App.ViewModels;

/// <summary>Root view model for the widget and the flyout.</summary>
public sealed partial class UsageViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly RefreshScheduler _scheduler;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _ticker;

    public UsageViewModel(IEnumerable<IUsageProvider> providers, RefreshScheduler scheduler, SettingsStore settings, TimeProvider time)
    {
        _providers = providers.ToArray();
        _scheduler = scheduler;
        _settings = settings;
        _time = time;
        _dispatcher = Dispatcher.CurrentDispatcher;

        foreach (var provider in _providers)
        {
            Providers.Add(new ProviderViewModel(provider.Id, provider.DisplayName, time));
        }

        ApplySettings(settings.Current);
        _settings.Changed += (_, s) => _dispatcher.BeginInvoke(() => ApplySettings(s));
        _scheduler.SnapshotPublished += (_, snapshot) => _dispatcher.BeginInvoke(() => Apply(snapshot));

        // Countdowns and "updated" texts change with time, not only with new data.
        _ticker = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => Tick(), _dispatcher);
        _ticker.Start();
    }

    /// <summary>All providers, in display order.</summary>
    public ObservableCollection<ProviderViewModel> Providers { get; } = [];

    /// <summary>Enabled providers, which the widget and the flyout show.</summary>
    public ObservableCollection<ProviderViewModel> VisibleProviders { get; } = [];

    /// <summary>Raised after visible data changes, so that the taskbar host can resize the widget.</summary>
    public event EventHandler? WidgetChanged;

    public event EventHandler? SettingsRequested;

    public event EventHandler? QuitRequested;

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    /// <summary>The form of the taskbar widget: bars or rings (plan decision D66).</summary>
    [ObservableProperty]
    public partial WidgetStyle WidgetStyle { get; set; }

    /// <summary>Redeems banked resets, or null when redemption is not available. The flyout asks the user to confirm first.</summary>
    public ResetRedemptionService? Redemptions
    {
        get;
        init
        {
            field = value;
            if (value is null)
            {
                return;
            }

            foreach (var provider in Providers.Where(p => value.CanRedeem(p.Id)))
            {
                provider.Resets.Redeem = resetId => value.RedeemAsync(provider.Id, resetId);
            }
        }
    }

    /// <summary>The store of the reset events, or null when no history store is available.</summary>
    public HistoryStore? ResetHistory
    {
        get;
        init
        {
            field = value;
            ReloadResetHistory();
        }
    }

    /// <summary>Records the reset events. The flyout shows each new event.</summary>
    public ResetTracker? ResetTracker
    {
        get;
        init
        {
            field = value;
            if (value is not null)
            {
                value.Changed += (_, _) => _dispatcher.BeginInvoke(ReloadResetHistory);
            }
        }
    }

    /// <summary>The history chart, or null when no history store is available.</summary>
    public HistoryViewModel? History
    {
        get;
        init
        {
            field = value;
            ApplySettings(_settings.Current);
        }
    }

    /// <summary>The local cost estimates, or null when cost tracking is not available.</summary>
    public CostTracker? Costs
    {
        get;
        init
        {
            field = value;
            if (value is null)
            {
                return;
            }

            value.CostsUpdated += (_, update) => _dispatcher.BeginInvoke(() => ApplyCosts(update));
            foreach (var (providerId, summary) in value.Latest)
            {
                ApplyCosts(new CostUpdate(providerId, summary));
            }
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            await Task.WhenAll(_scheduler.RefreshNowAsync(), Costs?.ScanAsync() ?? Task.CompletedTask);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>
    /// Refreshes when the flyout opens and the data is older than 60 seconds (plan decision D23).
    /// Rescans the session logs on the same rule.
    /// </summary>
    public Task RefreshIfStaleAsync()
    {
        // The user looks at the resets, so a provider whose details cost a separate lookup can read them again.
        foreach (var source in _providers.OfType<IResetDetailSource>())
        {
            source.RequestResetDetails();
        }

        return Task.WhenAll(
            _scheduler.RefreshStaleAsync(TimeSpan.FromSeconds(60)),
            Costs?.ScanIfStaleAsync(TimeSpan.FromSeconds(60)) ?? Task.CompletedTask);
    }

    /// <summary>Removes each confirmation that waits for the user and each redemption result, for example when the flyout closes.</summary>
    public void CancelPendingResets()
    {
        foreach (var provider in Providers)
        {
            provider.Resets.Dismiss();
        }
    }

    private void ReloadResetHistory()
    {
        if (ResetHistory is not { } store)
        {
            return;
        }

        foreach (var provider in Providers)
        {
            try
            {
                provider.Resets.ApplyHistory(store.QueryResetEvents(provider.Id, ResetsViewModel.HistoryLength));
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
            {
                // The history is not necessary to show the resets.
            }
        }
    }

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Quit() => QuitRequested?.Invoke(this, EventArgs.Empty);

    private void Apply(ProviderSnapshot snapshot)
    {
        var provider = Providers.FirstOrDefault(p => p.Id == snapshot.ProviderId);
        provider?.Apply(snapshot, _time.GetUtcNow());
        WidgetChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyCosts(CostUpdate update) =>
        Providers.FirstOrDefault(p => p.Id == update.ProviderId)?.ApplyCosts(update.Summary);

    private void ApplySettings(LidgetSettings settings)
    {
        WidgetStyle = settings.WidgetStyle;
        VisibleProviders.Clear();
        foreach (var provider in Providers)
        {
            provider.IsEnabled = settings.IsProviderEnabled(provider.Id);
            provider.ApplySettings(settings, _time.GetUtcNow());
            if (provider.IsEnabled)
            {
                VisibleProviders.Add(provider);
            }
        }

        if (History is not null)
        {
            History.VisibleProviders = VisibleProviders.Select(p => p.Id).ToHashSet();
            History.Display = settings.UsageDisplay;
            History.Reload();
        }

        WidgetChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Tick()
    {
        var now = _time.GetUtcNow();
        foreach (var provider in Providers)
        {
            provider.Tick(now);
        }
    }

    public void Dispose() => _ticker.Stop();
}
