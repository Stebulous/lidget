# Data sources and files

This page lists each source that Lidget reads, each file that Lidget writes, and the refresh rules.
The provider code in `src/Lidget.Providers` controls this behavior.

Lidget reads the logins of Claude Code and the Codex CLI on this computer.
Lidget does not send data to a server other than the providers and GitHub.

## Claude

| Source | When | Notes |
| --- | --- | --- |
| `https://api.anthropic.com/api/oauth/usage` | At a refresh, when no source has a recent response. See [Usage endpoint requests](#usage-endpoint-requests). | Uses the Claude Code login in `~/.claude/.credentials.json`. Anthropic does not document this endpoint, and can change it without notice. |
| The usage response that Claude Code saved, in `~/.claude.json` | At each refresh | Each Claude Code process saves its last successful usage request as `cachedUsageUtilization`. This includes the CLI that T3 Code starts to check the limits. Lidget uses a copy that is less than 1 hour old and that belongs to the signed-in account. The key is undocumented, and Lidget only reads the file. |
| T3 Code provider logs, `~/.t3/userdata/logs/provider/events.*.log` | When the logs change, if **Read limits from T3 Code** is on (default) | Lidget reads only the `rate_limit_event` entries of Claude sessions. It ignores all other log content. |
| The Claude Code statusline, through `lidget-statusline.exe` | After each Claude Code response in a terminal, if **Read limits from the Claude Code statusline** is on | Lidget edits only the `statusLine` key in `~/.claude/settings.json`. |
| Session logs in `~/.claude/projects` | At each refresh, and when the flyout opens | Lidget calculates the cost estimates from these logs. |

### Usage endpoint requests

Anthropic limits the number of requests to the usage endpoint.
Lidget, Claude Code, and the CLI that T3 Code starts every 5 minutes send these requests with the same login.
A wait that the endpoint asks for can come from a request of any of them.
Lidget uses these rules to send as few requests as possible:

- A recent response is the last successful response to Lidget, or the response that Claude Code saved, whichever is newer.
- If the recent response is less than 15 minutes old, Lidget skips the request.
- A feed reading is current for 10 minutes.
  Feeds can have a current session reading and a current weekly reading.
  Then Lidget waits until the recent response is 1 hour old, because the endpoint can report more limits.
- After HTTP 429, Lidget sends no request until the `Retry-After` time. Without that header, Lidget waits 15 minutes.
  Lidget saves that time, so the wait continues after a restart of Lidget or Windows.
- If the endpoint fails, the response that Claude Code saved, or current feed readings, keep the provider status OK.
  Older readings stay visible, with the error message.

For each limit, the newest reading from any source wins.

### Claude login

When the Claude access token expires, Lidget refreshes it.
Claude Code refresh tokens are single-use, so Lidget writes the new tokens back to `~/.claude/.credentials.json`.
Lidget keeps all other fields of that file unchanged.

## Codex

| Source | When | Notes |
| --- | --- | --- |
| `codex app-server`, started from the Codex CLI | At each refresh, and when the app-server sends a rate limit notification | Lidget reads the rate limits of the signed-in ChatGPT account. The Codex CLI owns its login and token refresh. |
| Session logs in `~/.codex/sessions` and `~/.codex/archived_sessions` | At each refresh, and when the flyout opens | Lidget calculates the cost estimates from these logs. |

Lidget does not write to Codex files.
If the npm `codex.cmd` shim is on `PATH`, Lidget starts the native `codex.exe` from the npm package.
If the app-server stops, Lidget starts it again after 2 seconds, 10 seconds, 60 seconds, and then 5 minutes.

## Configuration folders

| Variable | Effect |
| --- | --- |
| `CLAUDE_CONFIG_DIR` | Changes the Claude Code folder, as it does for Claude Code. For session logs, the value can have more than one folder, separated by commas. |
| `CODEX_HOME` | Changes the Codex folder. The value can have more than one folder, separated by commas. |

## GitHub

The installed app checks the releases of `Stebulous/lidget` for updates 24 hours apart.
A Debug build, or a copy that Velopack did not install, does not check for updates.

## Files that Lidget writes

| Location | Content |
| --- | --- |
| `%LOCALAPPDATA%\LidgetData\settings.json` | Settings |
| `%LOCALAPPDATA%\LidgetData\history.db` | Limit history (SQLite) |
| `%LOCALAPPDATA%\LidgetData\cost-cache.db` | Parsed session log data for the cost estimates (SQLite) |
| `%LOCALAPPDATA%\LidgetData\notifications.json` | The thresholds that notified in each limit window |
| `%LOCALAPPDATA%\LidgetData\claude-usage-wait.json` | The end of a wait after the Claude usage endpoint returned HTTP 429. Lidget deletes it after the next successful response. |
| `%LOCALAPPDATA%\LidgetData\claude-statusline.json` and `statusline-original.json` | The statusline feed, and your original statusline setting |
| `%LOCALAPPDATA%\LidgetData\logs\` | Logs for the last 7 days. Logs do not contain tokens. |
| `~/.claude/settings.json` | Only the `statusLine` key, when you change the statusline feed setting. Lidget keeps a copy in `settings.json.lidget-backup`. |
| `~/.claude/.credentials.json` | New tokens after a token refresh |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `Lidget` | Start with Windows |

The `LIDGET_DATA_DIR` environment variable moves the data folder.
