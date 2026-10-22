using Lidget.Core;

namespace Lidget.Tests;

public class AppInfoTests
{
    [Fact]
    public void DataDirectoryEndsWithAppName()
    {
        Assert.Equal(AppInfo.Name, Path.GetFileName(AppInfo.DataDirectory));
    }
}
