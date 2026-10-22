using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using static Lidget.App.Shell.NativeMethods;

namespace Lidget.App.Shell;

/// <summary>
/// Embeds widget content as a child window of the Windows 11 taskbar, left of the system tray.
/// A hidden top-level window receives <c>TaskbarCreated</c>, because Explorer destroys the
/// embedded child window when it restarts.
/// </summary>
internal sealed class TaskbarHost : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly Func<FrameworkElement> _contentFactory;
    private readonly uint _taskbarCreatedMessage;
    private readonly HwndSource _messageWindow;
    private readonly DispatcherTimer _timer;
    private TaskbarWidgetWindow? _window;
    private nint _taskbar;
    private bool _disposed;

    public TaskbarHost(Func<FrameworkElement> contentFactory)
    {
        _contentFactory = contentFactory;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        // Broadcast messages reach hidden top-level windows, but not message-only windows.
        _messageWindow = new HwndSource(new HwndSourceParameters("LidgetShellMessages")
        {
            WindowStyle = unchecked((int)WS_POPUP),
            Width = 0,
            Height = 0,
        });
        _messageWindow.AddHook(MessageHook);

        _timer = new DispatcherTimer(PollInterval, DispatcherPriority.Background, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
    }

    /// <summary>Distance between the widget and the tray, in device-independent pixels.</summary>
    public double TrayOffset { get; set; } = 4;

    public bool IsEmbedded => _window is not null;

    public void Start()
    {
        TryEmbed();
        _timer.Start();
    }

    /// <summary>Recomputes the widget size and position, for example after its content changes.</summary>
    public void UpdatePosition()
    {
        if (_window is null || !IsWindow(_taskbar))
        {
            return;
        }

        var hwnd = _window.EnsureHandle();
        if (!GetWindowRect(_taskbar, out var taskbarRect))
        {
            return;
        }

        var tray = FindWindowEx(_taskbar, 0, "TrayNotifyWnd", null);
        var anchorRight = tray != 0 && GetWindowRect(tray, out var trayRect)
            ? trayRect.Left - taskbarRect.Left
            : taskbarRect.Width;

        var scale = GetDpiForWindow(hwnd) / 96.0;
        var content = (FrameworkElement)_window.Content;
        content.Measure(new Size(double.PositiveInfinity, taskbarRect.Height / scale));

        var width = (int)Math.Ceiling(content.DesiredSize.Width * scale);
        var x = anchorRight - (int)Math.Round(TrayOffset * scale) - width;

        SetWindowPos(hwnd, HWND_TOP, x, 0, width, taskbarRect.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void TryEmbed()
    {
        if (_disposed || _window is not null)
        {
            return;
        }

        _taskbar = FindWindow("Shell_TrayWnd", null);
        if (_taskbar == 0)
        {
            return;
        }

        var window = new TaskbarWidgetWindow(_contentFactory());
        var hwnd = window.EnsureHandle();

        var style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
        SetWindowLongPtr(hwnd, GWL_STYLE, (nint)((style & ~WS_POPUP) | WS_CHILD));

        if (SetParent(hwnd, _taskbar) == 0)
        {
            window.Close();
            return;
        }

        window.Closed += OnWindowClosed;
        _window = window;
        window.Show();
        UpdatePosition();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // Explorer destroyed the taskbar and our child window with it.
        // The timer or TaskbarCreated embeds a new window.
        if (ReferenceEquals(sender, _window))
        {
            _window = null;
        }
    }

    private void Tick()
    {
        if (_window is null)
        {
            TryEmbed();
        }
        else
        {
            UpdatePosition();
        }
    }

    private nint MessageHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == (int)_taskbarCreatedMessage)
        {
            CloseWindow();
            TryEmbed();
        }
        else if (msg is WM_DISPLAYCHANGE or WM_SETTINGCHANGE)
        {
            _messageWindow.Dispatcher.BeginInvoke(UpdatePosition, DispatcherPriority.Background);
        }

        return 0;
    }

    private void CloseWindow()
    {
        var window = _window;
        _window = null;
        window?.Close();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        CloseWindow();
        _messageWindow.RemoveHook(MessageHook);
        _messageWindow.Dispose();
    }
}
