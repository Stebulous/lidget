using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Lidget.App.Shell;

/// <summary>
/// Borderless, transparent WPF window that <see cref="TaskbarHost"/> re-parents into the taskbar.
/// Explorer destroys it with the taskbar, so the host creates a new instance after each embed.
/// </summary>
internal sealed class TaskbarWidgetWindow : Window
{
    private static readonly SolidColorBrush HitTestableClear = CreateClearBrush();

    private static SolidColorBrush CreateClearBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        brush.Freeze();
        return brush;
    }

    public TaskbarWidgetWindow(UIElement content)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        // A layered window passes clicks through pixels with zero alpha. Alpha 1 is invisible but hit-testable.
        Background = HitTestableClear;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Content = content;
    }

    public nint EnsureHandle() => new WindowInteropHelper(this).EnsureHandle();
}
