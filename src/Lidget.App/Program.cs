using Lidget.App.Services;
using Lidget.Core;
using Lidget.Providers.Claude;
using Microsoft.Toolkit.Uwp.Notifications;
using Velopack;

namespace Lidget.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack must run first: install, update, and uninstall hooks start Lidget.exe and exit here.
        // A downloaded update is applied here, before the app starts.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => CleanUpBeforeUninstall())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>
    /// Removes what Lidget added outside its install folder. Velopack deletes the install folder after
    /// this hook. User data in <c>%LOCALAPPDATA%\LidgetData</c> stays (plan decision D40). The hook must not show UI.
    /// </summary>
    private static void CleanUpBeforeUninstall()
    {
        TryRun(() => ClaudeStatusLineInstaller.CreateDefault(AppInfo.DataDirectory).Disable());
        TryRun(() => StartupRegistration.Apply(enabled: false, executablePath: string.Empty));
        TryRun(ToastNotificationManagerCompat.Uninstall);
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Each cleanup step is independent. A failure must not stop the uninstall.
        }
    }
}
