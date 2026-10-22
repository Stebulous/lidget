using Lidget.Core;
using Microsoft.Win32;

namespace Lidget.App.Services;

/// <summary>Starts Lidget at sign-in through the current user's <c>Run</c> registry key (plan decision D29).</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(AppInfo.Name, $"\"{executablePath}\"");
        }
        else if (key.GetValue(AppInfo.Name) is not null)
        {
            key.DeleteValue(AppInfo.Name);
        }
    }

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppInfo.Name) is string;
    }
}
