# Development

Use this guide to build, test, run, and package Lidget on Windows.
[Architecture](architecture.md) describes the projects. [CONTRIBUTING.md](../CONTRIBUTING.md) describes the change process.

## Prerequisites

- Windows 11 22H2 or later.
- The .NET 10 SDK. CI uses `10.0.x` in `.github/workflows/ci.yml`.
- For packaging only: the Visual Studio C++ build tools, for the NativeAOT publish of `lidget-statusline.exe`.

## Build and test

Run these commands from the repository root:

```powershell
dotnet build Lidget.sln
dotnet test Lidget.sln
```

`Directory.Build.props` treats warnings as errors.
CI runs the same commands in the Release configuration on each push and pull request.

The tests do not need a network connection, a Claude login, or a Codex login.
They use fixtures in `tests/Lidget.Tests/fixtures` and temporary folders.

## Run the Debug build

Only one copy of Lidget can run. Quit the installed Lidget before you start the Debug build.

```powershell
src\Lidget.App\bin\Debug\net10.0-windows10.0.22621.0\Lidget.exe
```

A Debug build does not check for updates.
At start, a Debug build does not change the start-with-Windows registry value.

To keep your real data unchanged, start the Debug build with a copy of the data folder:

```powershell
Copy-Item "$env:LOCALAPPDATA\LidgetData" "$env:TEMP\lidget-data" -Recurse
$env:LIDGET_DATA_DIR = "$env:TEMP\lidget-data"
src\Lidget.App\bin\Debug\net10.0-windows10.0.22621.0\Lidget.exe
```

The Debug build still reads the real Claude Code and Codex logins, and it can refresh the Claude token.

## Environment variables

| Variable | Effect |
| --- | --- |
| `LIDGET_DATA_DIR` | Uses a different data folder |
| `LIDGET_DEBUG_LOG=1` | Writes debug messages to the log |
| `LIDGET_FORCE_FALLBACK=1` | Shows the pill instead of the taskbar widget |
| `LIDGET_UPDATE_FEED` | Uses a folder or URL as the update feed, for example `artifacts\releases` |
| `CLAUDE_CONFIG_DIR`, `CODEX_HOME` | Change the Claude Code and Codex folders, as they do for the CLIs |

## Make an installer

```powershell
./scripts/pack.ps1 -Version 0.1.0
```

The script publishes `Lidget.exe` self-contained for `win-x64`, publishes `lidget-statusline.exe` with NativeAOT, and runs Velopack.
The installer and the update packages are in `artifacts\releases`. Git ignores the `artifacts` folder.

To test an update, make a second package with a higher version.
Then start the installed Lidget with `LIDGET_UPDATE_FEED` set to the full path of `artifacts\releases`.
[The QA checklist](qa-checklist.md#update) gives the steps.

## Add a limit source

To add a provider:

1. Implement `IUsageProvider` in `src/Lidget.Providers/<Provider>/`. Return a `ProviderSnapshot` for each status, and never throw for an expected failure.
2. Add an ID to `ProviderIds` and a setting to `LidgetSettings.IsProviderEnabled`.
3. Create the provider in `App.OnStartup`. Add its toggle to `SettingsViewModel` and `SettingsWindow.xaml`.
4. Add the `ProviderIcon.<id>` geometry, and an optional `ProviderBrush.<id>` brush, to `src/Lidget.App/Resources/ProviderIcons.xaml`.
5. Add fixture tests for the mapping and for each failure status.
6. Document the source and the files in [Data sources and files](data-sources.md).

To add a Claude feed, implement `IClaudeLimitFeed` and pass it to `ClaudeProvider` with its setting.

## Manual checks

Unit tests cannot prove the shell integration.
Before a release, do the checks in [the QA checklist](qa-checklist.md).
