namespace Lidget.Core;

/// <summary>
/// Product identity. "Lidget" is a working name: change it here before the first public release,
/// because the Velopack package ID cannot change after users install the app.
/// </summary>
public static class AppInfo
{
    public const string Name = "Lidget";

    public const string PackageId = "Lidget";

    /// <summary>Overrides <see cref="DataDirectory"/>, for example in tests.</summary>
    public const string DataDirectoryVariable = "LIDGET_DATA_DIR";

    public static string DataDirectory =>
        Environment.GetEnvironmentVariable(DataDirectoryVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
}
