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
    public TaskbarWidgetWindow(UIElement content)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Content = content;
    }

    public nint EnsureHandle() => new WindowInteropHelper(this).EnsureHandle();
}
