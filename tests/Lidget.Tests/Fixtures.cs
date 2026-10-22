namespace Lidget.Tests;

internal static class Fixtures
{
    public static string Path(string relative) => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", relative);

    public static string Read(string relative) => File.ReadAllText(Path(relative));
}
