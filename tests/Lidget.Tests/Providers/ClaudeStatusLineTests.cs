using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lidget.Core;
using Lidget.Core.Models;
using Lidget.Core.StatusLine;
using Lidget.Providers.Claude;

namespace Lidget.Tests.Providers;

public class ClaudeStatusLineTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string SettingsPath => _dir.File("claude/settings.json");

    private string OriginalPath => _dir.File("data/statusline-original.json");

    private ClaudeStatusLineInstaller Installer => new(SettingsPath, OriginalPath);

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, json);
    }

    private JsonObject ReadSettings() => JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();

    [Fact]
    public void EnableAndDisableWithoutOriginalStatusLine()
    {
        WriteSettings("""{ "permissions": { "allow": ["Bash(ls:*)"] }, "effortLevel": "high" }""");

        Installer.Enable(@"C:\apps\lidget-statusline.exe");

        var enabled = ReadSettings();
        Assert.Equal("command", enabled["statusLine"]!["type"]!.GetValue<string>());
        Assert.Equal("C:/apps/lidget-statusline.exe", enabled["statusLine"]!["command"]!.GetValue<string>());
        Assert.Equal("high", enabled["effortLevel"]!.GetValue<string>());
        Assert.True(Installer.IsEnabled());
        Assert.True(File.Exists(SettingsPath + ".lidget-backup"));

        Installer.Disable();

        var disabled = ReadSettings();
        Assert.False(disabled.ContainsKey("statusLine"));
        Assert.Equal("Bash(ls:*)", disabled["permissions"]!["allow"]![0]!.GetValue<string>());
        Assert.False(File.Exists(OriginalPath));
        Assert.False(Installer.IsEnabled());
    }

    [Fact]
    public void EnableKeepsDisplayOptionsAndDisableRestoresOriginal()
    {
        WriteSettings("""{ "statusLine": { "type": "command", "command": "bash -c 'echo <hi>'", "padding": 2 } }""");

        Installer.Enable(@"C:\apps\lidget-statusline.exe");

        Assert.Equal(2, ReadSettings()["statusLine"]!["padding"]!.GetValue<int>());

        Installer.Disable();

        var restored = ReadSettings()["statusLine"]!;
        Assert.Equal("bash -c 'echo <hi>'", restored["command"]!.GetValue<string>());
        Assert.Contains("'echo <hi>'", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void EnableTwiceKeepsFirstOriginal()
    {
        WriteSettings("""{ "statusLine": { "type": "command", "command": "my-line" } }""");

        Installer.Enable(@"C:\apps\lidget-statusline.exe");
        Installer.Enable(@"C:\apps\lidget-statusline.exe");
        Installer.Disable();

        Assert.Equal("my-line", ReadSettings()["statusLine"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void DisableKeepsStatusLineThatUserChanged()
    {
        WriteSettings("""{}""");
        Installer.Enable(@"C:\apps\lidget-statusline.exe");
        var settings = ReadSettings();
        settings["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = "user-choice" };
        File.WriteAllText(SettingsPath, settings.ToJsonString());

        Installer.Disable();

        Assert.Equal("user-choice", ReadSettings()["statusLine"]!["command"]!.GetValue<string>());
        Assert.False(File.Exists(OriginalPath));
    }

    [Fact]
    public void EnableCreatesMissingSettingsFile()
    {
        Installer.Enable(@"C:\apps\lidget-statusline.exe");

        Assert.True(Installer.IsEnabled());
    }

    [Fact]
    public void CommandUsesHomeRelativePathUnderProfile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal("~/AppData/Local/Lidget/lidget-statusline.exe",
            ClaudeStatusLineInstaller.CommandFor(Path.Combine(home, "AppData", "Local", "Lidget", "lidget-statusline.exe")));
        Assert.Equal("\"D:/My Apps/lidget-statusline.exe\"", ClaudeStatusLineInstaller.CommandFor(@"D:\My Apps\lidget-statusline.exe"));
    }

    [Fact]
    public void SourceMapsWindowsAndDropsExpiredOnes()
    {
        var record = new JsonObject
        {
            ["observedAt"] = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
            ["rate_limits"] = new JsonObject
            {
                ["five_hour"] = new JsonObject { ["used_percentage"] = 23.5, ["resets_at"] = Now.AddHours(2).ToUnixTimeSeconds() },
                ["seven_day"] = new JsonObject { ["used_percentage"] = 41.2, ["resets_at"] = Now.AddMinutes(-5).ToUnixTimeSeconds() },
            },
        };
        using var document = JsonDocument.Parse(record.ToJsonString());

        var limits = ClaudeStatusLineSource.Map(document.RootElement, Now);

        var session = Assert.Single(limits);
        Assert.Equal(ClaudeUsageMapper.SessionId, session.Id);
        Assert.Equal(23.5, session.UsedPercent);
        Assert.Equal("statusline", session.Source);
        Assert.Equal(Now.AddMinutes(-1), session.ObservedAt);
    }

    [Fact]
    public void CombinePrefersNewerReading()
    {
        var endpoint = new ProviderSnapshot(ProviderIds.Claude, ProviderStatus.Ok,
        [
            new(ClaudeUsageMapper.SessionId, LimitKind.Session, "Session (5h)", 30, null, "endpoint", Now.AddMinutes(-3)),
            new(ClaudeUsageMapper.WeeklyId, LimitKind.Weekly, "Weekly", 50, null, "endpoint", Now.AddMinutes(-3)),
        ], null, Now.AddMinutes(-3));
        LimitWindow[] statusLine =
        [
            new(ClaudeUsageMapper.SessionId, LimitKind.Session, "Session (5h)", 35, null, "statusline", Now),
            new(ClaudeUsageMapper.WeeklyId, LimitKind.Weekly, "Weekly", 49, null, "statusline", Now.AddMinutes(-10)),
        ];

        var combined = ClaudeProvider.Combine(endpoint, statusLine);

        Assert.Equal(ProviderStatus.Ok, combined.Status);
        Assert.Equal([35d, 50d], combined.Limits.Select(l => l.UsedPercent));
        Assert.Equal(Now, combined.UpdatedAt);
    }

    [Fact]
    public void CombineWithFailedEndpointIsStale()
    {
        var failed = ProviderSnapshot.Failed(ProviderIds.Claude, ProviderStatus.Error, "HTTP 500", Now);
        LimitWindow[] statusLine = [new(ClaudeUsageMapper.SessionId, LimitKind.Session, "Session (5h)", 35, null, "statusline", Now.AddMinutes(-2))];

        var combined = ClaudeProvider.Combine(failed, statusLine);

        Assert.Equal(ProviderStatus.Stale, combined.Status);
        Assert.Single(combined.Limits);
        Assert.Equal("HTTP 500", combined.StatusMessage);
    }

    [Fact]
    public async Task WrapperRecordsFeedAndRunsOriginalCommand()
    {
        var dataDir = _dir.File("data");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(StatusLineFiles.OriginalPath(dataDir), """{"statusLine":{"type":"command","command":"cat"}}""");
        var input = """{"model":{"display_name":"Opus"},"rate_limits":{"five_hour":{"used_percentage":12,"resets_at":1792663869}}}""";

        var (output, exitCode) = await RunWrapperAsync(dataDir, input);

        Assert.Equal(0, exitCode);
        Assert.Equal(input, output.TrimEnd());
        var feed = JsonNode.Parse(File.ReadAllText(StatusLineFiles.FeedPath(dataDir)))!;
        Assert.Equal(12, feed["rate_limits"]!["five_hour"]!["used_percentage"]!.GetValue<int>());
        Assert.True(feed["observedAt"]!.GetValue<long>() > 0);
    }

    [Fact]
    public async Task WrapperWithoutOriginalPrintsNothing()
    {
        var dataDir = _dir.File("data");

        var (output, exitCode) = await RunWrapperAsync(dataDir, "not json");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.False(File.Exists(StatusLineFiles.FeedPath(dataDir)));
    }

    private static async Task<(string Output, int ExitCode)> RunWrapperAsync(string dataDir, string input)
    {
        // tests/Lidget.Tests/bin/<Configuration>/net10.0 -> src/Lidget.StatusLine/bin/<Configuration>/net10.0
        var testBin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = testBin.Parent!.Name;
        var repoRoot = testBin.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        var wrapper = Path.Combine(repoRoot, "src", "Lidget.StatusLine", "bin", configuration, "net10.0", StatusLineFiles.WrapperFileName);
        Assert.True(File.Exists(wrapper), $"Build the wrapper first: {wrapper}");

        var startInfo = new ProcessStartInfo(wrapper)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            Environment = { [AppInfo.DataDirectoryVariable] = dataDir },
        };
        using var process = Process.Start(startInfo)!;
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (output, process.ExitCode);
    }
}
