using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Lidget.App.Shell;

/// <summary>
/// Floating, draggable window that shows the widget when Lidget cannot embed it in the taskbar
/// (plan decision D17).
/// </summary>
internal sealed class FallbackPillWindow : Window
{
    private const double DragThreshold = 3;

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
        Loaded += (_, _) => PlaceInCorner();
        SizeChanged += OnSizeChanged;
    }

    public event EventHandler? Clicked;

    public nint Handle => new WindowInteropHelper(this).Handle;

    private void PlaceInCorner()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - ActualWidth - 16;
        Top = work.Bottom - ActualHeight - 16;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The pill grows when data arrives. Keep its right edge in place so that it stays on screen.
        if (e.WidthChanged && e.PreviousSize.Width > 0)
        {
            Left -= e.NewSize.Width - e.PreviousSize.Width;
        }
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
    }
}
