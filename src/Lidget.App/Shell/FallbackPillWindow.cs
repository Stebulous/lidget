using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static Lidget.App.Shell.NativeMethods;

namespace Lidget.App.Shell;

/// <summary>
/// Floating, draggable window that shows the widget when Lidget cannot embed it in the taskbar
/// (plan decision D17).
/// </summary>
internal sealed class FallbackPillWindow : Window
{
    private const double DragThreshold = 3;

    /// <summary>Distance from the corner of the work area, in device-independent pixels.</summary>
    private const double CornerMargin = 16;

    /// <summary>The right edge in physical pixels after the user moves the pill, or null while it stays in the corner.</summary>
    private int? _movedRight;

    public FallbackPillWindow(FrameworkElement content)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Widget = content;

        var surface = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4, 0, 4, 0),
            Height = 40,
            BorderThickness = new Thickness(1),
            Child = content,
        };
        surface.SetResourceReference(Border.BackgroundProperty, "Taskbar.PillBackground");
        surface.SetResourceReference(Border.BorderBrushProperty, "Taskbar.Track");
        Content = surface;

        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        Loaded += (_, _) => Place();
        SizeChanged += (_, _) => Place();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public event EventHandler? Clicked;

    /// <summary>The widget content, without the pill border.</summary>
    public FrameworkElement Widget { get; }

    public nint Handle => new WindowInteropHelper(this).Handle;

    /// <summary>Called when the display scale or the work area changes.</summary>
    public void Reposition() => Place();

    /// <summary>
    /// Puts the pill in the bottom-right corner of the work area, or keeps the right edge where the
    /// user moved it, so that the pill stays on screen when it grows. The placement uses physical
    /// pixels, because WPF device-independent pixels do not follow a display scale change.
    /// </summary>
    private void Place()
    {
        var hwnd = Handle;
        if (hwnd == 0 || !GetWindowRect(hwnd, out var rect))
        {
            return;
        }

        var monitor = MonitorInfo.FromPoint(rect.Left + (rect.Width / 2.0), rect.Top + (rect.Height / 2.0));
        if (_movedRight is null || monitor.WorkArea.Width <= 0)
        {
            monitor = MonitorInfo.FromPoint(0, 0);
        }

        var work = monitor.WorkArea;
        var margin = (int)Math.Round(CornerMargin * monitor.Scale);
        var right = _movedRight ?? work.Right - margin;
        var x = Math.Clamp(right - rect.Width, work.Left, Math.Max(work.Left, work.Right - rect.Width));
        var y = _movedRight is null ? work.Bottom - margin - rect.Height : Math.Clamp(rect.Top, work.Top, Math.Max(work.Top, work.Bottom - rect.Height));
        SetWindowPos(hwnd, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var start = new Point(Left, Top);
        DragMove();
        e.Handled = true;

        if (Math.Abs(Left - start.X) < DragThreshold && Math.Abs(Top - start.Y) < DragThreshold)
        {
            Clicked?.Invoke(this, EventArgs.Empty);
        }
        else if (GetWindowRect(Handle, out var rect))
        {
            _movedRight = rect.Right;
        }
    }
}
