using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using static Lidget.App.Shell.NativeMethods;

namespace Lidget.App.Shell;

/// <summary>
/// Embeds widget content as a child window of the Windows 11 taskbar, left of the system tray.
/// A hidden top-level window receives <c>TaskbarCreated</c>, because Explorer destroys the
/// embedded child window when it restarts. If embedding is not possible, the host shows the
/// content in a floating pill window instead.
/// </summary>
internal sealed class TaskbarHost : IDisposable
{
    public const string ForceFallbackVariable = "LIDGET_FORCE_FALLBACK";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MissingTaskbarGrace = TimeSpan.FromSeconds(30);

    private readonly Func<FrameworkElement> _contentFactory;
    private readonly Action<FallbackPillWindow>? _onFallbackCreated;
    private readonly ILogger _logger;
    private readonly uint _taskbarCreatedMessage;
    private readonly HwndSource _messageWindow;
    private readonly DispatcherTimer _timer;
    private readonly bool _forceFallback;
    private TaskbarWidgetWindow? _window;
    private FallbackPillWindow? _fallback;
    private nint _taskbar;
    private DateTimeOffset? _missingSince;
    private bool _disposed;

    public TaskbarHost(Func<FrameworkElement> contentFactory, ILogger logger, Action<FallbackPillWindow>? onFallbackCreated = null)
    {
        _contentFactory = contentFactory;
        _onFallbackCreated = onFallbackCreated;
        _logger = logger;
        _forceFallback = Environment.GetEnvironmentVariable(ForceFallbackVariable) == "1";
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

    /// <summary>Raised the first time the host shows the floating pill.</summary>
    public event EventHandler? FallbackShown;

    /// <summary>Distance between the widget and the tray, in device-independent pixels.</summary>
    public double TrayOffset { get; set; } = 4;

    public bool IsInFallback => _fallback is not null;

    /// <summary>The widget bounds in physical screen pixels, or null if no widget is visible.</summary>
    public ScreenRect? WidgetBounds
    {
        get
        {
            var hwnd = _window?.EnsureHandle() ?? _fallback?.Handle ?? 0;
            return hwnd != 0 && GetWindowRect(hwnd, out var rect)
                ? new ScreenRect(rect.Left, rect.Top, rect.Right, rect.Bottom)
                : null;
        }
    }

    public void Start()
    {
        Tick();
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

    private void Tick()
    {
        if (_disposed)
        {
            return;
        }

        if (_window is not null)
        {
            if (FindUsableTaskbar() == TaskbarState.Usable)
            {
                UpdatePosition();
                return;
            }

            CloseWindow();
        }

        switch (FindUsableTaskbar())
        {
            case TaskbarState.Usable when Embed():
                _missingSince = null;
                CloseFallback();
                break;
            case TaskbarState.Missing:
                _missingSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - _missingSince >= MissingTaskbarGrace)
                {
                    ShowFallback("the taskbar is not available");
                }

                break;
            default:
                ShowFallback(_forceFallback ? $"{ForceFallbackVariable} is set" : "the taskbar is not a bottom, horizontal taskbar");
                break;
        }
    }

    private enum TaskbarState
    {
        Usable,
        Missing,
        Unsupported,
    }

    private TaskbarState FindUsableTaskbar()
    {
        if (_forceFallback)
        {
            return TaskbarState.Unsupported;
        }

        _taskbar = FindWindow("Shell_TrayWnd", null);
        if (_taskbar == 0 || !GetWindowRect(_taskbar, out var rect))
        {
            return TaskbarState.Missing;
        }

        var monitor = MonitorInfo.FromPoint(rect.Left + (rect.Width / 2.0), rect.Top + (rect.Height / 2.0));
        var horizontal = rect.Width > rect.Height;
        var atBottom = rect.Top + (rect.Height / 2) > monitor.Bounds.Top + (monitor.Bounds.Height / 2);
        return horizontal && atBottom ? TaskbarState.Usable : TaskbarState.Unsupported;
    }

    private bool Embed()
    {
        var window = new TaskbarWidgetWindow(_contentFactory());
        var hwnd = window.EnsureHandle();

        var style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
        SetWindowLongPtr(hwnd, GWL_STYLE, (nint)((style & ~WS_POPUP) | WS_CHILD));

        if (SetParent(hwnd, _taskbar) == 0)
        {
            _logger.LogWarning("Could not embed the widget in the taskbar (error {Error})", System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
            window.Close();
            return false;
        }

        window.Closed += OnWindowClosed;
        _window = window;
        window.Show();
        UpdatePosition();
        _logger.LogInformation("Embedded the widget in the taskbar");
        return true;
    }

    private void ShowFallback(string reason)
    {
        if (_fallback is not null)
        {
            return;
        }

        _logger.LogWarning("Showing the floating widget because {Reason}", reason);
        _fallback = new FallbackPillWindow(_contentFactory());
        _onFallbackCreated?.Invoke(_fallback);
        _fallback.Show();
        FallbackShown?.Invoke(this, EventArgs.Empty);
    }

    private void CloseFallback()
    {
        var fallback = _fallback;
        _fallback = null;
        fallback?.Close();
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

    private nint MessageHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == (int)_taskbarCreatedMessage)
        {
            CloseWindow();
            Tick();
        }
        else if (msg is WM_DISPLAYCHANGE or WM_SETTINGCHANGE)
        {
            _messageWindow.Dispatcher.BeginInvoke(Tick, DispatcherPriority.Background);
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
        CloseFallback();
        _messageWindow.RemoveHook(MessageHook);
        _messageWindow.Dispose();
    }
}
