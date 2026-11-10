# Data sources and files

This page lists each source that Lidget reads, each file that Lidget writes, and the refresh rules.
The provider code in `src/Lidget.Providers` controls this behavior.

Lidget reads the logins of Claude Code and the Codex CLI on this computer.
Lidget does not send data to a server other than the providers and GitHub.

## Claude

| Source | When | Notes |
| --- | --- | --- |
| `https://api.anthropic.com/api/oauth/usage?cedar_ember=1` | At a refresh, when no source has a recent response. See [Usage endpoint requests](#usage-endpoint-requests). | Uses the Claude Code login in `~/.claude/.credentials.json`. Anthropic does not document this endpoint, and can change it without notice. The `cedar_ember` parameter adds the banked resets to the response. |
| The usage response that Claude Code saved, in `~/.claude.json` | At each refresh | Each Claude Code process saves its last successful usage request as `cachedUsageUtilization`. This includes the CLI that T3 Code starts to check the limits. Lidget uses a copy that belongs to the signed-in account. The copy must be less than 1 hour old, unless the endpoint fails. The key is undocumented, and Lidget only reads the file. |
| T3 Code provider logs, `~/.t3/userdata/logs/provider/events.*.log` | When the logs change, if **Read limits from T3 Code** is on (default) | Lidget reads only the `rate_limit_event` entries of Claude sessions. It ignores all other log content. |
| The Claude Code statusline, through `lidget-statusline.exe` | After each Claude Code response in a terminal, if **Read limits from the Claude Code statusline** is on | Lidget edits only the `statusLine` key in `~/.claude/settings.json`. |
| Session logs in `~/.claude/projects` | At each refresh, and when the flyout opens | Lidget calculates the cost estimates from these logs. |

### Usage endpoint requests

Anthropic limits the number of requests to the usage endpoint.
Lidget, Claude Code, and the CLI that T3 Code starts every 5 minutes send these requests with the same login.
A wait that the endpoint asks for can come from a request of any of them.
Lidget uses these rules to send as few requests as possible:

- A recent response is the last successful response to Lidget, or the response that Claude Code saved, whichever is newer.
- If the recent response is less than 30 minutes old, Lidget skips the request.
- A feed reading is current for 10 minutes.
  Feeds can have a current session reading and a current weekly reading.
  Then Lidget waits until the recent response is 1 hour old, because the endpoint can report more limits.
- After HTTP 429, Lidget sends no request until the `Retry-After` time. Without that header, Lidget waits 15 minutes.
  Lidget saves that time, so the wait continues after a restart of Lidget or Windows.
- If the endpoint gives HTTP 429 or another temporary failure, Lidget keeps the newest response from Lidget or from Claude Code.
  It removes a limit from that response when the reset time of the limit passes.
- During this failure, a response that is less than 1 hour old, or current feed readings, keep the provider status OK.
  Older readings stay visible, with their real age and the error message.
- A refused login does not keep the response of Lidget, because the login can belong to another account.
- Only the endpoint gives the banked resets.
  If the last reset data is 6 hours old, Lidget sends a request, but not less than 30 minutes after its last request.
- After a redemption, Lidget sends a request at the next refresh, to read the new limits and resets.
  The `Retry-After` wait still applies.

For each limit, the newest reading from any source wins.

### Claude banked resets

Lidget reads the `cedar_ember` block of the usage response.
It shows a reset only if the block has `eligible: true` and the grant has resets left and a future expiry time.
Only the grant in `next_grant_id` can be used now.
The grant `label`, for example "Free limit reset", is the title in the flyout.

The endpoint gives the block only to the Claude Code user agent.
Thus, the usage request and the reset request send `User-Agent: claude-cli/<version> (external, cli)`, as T3 Code does.
Lidget reads the version from `lastReleaseNotesSeen` in `~/.claude.json`. Without it, Lidget uses `2.1.322`.

To redeem a reset, Lidget sends `POST https://api.anthropic.com/api/organizations/{organization}/reset_rate_limits`.
The body has the program `cedar_ember`, the grant ID, and a request ID.
Lidget reads the organization ID from `oauthAccount.organizationUuid` in `~/.claude.json`.
T3 Code uses the same request.

### Claude login

When the Claude access token expires, Lidget refreshes it.
Claude Code refresh tokens are single-use, so Lidget writes the new tokens back to `~/.claude/.credentials.json`.
Lidget keeps all other fields of that file unchanged.

## Codex

| Source | When | Notes |
| --- | --- | --- |
| `codex app-server`, started from the Codex CLI | At each refresh, and when the app-server sends a rate limit notification | Lidget reads the rate limits of the signed-in ChatGPT account. The Codex CLI owns its login and token refresh. Lidget shows the plan allowance, with the limit ID `codex`, and ignores the limits for one model. |
| Session logs in `~/.codex/sessions` and `~/.codex/archived_sessions` | At each refresh, and when the flyout opens | Lidget calculates the cost estimates from these logs. |

Lidget does not write to Codex files.
If the npm `codex.cmd` shim is on `PATH`, Lidget starts the native `codex.exe` from the npm package.
If the app-server stops, Lidget starts it again after 2 seconds, 10 seconds, 60 seconds, and then 5 minutes.

### Codex banked resets

Each `account/rateLimits/read` request gives the number of banked resets in `rateLimitResetCredits.availableCount`.
The list of resets, with titles and expiry times, costs the server a separate lookup.
Lidget sets `excludeResetCreditDetails` to `false` only in these conditions:

- At the first read, and when the count changes.
- After a redemption.
- If the details are 6 hours old.
- If the flyout opens and the details are 10 minutes old.

To redeem a reset, Lidget sends `account/rateLimitResetCredit/consume` with the reset ID and an idempotency key.

### Redemption safety

Lidget redeems a reset only after you confirm it in the flyout.
Before the request, Lidget saves an idempotency key in `reset-redemptions.json`.
If Lidget cannot save the key, it sends no request.
If the response is lost, the next try for the same reset uses the same key, so the provider spends one reset at most.
Lidget removes the key after a final result.

## Cost log scanning

Both cost scanners cache complete log lines and read appended lines on later scans.
They reuse read buffers and check cancellation between lines and read blocks.
An interrupted file scan leaves its cached entries and read position unchanged, so the next scan can retry it.
The Claude scanner accepts JSON whitespace around the `usage` field separator.

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
| `%LOCALAPPDATA%\LidgetData\history.db` | Limit history, the known banked resets, and the reset events (SQLite) |
| `%LOCALAPPDATA%\LidgetData\cost-cache.db` | Parsed session log data for the cost estimates (SQLite) |
| `%LOCALAPPDATA%\LidgetData\notifications.json` | The thresholds that notified in each limit window |
| `%LOCALAPPDATA%\LidgetData\claude-usage-wait.json` | The end of a wait after the Claude usage endpoint returned HTTP 429. Lidget deletes it after the next successful response. |
| `%LOCALAPPDATA%\LidgetData\claude-resets.json` | The last Claude banked resets and the time of that check. Other Claude sources do not have the resets. |
| `%LOCALAPPDATA%\LidgetData\reset-redemptions.json` | The idempotency key of a redemption that has no final result |
| `%LOCALAPPDATA%\LidgetData\reset-warnings.json` | The expiry warnings that each banked reset gave |
| `%LOCALAPPDATA%\LidgetData\claude-statusline.json` and `statusline-original.json` | The statusline feed, and your original statusline setting |
| `%LOCALAPPDATA%\LidgetData\logs\` | Logs for the last 7 days. Logs do not contain tokens. |
| `~/.claude/settings.json` | Only the `statusLine` key, when you change the statusline feed setting. Lidget keeps a copy in `settings.json.lidget-backup`. |
| `~/.claude/.credentials.json` | New tokens after a token refresh |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `Lidget` | Start with Windows |

The `LIDGET_DATA_DIR` environment variable moves the data folder.
