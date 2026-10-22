namespace Lidget.Core;

/// <summary>
/// Product identity. "Lidget" is a working name: change it here before the first public release,
/// because the Velopack package ID cannot change after users install the app.
/// </summary>
public static class AppInfo
{
    public const string Name = "Lidget";

    public const string PackageId = "Lidget";

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
}
