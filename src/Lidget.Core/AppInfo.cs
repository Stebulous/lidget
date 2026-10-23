namespace Lidget.Core;

/// <summary>
/// Product identity (plan decision D42). Do not change the package ID: installed copies
/// update only from releases with the same Velopack package ID.
/// </summary>
public static class AppInfo
{
    public const string Name = "Lidget";

    public const string PackageId = "Lidget";

    /// <summary>
    /// The GitHub repository whose releases are the update feed. Installed copies update from its
    /// published releases.
    /// </summary>
    public const string RepositoryUrl = "https://github.com/Stebulous/lidget";

    /// <summary>Overrides the update feed with a folder or URL, for example a local feed to test updates.</summary>
    public const string UpdateFeedVariable = "LIDGET_UPDATE_FEED";

    /// <summary>Overrides <see cref="DataDirectory"/>, for example in tests.</summary>
    public const string DataDirectoryVariable = "LIDGET_DATA_DIR";

    /// <summary>
    /// User data: settings, history, caches, and logs, in <c>%LOCALAPPDATA%\LidgetData</c> (plan decision D40).
    /// Velopack installs the app in <c>%LOCALAPPDATA%\Lidget</c> and deletes that folder when it installs
    /// or uninstalls, so user data must not be in it.
    /// </summary>
    public static string DataDirectory =>
        Environment.GetEnvironmentVariable(DataDirectoryVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name + "Data");
}
