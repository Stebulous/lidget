# User guide

Use this guide to install Lidget, read its values, change its settings, and correct common problems.
[Data sources and files](data-sources.md) lists what Lidget reads and writes.

## Requirements

- Windows 11 22H2 or later, with the taskbar at the bottom of the screen.
- For Claude: [Claude Code](https://code.claude.com), signed in with a Claude Pro or Max plan.
- For Codex: the [Codex CLI](https://github.com/openai/codex), signed in with `codex login` and a ChatGPT account.

Lidget shows the widget on the primary monitor only.

## Install

1. Download `Lidget-win-Setup.exe` from the [latest release](https://github.com/Stebulous/lidget/releases/latest).
2. Run the installer.
   The installer is not signed, so Microsoft Defender SmartScreen can show "Windows protected your PC".
   To continue, select **More info**, and then select **Run anyway**.
3. Start Lidget from the Start menu.

Expected result: within 10 seconds, the widget shows at the right side of the taskbar, near the system tray.

Lidget installs in `%LOCALAPPDATA%\Lidget` and adds a Start menu shortcut.
Lidget starts with Windows. To stop this, turn off **Start with Windows** in Settings.

## Update

Lidget checks GitHub Releases for a new version 24 hours apart.
Lidget downloads a new version in the background and installs it at the next start.

## Uninstall

1. Open **Settings** > **Apps** > **Installed apps** in Windows.
2. Select **Lidget**, and then select **Uninstall**.

The uninstaller removes the install folder, the Start menu shortcut, and the start-with-Windows value.
If the statusline feed is on, the uninstaller puts back your original Claude Code statusline setting.
The uninstaller keeps the data folder `%LOCALAPPDATA%\LidgetData`. You can delete it after the uninstall.

## Read the widget

The widget shows one logo, bar, and percentage for each provider that is on.

- By default, the percentage is the share that is left of the tightest limit.
  A full bar means that most of the limit is left.
- The bar is green until 75% of the limit is used, amber from 75%, and red from 90%.
  The color follows the used share in both display modes.
- A gray bar means that Lidget has no data for the provider yet.
- An exclamation mark on the logo means that the provider has a problem. The flyout shows the message.

Hover over the widget to see the plan, each limit, and each reset countdown.
Right-click the widget to open a menu with **Refresh**, **Settings**, and **Quit Lidget**.

If Lidget cannot join the taskbar, or the widget would cover taskbar buttons, Lidget shows the widget in a pill.
The pill is a small window in the bottom-right corner of the screen.
You can drag the pill to another location.
Lidget puts the widget back into the taskbar when there is space again.

## Read the flyout

Click the widget to open the flyout. Click the widget again, or press Esc, to close it.
When the flyout opens, Lidget refreshes data that is older than 60 seconds.

For each provider, the flyout shows:

- The plan, for example **Pro** or **Plus**, and the time of the last update.
- The session limit and the weekly limit, with a bar, the percentage, and the reset countdown.
  "Not started" means that no request has started the limit window yet.
- Cost estimates for today, the last 7 days, and the last 30 days.
  A cost estimate is the API price of the tokens in your local session logs. Your subscription price does not change.
- A message, if the provider has a problem.

The **History** chart shows each limit for the last 24 hours or the last 7 days.
Each step in a line is a new reading. A vertical jump is usually a limit reset.
A break in a line means that Lidget did not run, or had no new reading, for more than 45 minutes.
A dot is a single reading between two breaks.

The refresh button reads all providers again. The settings button opens Settings.

## Change settings

Settings save at once. Open Settings from the widget menu or from the flyout.

| Setting | Default | Effect |
| --- | --- | --- |
| **Claude**, **Codex** | On | Shows or hides the provider in the widget, the flyout, and the chart. |
| **Show limits as** | Remaining | Shows the share that is left, or the used share. It applies to the widget, the flyout, the chart, and notifications. |
| **Percentage in the taskbar** | Tightest limit | Selects the limit in the widget: the tightest limit, the session limit, or the weekly limit. |
| **Distance from the system tray** | 4 | Moves the widget left of the system tray, in device-independent pixels (0 to 400). |
| **Refresh interval** | 5 minutes | Sets the time between refreshes, from 1 to 30 minutes. |
| **Warn when usage passes these percentages** | 80, 95 | Sets the used percentages that show a notification. |
| **Notify when a limit resets** | Off | Shows a notification when a limit window that passed a threshold resets. |
| **Read limits from the Claude Code statusline** | Off | Turns on the statusline feed. See [Get current Claude values](#get-current-claude-values). |
| **Read limits from T3 Code** | On | Turns on the T3 Code feed. See [Get current Claude values](#get-current-claude-values). |
| **Start with Windows** | On | Starts Lidget when you sign in to Windows. |
| **Keep usage history** | 90 days | Deletes history older than this number of days (1 to 3650). |

**Open data folder** opens `%LOCALAPPDATA%\LidgetData` in File Explorer.

## Notifications

Lidget shows a notification when the used share of a limit passes a threshold.
Each threshold notifies one time in each limit window.
The thresholds use the used share in both display modes. For example, 80 means "80% used, 20% left".
Click a notification to open the flyout.

## Get current Claude values

Anthropic limits the number of requests to the Claude usage endpoint.
After a few requests, the endpoint can ask Lidget to wait for about an hour.
Lidget uses these sources to send fewer requests:

- **The response that Claude Code saved.** When Claude Code, or the Claude Code CLI that T3 Code starts, gets the usage limits, it saves them in `~/.claude.json`. Lidget reads that copy, and does not send a request while the copy is less than 15 minutes old. This source has no setting.
- **A feed.** A feed gives Lidget current Claude values after each response, between endpoint requests:

| Feed | Use it when | How it works |
| --- | --- | --- |
| T3 Code feed | You use Claude in T3 Code | T3 Code logs the Claude limits after each response. Lidget reads only those log entries. |
| Statusline feed | You use Claude Code in a terminal | Lidget wraps your statusline command. Claude Code sends the limits to the command after each response. |

Claude Code runs the statusline command only in its terminal interface.
T3 Code sessions do not run it, so use the T3 Code feed for them.

While a feed has current values, Lidget does not request the usage endpoint more than one time each hour.
[Usage endpoint requests](data-sources.md#usage-endpoint-requests) gives the exact rules.

The statusline feed edits the `statusLine` key in `~/.claude/settings.json`.
Lidget keeps the output of your original statusline command.
With a custom statusline, Claude Code hides most footer key hints.
A `statusLine` setting in a project overrides the user setting, so the feed does not run in that project.

## Troubleshooting

| Symptom | Check | Correction |
| --- | --- | --- |
| A notification says "Lidget could not join the taskbar" | The taskbar is at the bottom of the primary monitor. | Move the taskbar to the bottom. Lidget shows the pill until it can join the taskbar. |
| The pill shows after a display scale change | The widget would cover taskbar buttons. | Close some apps, or use a smaller scale. The widget goes back when there is space. |
| "Anthropic is limiting usage requests" | The usage endpoint returned HTTP 429, and Claude Code saved no response in the last hour. | Wait. Lidget retries after the time that Anthropic sends. Turn on a feed for current values. |
| "Sign in to Claude Code to show Claude limits" | `~/.claude/.credentials.json` exists. | Run `claude`, and then sign in. |
| "Run `codex login` to show Codex limits" | `codex login status` | Run `codex login` with a ChatGPT account. |
| "Install the Codex CLI to show Codex limits" | `codex --version` | Install the Codex CLI, and then restart Lidget. |
| Claude values update only every few minutes while you work | The feed setting for your tool is on. | Turn on the T3 Code feed or the statusline feed. |

Lidget writes logs to `%LOCALAPPDATA%\LidgetData\logs`. Logs do not contain tokens.
To add debug messages, start Lidget with the environment variable `LIDGET_DEBUG_LOG=1`.
Remove personal paths from a log before you attach it to an issue.
