using System.Runtime.InteropServices;

namespace Lidget.Providers.Codex;

/// <summary>
/// Finds the Codex CLI on <c>PATH</c>. For the npm <c>codex.cmd</c> shim, it returns the native
/// <c>codex.exe</c> that the npm package contains, because the shim needs <c>node</c> on <c>PATH</c>.
/// </summary>
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
                    return name == "codex.exe" ? candidate : FindNpmBinary(Path.GetDirectoryName(candidate)!) ?? candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Returns the native binary of an npm global install of <c>@openai/codex</c>, if it exists.</summary>
    internal static string? FindNpmBinary(string shimDirectory, Architecture? architecture = null)
    {
        var (packageArch, triple) = (architecture ?? RuntimeInformation.OSArchitecture) switch
        {
            Architecture.X64 => ("x64", "x86_64-pc-windows-msvc"),
            Architecture.Arm64 => ("arm64", "aarch64-pc-windows-msvc"),
            _ => (null, null),
        };
        if (packageArch is null)
        {
            return null;
        }

        var package = Path.Combine(shimDirectory, "node_modules", "@openai", "codex");
        var platformPackage = $"codex-win32-{packageArch}";
        string[] vendorRoots =
        [
            Path.Combine(package, "node_modules", "@openai", platformPackage, "vendor"),
            Path.Combine(shimDirectory, "node_modules", "@openai", platformPackage, "vendor"),
            Path.Combine(package, "vendor"),
        ];

        return vendorRoots
            .Select(root => Path.Combine(root, triple!, "bin", "codex.exe"))
            .FirstOrDefault(File.Exists);
    }
}
