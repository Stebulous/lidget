# Architecture

This page describes the projects of Lidget, their dependencies, and the flow of limit data.
[The MVP plan](plans/lidget-mvp.md#decisions) records the reason for each design decision.

## Projects

| Project | Contents | Can depend on |
| --- | --- | --- |
| `src/Lidget.Core` | Models, settings, the refresh scheduler, history, cost estimates, notification rules, and text formats. No WPF. | .NET and NuGet packages only |
| `src/Lidget.Providers` | The Claude and Codex providers, their feeds, and their session log scanners. No WPF. | `Lidget.Core` |
| `src/Lidget.App` | The WPF app: the taskbar host, the views, the view models, notifications, start-with-Windows, and updates. | `Lidget.Core`, `Lidget.Providers` |
| `src/Lidget.StatusLine` | `lidget-statusline.exe`, the NativeAOT wrapper for the Claude Code statusline command. | Two linked files from `Lidget.Core`: `AppInfo.cs` and `StatusLineFiles.cs`. Keep them free of other dependencies. |
| `tests/Lidget.Tests` | xUnit tests for `Lidget.Core`, `Lidget.Providers`, and the wrapper. | All projects except `Lidget.App` |

Keep logic out of `Lidget.App` when a test can cover it.
`Lidget.Core/AppInfo.cs` holds the name, the package ID, and the data folder. Do not change the package ID `Lidget`: installed copies update only from releases with the same ID.

## Limit data flow

1. `RefreshScheduler` calls `RefreshAsync` on each enabled provider at the refresh interval.
   A failure in one provider does not stop the others.
2. Each provider returns a `ProviderSnapshot`: a status, a list of `LimitWindow` values, and the update time.
3. Providers raise `SnapshotPushed` for readings that arrive between refreshes, for example from a feed or an app-server notification.
4. The scheduler publishes each snapshot to these consumers:
   - `HistoryRecorder`, which stores each new reading in `history.db`.
   - `ThresholdEvaluator`, which returns the notifications to show.
   - `UsageViewModel`, which updates the widget and the flyout.
5. `CostTracker` scans the session logs separately, at the refresh interval, and publishes one `CostSummary` for each provider.

A `LimitWindow` always stores the used share, from 0 to 100.
The view models and `UsageFormat` change it to the share that is left when **Show limits as** is Remaining.
Severity colors and notification thresholds always use the used share.

A `LimitWindow` also stores the window length in `Duration`, when the provider mapper knows it.
`UsagePace` uses the length and the reset time to compare the used share with even pace.
`UsageFormat.PaceText` gives the pace text. The view models calculate the pace again at each tick, because it changes with time.

After HTTP 429, the scheduler waits before the next refresh of that provider.
The wait doubles after each 429, up to 1 hour, and is never shorter than the `Retry-After` time.

## Claude provider

`ClaudeProvider` combines four sources. For each limit, the newest reading wins.

| Class | Source |
| --- | --- |
| `ClaudeUsageClient` and `ClaudeUsageMapper` | The usage endpoint |
| `ClaudeCodeUsageCache` | The usage response that Claude Code saved in `~/.claude.json` |
| `ClaudeT3CodeSource` | The T3 Code provider logs |
| `ClaudeStatusLineSource` | `claude-statusline.json`, which `lidget-statusline.exe` writes |

Both feeds implement `IClaudeLimitFeed`.
`ClaudeCodeUsageCache` is not a feed: it gives a full usage response, like the endpoint, and `ClaudeUsageMapper` maps both.
`ClaudeProvider` skips the endpoint while a recent response or current feed readings exist.
It also skips the endpoint while a `Retry-After` time is in the future.
[Data sources and files](data-sources.md#usage-endpoint-requests) gives the exact rules.

`ClaudeCredentialStore` reads `~/.claude/.credentials.json` and refreshes the access token.
It writes the new single-use refresh token back, so that Claude Code stays signed in.
It never logs a token or the file contents.

`ClaudeT3CodeSource` reads only the new bytes of each log file after the first scan, and at most 2 MB of a file in one scan.
It parses only the lines that contain a `rate_limit_event`.

## Codex provider

`CodexProvider` starts one `codex app-server` process and sends JSON-RPC requests over standard input and output.
`KillOnCloseJob` puts the process in a Windows job object, so that the process stops when Lidget stops.
`CodexLocator` finds `codex` on `PATH`. If `PATH` has the npm shim, it starts the native `codex.exe` from the npm package.

## Taskbar host

`TaskbarHost` puts the widget window into the Windows taskbar as a child window of `Shell_TrayWnd`.
It computes the position in physical pixels, left of the system tray.

- It reads the edges of the taskbar buttons with UI Automation, on a background thread.
  If the widget would cover app buttons, it shows `FallbackPillWindow` instead.
- It embeds the widget again after `TaskbarCreated`, a display change, a DPI change, or a settings change.
- Explorer can destroy the child window without a WPF `Closed` event. A 2-second timer checks the window handle and embeds again.

Do not copy code from FluentFlyout (GPL-3.0). Lidget uses the same technique with code written for Lidget.

## Storage

| Store | Class | Notes |
| --- | --- | --- |
| `settings.json` | `SettingsStore` | `LidgetSettings` defines each key and its default. Writes are atomic. |
| `history.db` | `HistoryStore` | One row for each reading, keyed by provider, limit, and observation time. `Migrations` holds the schema versions. |
| `cost-cache.db` | `CostCache` | Parsed log entries, so that a scan reads only new log data. |
| `notifications.json` | `ThresholdEvaluator` | The thresholds that notified in each limit window. |
| `claude-usage-wait.json` | `ClaudeProvider` | The end of a rate-limit wait, so that a restart does not send a request before the `Retry-After` time. |

`AtomicFile` writes a temporary file and then replaces the target, so that a crash does not leave a partial file.
