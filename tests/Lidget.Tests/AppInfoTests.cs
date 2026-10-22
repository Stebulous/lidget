using Lidget.Core;

namespace Lidget.Tests;

public class AppInfoTests
{
    [Fact]
    public void DataDirectoryIsInsideAppFolder()
    {
        if (Environment.GetEnvironmentVariable(AppInfo.DataDirectoryVariable) is { Length: > 0 })
        {
            return;
        }

        Assert.Equal("data", Path.GetFileName(AppInfo.DataDirectory));
        Assert.Equal(AppInfo.Name, Path.GetFileName(Path.GetDirectoryName(AppInfo.DataDirectory)));
    }
}
