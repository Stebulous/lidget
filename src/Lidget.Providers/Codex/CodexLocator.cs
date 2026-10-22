namespace Lidget.Providers.Codex;

/// <summary>Finds the Codex CLI on <c>PATH</c>, including the npm <c>codex.cmd</c> shim.</summary>
public static class CodexLocator
{
    private static readonly string[] WindowsNames = ["codex.exe", "codex.cmd", "codex.bat"];

    public static string? Find(string? pathVariable = null)
    {
        var path = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var names = OperatingSystem.IsWindows() ? WindowsNames : ["codex"];

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim('"'), name);
                }
                catch (ArgumentException)
                {
                    break; // Invalid characters in this PATH entry.
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
