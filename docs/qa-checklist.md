# QA checklist

Use this checklist to test a release candidate of Lidget on Windows 11.
The checks cover the shell integration and other behavior that unit tests cannot prove.
Record each result, with the date, the version, and the revision, in the evidence log of [the MVP plan](plans/lidget-mvp.md#evidence-log).

Some checks change the Claude Code settings and the start-with-Windows registry value of the test user.
Run them only on a computer where you can accept these changes.

## Prerequisites

- An installer from `./scripts/pack.ps1`, or from a draft GitHub release.
- Claude Code, signed in with a Pro or Max plan.
- The Codex CLI, signed in with `codex login`.
- No other copy of Lidget runs. Quit the Debug build first.

## Install and first start

1. Run `Lidget-win-Setup.exe`.
2. Start Lidget from the Start menu.

Expected result:

- Within 10 seconds, the widget shows a logo, a bar, and a percentage for each provider.
- The Start menu has a Lidget shortcut. The desktop has no Lidget shortcut.
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` has the value `Lidget`, with the path `%LOCALAPPDATA%\Lidget\current\Lidget.exe`.

## Widget

| Check | Expected result |
| --- | --- |
| Read the widget with the default settings | Each percentage is the share that is left of the tightest limit. A limit with little use has a full bar. |
| Hover over the widget | The tooltip shows the plan, each limit, and each reset countdown. |
| Wait 5 minutes | The values change when the provider values change. The countdowns match the provider to the minute. |
| Right-click the widget | The menu shows **Refresh**, **Settings**, and **Quit Lidget**. |
| Restart Explorer (`taskkill /f /im explorer.exe`, then `start explorer.exe`) | The widget comes back in the taskbar within 30 seconds. |
| Change the display scale to 100%, 150%, and 200% | At each scale, the widget stays in the taskbar, next to the system tray. If the widget would cover taskbar buttons, it moves to the bottom-right corner of the screen. It goes back into the taskbar when there is space again. |
| Set the taskbar alignment to left | The widget stays at the right side of the taskbar, left of the Widgets button. |
| Turn on **Automatically hide the taskbar** | The widget hides and shows with the taskbar. |
| Change the Windows mode from dark to light | The widget text and bars stay readable on the light taskbar. |
| Start Lidget with `LIDGET_FORCE_FALLBACK=1` | A toast says "Lidget could not join the taskbar". The widget shows in the bottom-right corner. |

## Flyout

| Check | Expected result |
| --- | --- |
| Click the widget | The flyout opens above the widget. |
| Click the widget again, or press Esc | The flyout closes. |
| Click the refresh button | A progress ring shows, and the **Updated** time changes to "just now". |
| Read the cost tiles | Each provider shows Today, 7 days, and 30 days, and the label "API-equivalent estimate". |
| Select **24 h** and **7 d** in the history chart | The chart shows each limit for the selected range. |

## Settings

| Check | Expected result |
| --- | --- |
| Turn a provider off | The provider goes away from the widget and the flyout at once. |
| Change **Show limits as** to Used, and then back to Remaining | The widget, the flyout, and the chart change at once. The flyout labels end in "used" or "left". |
| Change **Start with Windows** | The `Run` value goes away or comes back. |
| Turn on **Read limits from the Claude Code statusline** | `~/.claude/settings.json` has a `statusLine` command for `lidget-statusline.exe`. Your original statusline still shows in Claude Code. |
| Send a message in Claude Code with the feed on | The Claude values in Lidget change within 10 seconds. |
| Turn off **Read limits from the Claude Code statusline** | `~/.claude/settings.json` has your original `statusLine` setting again. |
| Send a message to Claude in T3 Code, with **Read limits from T3 Code** on | Within 10 seconds, the Claude values in Lidget change. The flyout shows "Updated just now". |
| Turn off **Read limits from T3 Code**, and send a message in T3 Code | The Claude values change only at the next endpoint request. |
| Select **Open data folder** | File Explorer opens `%LOCALAPPDATA%\LidgetData`. |

## Notifications

| Check | Expected result |
| --- | --- |
| A limit passes 80%, and then 95% | One toast shows for each threshold. |
| Restart Lidget while a limit is above a threshold | No toast shows again for that threshold. |
| Turn on **Notify when a limit resets**, and wait for a limit window that passed a threshold to reset | One toast says that the limit reset. |
| Click a toast | The flyout opens. |

## Update

1. Make a second package with a higher version, for example `./scripts/pack.ps1 -Version 0.1.1`.
2. Start the installed Lidget with `LIDGET_UPDATE_FEED` set to the full path of `artifacts\releases`.
3. Wait until the log in `%LOCALAPPDATA%\LidgetData\logs` says "is ready for the next start".
4. Quit Lidget, and start it from the Start menu.

Expected result: the log says "Starting Lidget" with the new version.

## Uninstall

1. In Settings, turn on **Read limits from the Claude Code statusline**.
2. Uninstall Lidget in **Settings** > **Apps** > **Installed apps**.

Expected result:

- `%LOCALAPPDATA%\Lidget` and the Start menu shortcut are gone.
- The `Run` value `Lidget` is gone.
- `~/.claude/settings.json` has your original `statusLine` setting.
- `%LOCALAPPDATA%\LidgetData` still exists.

## One-week check

Use Lidget for one week, with normal use of Claude Code and the Codex CLI.

Expected result: Claude Code and the Codex CLI stay signed in, and Lidget shows current values for the full week.
