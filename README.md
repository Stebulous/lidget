# Lidget

Lidget is a Windows 11 taskbar widget that shows the usage limits of AI subscription plans.
It tracks Claude (Pro and Max plans, through Claude Code) and Codex (ChatGPT plans, through the Codex CLI).
The name joins "limits" and "widget".

![The Lidget widget in the taskbar, with an even-pace marker on each bar](docs/images/widget.png)

![The Lidget flyout with limits, pace, cost estimates, and history](docs/images/flyout.png)

## Start here

| Your task | Read |
| --- | --- |
| Install and use Lidget | [User guide](docs/user-guide.md) |
| See what Lidget reads and writes | [Data sources and files](docs/data-sources.md) |
| Build Lidget or propose a change | [CONTRIBUTING.md](CONTRIBUTING.md) and [Development](docs/development.md) |
| Find another document | [Documentation index](docs/README.md) |

## Current behavior

- The widget shows the tightest limit of each provider as a bar and a percentage.
  By default, the percentage is the share of the limit that is left. A setting changes it to the used share.
  A tick on each bar marks the even-pace level. A setting removes the tick.
- A click on the widget opens a flyout. For each provider, the flyout shows:
  - The session limit (5 hours) and the weekly limit, with reset countdowns.
  - The [pace](docs/user-guide.md#read-the-pace) of each limit: on pace, in reserve, or in deficit.
    If a limit runs out before its reset at the current rate, an amber text shows the time until it runs out.
  - The API-equivalent cost for today, the last 7 days, and the last 30 days, from local session logs.
  - A history chart of the last 24 hours or 7 days.
- A notification shows when the used share of a limit passes 80% and 95%.
- Claude values update while you work, from the T3 Code logs or from the Claude Code statusline.
  Lidget also reads the usage response that Claude Code saves.
  Lidget then sends fewer requests to the Claude usage endpoint, which Anthropic rate-limits.
- If Lidget cannot join the taskbar, or the widget would cover taskbar buttons, the widget moves to a small window in the screen corner.
- Lidget checks GitHub Releases for updates every 24 hours, and installs a downloaded update at the next start.

## Install

1. Download `Lidget-win-Setup.exe` from the [latest release](https://github.com/Stebulous/lidget/releases/latest).
2. Run the installer. It is not signed, so SmartScreen can show a warning. Select **More info**, and then **Run anyway**.

Lidget needs Windows 11 with the taskbar at the bottom of the screen.
For Claude values, sign in to Claude Code with a Pro or Max plan.
For Codex values, install the Codex CLI and run `codex login`.
The [user guide](docs/user-guide.md) has the full steps.

## Build and test

Prerequisites: Windows 11 and the .NET 10 SDK.

```powershell
dotnet build Lidget.sln
dotnet test Lidget.sln
```

[Development](docs/development.md) describes the Debug build, the environment variables, and packaging.

## License

Lidget uses the [MIT license](LICENSE).
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) lists the code, data, and packages from other projects.
