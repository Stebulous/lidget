using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using static Lidget.App.Shell.NativeMethods;

namespace Lidget.App.Shell;

/// <summary>
/// Taskbar button edges in physical screen pixels. <see cref="LeftGroupRight"/> is the right edge of the
/// Start, search, task view, and app buttons. <see cref="RightGroupLeft"/> is the left edge of a Widgets
/// button at the right side of the taskbar, which Windows puts there when the taskbar is left-aligned.
/// </summary>
internal sealed record TaskbarButtons(int? LeftGroupRight, int? RightGroupLeft);

/// <summary>
/// Finds the taskbar buttons with UI Automation, so that the widget does not cover them.
/// The probe runs on a background thread: UI Automation calls from the UI thread can block,
/// because the widget is part of the taskbar tree.
/// </summary>
internal sealed class TaskbarButtonsProbe : IDisposable
{
    private const string WidgetsButtonId = "WidgetsButton";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private static readonly TaskbarButtons None = new(null, null);

    private static readonly Condition ButtonCondition = new OrCondition(
        new PropertyCondition(AutomationElement.ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer"),
        new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"),
        new PropertyCondition(AutomationElement.AutomationIdProperty, "SearchButton"),
        new PropertyCondition(AutomationElement.AutomationIdProperty, "SearchBoxButton"),
        new PropertyCondition(AutomationElement.AutomationIdProperty, "TaskViewButton"),
        new PropertyCondition(AutomationElement.AutomationIdProperty, WidgetsButtonId));

    private readonly ILogger _logger;
    private readonly ManualResetEvent _stopping = new(false);
    private nint _taskbar;
    private AutomationElement? _frame;
    private TaskbarButtons _buttons = None;

    public TaskbarButtonsProbe(ILogger logger)
    {
        _logger = logger;
        var thread = new Thread(Run) { IsBackground = true, Name = "Lidget taskbar probe" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// <summary>Raised on the probe thread when <see cref="Buttons"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The latest button edges. Both edges are null when the probe cannot read the taskbar.</summary>
    public TaskbarButtons Buttons => Volatile.Read(ref _buttons);

    private void Run()
    {
        // The app manifest declares per-monitor DPI awareness, so bounds are in physical pixels,
        // the same unit as GetWindowRect in TaskbarHost.
        do
        {
            var buttons = Probe();
            if (buttons != _buttons)
            {
                Volatile.Write(ref _buttons, buttons);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        while (!_stopping.WaitOne(Interval));
    }

    private TaskbarButtons Probe()
    {
        try
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar == 0 || !GetWindowRect(taskbar, out var taskbarRect))
            {
                _frame = null;
                return None;
            }

            if (taskbar != _taskbar || _frame is null)
            {
                _taskbar = taskbar;
                _frame = AutomationElement.FromHandle(taskbar).FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "TaskbarFrame"));
            }

            var elements = _frame?.FindAll(TreeScope.Descendants, ButtonCondition);
            if (elements is null)
            {
                return None;
            }

            var center = taskbarRect.Left + (taskbarRect.Width / 2.0);
            double? leftGroupRight = null;
            double? rightGroupLeft = null;
            foreach (AutomationElement element in elements)
            {
                var bounds = element.Current.BoundingRectangle;
                if (bounds.IsEmpty || bounds.Width <= 0)
                {
                    continue;
                }

                if (element.Current.AutomationId == WidgetsButtonId && bounds.Left > center)
                {
                    rightGroupLeft = Math.Min(rightGroupLeft ?? double.MaxValue, bounds.Left);
                }
                else
                {
                    leftGroupRight = Math.Max(leftGroupRight ?? double.MinValue, bounds.Right);
                }
            }

            return new TaskbarButtons(
                leftGroupRight is { } right ? (int)Math.Ceiling(right) : null,
                rightGroupLeft is { } left ? (int)Math.Floor(left) : null);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            // Explorer restarted or changed the taskbar. Find the frame again at the next probe.
            _logger.LogDebug(ex, "Could not read the taskbar buttons");
            _frame = null;
            return None;
        }
    }

    public void Dispose()
    {
        // The thread exits at its next wait. The event is not disposed, because the thread can still wait on it.
        _stopping.Set();
    }
}
