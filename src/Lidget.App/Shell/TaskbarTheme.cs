using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Lidget.App.Shell;

/// <summary>
/// Brushes for content inside the taskbar. The taskbar follows the Windows mode
/// (<c>SystemUsesLightTheme</c>), which can differ from the app mode that the flyout follows.
/// </summary>
internal static class TaskbarTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsLight { get; private set; }

    public static event EventHandler? Changed;

    public static void Start()
    {
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            {
                Application.Current.Dispatcher.BeginInvoke(Apply);
            }
        };
    }

    private static void Apply()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        IsLight = key?.GetValue("SystemUsesLightTheme") is int value && value != 0;

        var resources = Application.Current.Resources;
        resources["Taskbar.Foreground"] = Brush(IsLight ? "#E4000000" : "#FFFFFFFF");
        resources["Taskbar.Track"] = Brush(IsLight ? "#26000000" : "#33FFFFFF");
        resources["Taskbar.Hover"] = Brush(IsLight ? "#0F000000" : "#14FFFFFF");
        resources["Taskbar.Pressed"] = Brush(IsLight ? "#08000000" : "#0AFFFFFF");
        resources["Taskbar.Severity.Normal"] = Brush(IsLight ? "#0F7B0F" : "#6CCB5F");
        resources["Taskbar.Severity.Warning"] = Brush(IsLight ? "#9D5D00" : "#FCE100");
        resources["Taskbar.Severity.Critical"] = Brush(IsLight ? "#C42B1C" : "#FF99A4");
        resources["Taskbar.Muted"] = Brush(IsLight ? "#72000000" : "#8BFFFFFF");
        resources["Taskbar.PillBackground"] = Brush(IsLight ? "#F5F3F3F3" : "#F0202020");

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
