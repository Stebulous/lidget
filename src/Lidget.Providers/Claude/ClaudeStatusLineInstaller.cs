using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lidget.Core.Storage;

namespace Lidget.Providers.Claude;

/// <summary>
/// Turns the Claude statusline feed on and off by editing the <c>statusLine</c> key in the
/// Claude Code user settings. Every other key in that file stays unchanged.
/// </summary>
public sealed class ClaudeStatusLineInstaller(string claudeSettingsPath, string originalPath)
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ClaudeStatusLineInstaller CreateDefault(string dataDirectory) => new(
        Path.Combine(ClaudeCredentialStore.ResolveClaudeHome(), "settings.json"),
        Lidget.Core.StatusLine.StatusLineFiles.OriginalPath(dataDirectory));

    public string SettingsPath => claudeSettingsPath;

    public bool IsEnabled() => IsWrapper(ReadSettings()?["statusLine"]);

    /// <summary>Replaces the statusline command with the wrapper and saves the original setting.</summary>
    public void Enable(string wrapperPath)
    {
        var settings = ReadSettings() ?? new JsonObject();
        var current = settings["statusLine"];
        if (IsWrapper(current))
        {
            return;
        }

        if (File.Exists(claudeSettingsPath))
        {
            File.Copy(claudeSettingsPath, claudeSettingsPath + ".lidget-backup", overwrite: true);
        }

        AtomicFile.WriteAllText(originalPath, new JsonObject { ["statusLine"] = current?.DeepClone() }.ToJsonString(Indented));

        // Keep display options such as padding and refreshInterval from the original setting.
        var replacement = current is JsonObject existing ? (JsonObject)existing.DeepClone() : new JsonObject();
        replacement["type"] = "command";
        replacement["command"] = CommandFor(wrapperPath);
        settings["statusLine"] = replacement;

        AtomicFile.WriteAllText(claudeSettingsPath, settings.ToJsonString(Indented));
    }

    /// <summary>Restores the saved statusline setting, if the wrapper is still configured.</summary>
    public void Disable()
    {
        var settings = ReadSettings();
        if (settings is not null && IsWrapper(settings["statusLine"]))
        {
            var original = File.Exists(originalPath) ? JsonNode.Parse(File.ReadAllText(originalPath))?["statusLine"] : null;
            if (original is null)
            {
                settings.Remove("statusLine");
            }
            else
            {
                settings["statusLine"] = original.DeepClone();
            }

            AtomicFile.WriteAllText(claudeSettingsPath, settings.ToJsonString(Indented));
        }

        // If the user replaced the wrapper, keep their setting and forget the saved one.
        if (File.Exists(originalPath))
        {
            File.Delete(originalPath);
        }
    }

    /// <summary>
    /// Builds a command that Git Bash and PowerShell both run. A <c>~/</c> path with forward slashes
    /// works in both shells, also when the user profile path contains spaces.
    /// </summary>
    public static string CommandFor(string wrapperPath)
    {
        var full = Path.GetFullPath(wrapperPath);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (full.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return "~/" + full[(home.Length + 1)..].Replace('\\', '/');
        }

        var forward = full.Replace('\\', '/');
        return forward.Contains(' ') ? $"\"{forward}\"" : forward;
    }

    private static bool IsWrapper(JsonNode? statusLine) =>
        statusLine?["command"] is JsonValue value && value.TryGetValue<string>(out var command)
        && command.Contains(Lidget.Core.StatusLine.StatusLineFiles.WrapperFileName, StringComparison.OrdinalIgnoreCase);

    private JsonObject? ReadSettings()
    {
        if (!File.Exists(claudeSettingsPath))
        {
            return null;
        }

        return JsonNode.Parse(File.ReadAllText(claudeSettingsPath), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject ?? throw new InvalidDataException("The Claude Code settings file does not contain a JSON object.");
    }
}
