using System.Windows;
using Lidget.Core;

namespace Lidget.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new Window { Title = AppInfo.Name, Width = 480, Height = 320 };
        window.Show();
    }
}
