using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lidget.App.Shell;
using Lidget.Core;

namespace Lidget.App;

public partial class App : Application
{
    private TaskbarHost? _taskbarHost;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _taskbarHost = new TaskbarHost(CreatePlaceholder);
        _taskbarHost.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _taskbarHost?.Dispose();
        base.OnExit(e);
    }

    // Spike placeholder (T0.4). T2.2 replaces it with the widget view.
    private FrameworkElement CreatePlaceholder()
    {
        var quit = new MenuItem { Header = "Quit" };
        quit.Click += (_, _) => Shutdown();

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x3B, 0x82, 0xF6)),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 6, 0, 6),
            Padding = new Thickness(10, 0, 10, 0),
            ContextMenu = new ContextMenu { Items = { quit } },
            Child = new TextBlock
            {
                Text = AppInfo.Name + " 62%",
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
            },
        };
    }
}
