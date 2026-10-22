// Claude Code statusline wrapper (plan task T1.6).
// 1. Read the statusline JSON from stdin.
// 2. Save its `rate_limits` object for Lidget.
// 3. Run the user's original statusline command with the same input and print its output.
// Errors in steps 1 and 2 must never change the output of step 3.
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Lidget.Core;
using Lidget.Core.StatusLine;

var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.InputEncoding = utf8;
Console.OutputEncoding = utf8;

string input;
using (var stdin = new StreamReader(Console.OpenStandardInput(), utf8))
{
    input = stdin.ReadToEnd();
}

try
{
    RecordRateLimits(input);
}
catch (Exception)
{
    // The feed is optional. The original statusline must still run.
}

return RunOriginal(input);

static void RecordRateLimits(string input)
{
    if (JsonNode.Parse(input) is not JsonObject root || root["rate_limits"] is not JsonObject rateLimits)
    {
        return;
    }

    var record = new JsonObject
    {
        ["observedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ["rate_limits"] = rateLimits.DeepClone(),
    };

    var path = StatusLineFiles.FeedPath(AppInfo.DataDirectory);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var temp = $"{path}.{Environment.ProcessId}.tmp";
    File.WriteAllText(temp, record.ToJsonString());
    File.Move(temp, path, overwrite: true);
}

static int RunOriginal(string input)
{
    string? command;
    try
    {
        var original = JsonNode.Parse(File.ReadAllText(StatusLineFiles.OriginalPath(AppInfo.DataDirectory)));
        command = original?["statusLine"]?["command"]?.GetValue<string>();
    }
    catch (Exception)
    {
        command = null;
    }

    if (string.IsNullOrWhiteSpace(command))
    {
        return 0;
    }

    // Claude Code runs statusline commands in Git Bash when it is installed, or else in PowerShell.
    var startInfo = FindGitBash() is { } bash
        ? new ProcessStartInfo(bash) { ArgumentList = { "-c", command } }
        : new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", command } };
    startInfo.RedirectStandardInput = true;
    startInfo.UseShellExecute = false;
    startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    using var process = Process.Start(startInfo);
    if (process is null)
    {
        return 1;
    }

    // Write stdin on another thread: a command that does not read its input must not block us.
    var writer = Task.Run(() =>
    {
        try
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
    });

    process.WaitForExit();
    writer.Wait(TimeSpan.FromSeconds(1));
    return process.ExitCode;
}

static string? FindGitBash()
{
    if (Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH") is { Length: > 0 } configured && File.Exists(configured))
    {
        return configured;
    }

    foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
        // Git for Windows puts git.exe in <root>\cmd and bash.exe in <root>\bin.
        if (File.Exists(Path.Combine(directory, "git.exe")) && Path.GetDirectoryName(directory.TrimEnd('\\', '/')) is { } root)
        {
            var candidate = Path.Combine(root, "bin", "bash.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
    return File.Exists(programFiles) ? programFiles : null;
}
