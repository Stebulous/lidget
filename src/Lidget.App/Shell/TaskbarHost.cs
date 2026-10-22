using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using static Lidget.App.Shell.NativeMethods;

namespace Lidget.App.Shell;

/// <summary>
/// Embeds widget content as a child window of the Windows 11 taskbar, left of the system tray.
/// A hidden top-level window receives <c>TaskbarCreated</c>, because Explorer destroys the
/// embedded child window when it restarts. If embedding is not possible, or the widget would
/// cover taskbar buttons, the host shows the content in a floating pill window instead.
/// </summary>
internal sealed class TaskbarHost : IDisposable
{
    public const string ForceFallbackVariable = "LIDGET_FORCE_FALLBACK";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MissingTaskbarGrace = TimeSpan.FromSeconds(30);

    /// <summary>Minimum space between the taskbar buttons and the widget, in device-independent pixels.</summary>
    private const double ButtonGap = 8;

    /// <summary>
    /// Extra space that the taskbar must have before the widget goes back from the pill,
    /// in device-independent pixels. It prevents a switch at each small change.
    /// </summary>
    private const double ReturnMargin = 24;

    private readonly Func<FrameworkElement> _contentFactory;
    private readonly Action<FallbackPillWindow>? _onFallbackCreated;
    private readonly ILogger _logger;
    private readonly uint _taskbarCreatedMessage;
    private readonly HwndSource _messageWindow;
    private readonly DispatcherTimer _timer;
    private readonly bool _forceFallback;
    private readonly TaskbarButtonsProbe? _buttons;
    private TaskbarWidgetWindow? _window;
    private nint _windowHandle;
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

        if (!_forceFallback)
        {
            _buttons = new TaskbarButtonsProbe(logger);
            _buttons.Changed += (_, _) => _timer.Dispatcher.BeginInvoke(Tick, DispatcherPriority.Background);
        }
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
            var hwnd = IsWindowAlive() ? _windowHandle : _fallback?.Handle ?? 0;
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
        if (!TryPlaceWindow())
        {
            _timer.Dispatcher.BeginInvoke(Tick, DispatcherPriority.Background);
        }
    }

    /// <summary>Places the embedded window left of the tray. Returns false if it would cover taskbar buttons.</summary>
    private bool TryPlaceWindow()
    {
        if (!IsWindowAlive())
        {
            return true;
        }

        var hwnd = _windowHandle;
        if (!GetWindowRect(_taskbar, out var taskbarRect))
        {
            return true;
        }

        var anchorRight = AnchorRight(taskbarRect) - taskbarRect.Left;
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var content = (FrameworkElement)_window!.Content;
        content.Measure(new Size(double.PositiveInfinity, taskbarRect.Height / scale));

        var width = (int)Math.Ceiling(content.DesiredSize.Width * scale);
        var x = anchorRight - (int)Math.Round(TrayOffset * scale) - width;
        if (_buttons?.Buttons.LeftGroupRight is { } buttonsRight && taskbarRect.Left + x < buttonsRight + (ButtonGap * scale))
        {
            return false;
        }

        SetWindowPos(hwnd, HWND_TOP, x, 0, width, taskbarRect.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        return true;
    }

    /// <summary>
    /// Whether the widget fits between the taskbar buttons and the tray. While the pill shows,
    /// the space must also include <see cref="ReturnMargin"/>.
    /// </summary>
    private bool HasRoomForWidget()
    {
        if (_buttons?.Buttons.LeftGroupRight is not { } buttonsRight || _fallback is null || !GetWindowRect(_taskbar, out var taskbarRect))
        {
            return true;
        }

        var anchorRight = AnchorRight(taskbarRect);
        var scale = GetDpiForWindow(_taskbar) / 96.0;
        var width = _fallback.Widget.ActualWidth * scale;
        return anchorRight - (TrayOffset * scale) - width >= buttonsRight + ((ButtonGap + ReturnMargin) * scale);
    }

    /// <summary>
    /// The screen x coordinate that the widget ends at, before <see cref="TrayOffset"/>: the left edge of
    /// the tray, or of a Widgets button between the app buttons and the tray.
    /// </summary>
    private int AnchorRight(RECT taskbarRect)
    {
        var tray = FindWindowEx(_taskbar, 0, "TrayNotifyWnd", null);
        var anchor = tray != 0 && GetWindowRect(tray, out var trayRect) ? trayRect.Left : taskbarRect.Right;
        return _buttons?.Buttons.RightGroupLeft is { } widgetsLeft && widgetsLeft < anchor ? widgetsLeft : anchor;
    }

    private void Tick()
    {
        if (_disposed)
        {
            return;
        }

        if (_window is not null)
        {
            if (FindUsableTaskbar() == TaskbarState.Usable && IsWindowAlive())
            {
                if (TryPlaceWindow())
                {
                    return;
                }

                CloseWindow();
                ShowFallback("the widget would cover taskbar buttons");
                return;
            }

            CloseWindow();
        }

        switch (FindUsableTaskbar())
        {
            case TaskbarState.Usable:
                _missingSince = null;
                if (!HasRoomForWidget())
                {
                    break;
                }

                switch (Embed())
                {
                    case EmbedResult.Embedded:
                        CloseFallback();
                        break;
                    case EmbedResult.NoRoom:
                        ShowFallback("the widget would cover taskbar buttons");
                        break;
                    default:
                        ShowFallback("Windows did not accept the widget in the taskbar");
                        break;
                }

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

    private enum EmbedResult
    {
        Embedded,
        NoRoom,
        Failed,
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

    private EmbedResult Embed()
    {
        var window = new TaskbarWidgetWindow(_contentFactory());
        var hwnd = window.EnsureHandle();

        var style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
        SetWindowLongPtr(hwnd, GWL_STYLE, (nint)((style & ~WS_POPUP) | WS_CHILD));

        if (SetParent(hwnd, _taskbar) == 0)
        {
            _logger.LogWarning("Could not embed the widget in the taskbar (error {Error})", System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
            window.Close();
            return EmbedResult.Failed;
        }

        window.Closed += OnWindowClosed;
        _window = window;
        _windowHandle = hwnd;
        window.Show();
        if (!TryPlaceWindow())
        {
            CloseWindow();
            return EmbedResult.NoRoom;
        }

        _logger.LogInformation("Embedded the widget in the taskbar");
        return EmbedResult.Embedded;
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
            _windowHandle = 0;
        }
    }

    /// <summary>
    /// Whether the embedded window still exists in the current taskbar. When Explorer exits, Windows
    /// destroys the child window without a <see cref="Window.Closed"/> event, so the handle is the truth.
    /// </summary>
    private bool IsWindowAlive() =>
        _window is not null && _windowHandle != 0 && IsWindow(_windowHandle) && IsWindow(_taskbar) && GetParent(_windowHandle) == _taskbar;

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
            _messageWindow.Dispatcher.BeginInvoke(() => _fallback?.Reposition(), DispatcherPriority.Background);
        }

        return 0;
    }

    private void CloseWindow()
    {
        var window = _window;
        var handle = _windowHandle;
        _window = null;
        _windowHandle = 0;
        if (window is null)
        {
            return;
        }

        window.Closed -= OnWindowClosed;
        try
        {
            if (IsWindow(handle))
            {
                window.Close();
            }
        }
        catch (InvalidOperationException ex)
        {
            // WPF already treats the destroyed window as closed.
            _logger.LogDebug(ex, "The widget window was already closed");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _buttons?.Dispose();
        CloseWindow();
        CloseFallback();
        _messageWindow.RemoveHook(MessageHook);
        _messageWindow.Dispose();
    }
}
