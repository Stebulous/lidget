using Lidget.Core;

namespace Lidget.Tests;

public class AppInfoTests
{
    [Fact]
    public void DataDirectoryIsOutsideTheInstallFolder()
    {
        if (Environment.GetEnvironmentVariable(AppInfo.DataDirectoryVariable) is { Length: > 0 })
        {
            return;
        }

        // Velopack deletes %LOCALAPPDATA%\Lidget when it installs or uninstalls (plan decision D40).
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var installFolder = Path.Combine(localAppData, AppInfo.PackageId) + Path.DirectorySeparatorChar;

        Assert.Equal(Path.Combine(localAppData, "LidgetData"), AppInfo.DataDirectory);
        Assert.False(AppInfo.DataDirectory.StartsWith(installFolder, StringComparison.OrdinalIgnoreCase));
    }
}
