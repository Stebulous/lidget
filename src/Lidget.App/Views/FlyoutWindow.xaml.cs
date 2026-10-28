using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Lidget.App.Shell;
using Lidget.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Lidget.App.Views;

/// <summary>
/// Detail flyout above the taskbar widget. It closes when it loses focus, like the system flyouts.
/// </summary>
public partial class FlyoutWindow : FluentWindow
{
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(300);
    private const double ScreenMargin = 12;

    private readonly UsageViewModel _viewModel;
    private long _lastHiddenTicks;
    private double _anchorBottom;
    private double _anchorCenterX;
    private double _workLeft;
    private double _workRight;
    private bool _allowClose;

    public FlyoutWindow(UsageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        SystemThemeWatcher.Watch(this);

        viewModel.VisibleProviders.CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateEmptyState();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UsageViewModel.IsRefreshing))
            {
                UpdateRefreshRing();
            }
        };

        Deactivated += (_, _) => HideFlyout();
        SizeChanged += (_, _) => KeepAnchored();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Opens the flyout above <paramref name="anchor"/>, or closes it if it is open.</summary>
    /// <param name="anchor">The widget bounds in physical screen pixels.</param>
    public void Toggle(ScreenRect anchor)
    {
        if (IsVisible)
        {
            HideFlyout();
            return;
        }

        // A click on the widget first deactivates the open flyout. Do not open it again at once.
        if (Environment.TickCount64 - _lastHiddenTicks < ReopenGuard.TotalMilliseconds)
        {
            return;
        }

        ShowAt(anchor);
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void ShowAt(ScreenRect anchor)
    {
        AttachHistory(true);
        var monitor = MonitorInfo.FromPoint(anchor.CenterX, anchor.CenterY);
        var scale = monitor.Scale;

        Root.Measure(new Size(Width - Root.Margin.Left - Root.Margin.Right, double.PositiveInfinity));
        var height = Root.DesiredSize.Height + Root.Margin.Top + Root.Margin.Bottom;

        _workLeft = monitor.WorkArea.Left / scale;
        _workRight = monitor.WorkArea.Right / scale;
        _anchorCenterX = anchor.CenterX / scale;
        // The floating pill sits inside the work area, so open above it, not over it.
        _anchorBottom = (Math.Min(monitor.WorkArea.Bottom, anchor.Top) / scale) - ScreenMargin;
        Left = ClampLeft(Width);
        Top = _anchorBottom - height;

        Show();
        Activate();
        KeepAnchored();
        PlayEntrance();
        _viewModel.History?.Reload();
        _ = _viewModel.RefreshIfStaleAsync();
    }

    private void HideFlyout()
    {
        if (!IsVisible)
        {
            return;
        }

        _lastHiddenTicks = Environment.TickCount64;
        Hide();
        AttachHistory(false);
    }

    /// <summary>
    /// Adds the history chart to the flyout, or removes it. A loaded LiveCharts chart handles each WPF frame
    /// (<c>CompositionTarget.Rendering</c>), so that WPF renders at the display refresh rate. A hidden window
    /// does not unload its content, and a hidden window does no layout, so a content change has no effect.
    /// Only a removed child unloads at once, and its chart stops the frames.
    /// </summary>
    private void AttachHistory(bool shown)
    {
        if (_viewModel.History is not { } history)
        {
            return;
        }

        history.IsShown = shown;
        HistorySlot.Child = shown ? new HistoryChartView { DataContext = history } : null;
    }

    private void KeepAnchored()
    {
        if (IsVisible && ActualHeight > 0)
        {
            Left = ClampLeft(ActualWidth);
            Top = _anchorBottom - ActualHeight;
        }
    }

    private double ClampLeft(double width) =>
        Math.Clamp(_anchorCenterX - (width / 2), _workLeft + ScreenMargin, Math.Max(_workLeft + ScreenMargin, _workRight - width - ScreenMargin));

    private void PlayEntrance()
    {
        // Windows already fades the window in. The content rises 8 px with a strong ease-out curve.
        // Skip the movement when the user turned off animations in Windows.
        if (!SystemParameters.ClientAreaAnimation)
        {
            Slide.Y = 0;
            return;
        }

        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(180) };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(8, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromPercent(1), new KeySpline(0.23, 1, 0.32, 1)));
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, animation);
    }

    /// <summary>
    /// Adds a progress ring during a refresh, and removes it after. The ring starts an animation that repeats
    /// forever, and its template only pauses the animation when the ring is hidden. A paused animation keeps
    /// WPF rendering frames, so a hidden ring costs CPU until Lidget quits. A removed ring stops the frames.
    /// </summary>
    private void UpdateRefreshRing() =>
        RefreshSlot.Child = _viewModel.IsRefreshing ? new ProgressRing { IsIndeterminate = true, Width = 16, Height = 16 } : null;

    private void UpdateEmptyState() =>
        EmptyText.Visibility = _viewModel.VisibleProviders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideFlyout();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 hides the flyout. Only app exit closes it.
        if (!_allowClose)
        {
            e.Cancel = true;
            HideFlyout();
        }

        base.OnClosing(e);
    }
}
