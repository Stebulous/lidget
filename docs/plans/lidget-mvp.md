# Lidget MVP — Development plan

> Status: approved · Planned: 2026-10-22 · Repository: new project at `E:\Documents\Projects\lidget`

"Lidget" is a working name.
The final name is open (see [Assumptions to confirm](#assumptions-to-confirm)).

## Summary

Lidget is a Windows 11 app that shows the usage limits of AI subscription plans in a small widget inside the taskbar.
The MVP tracks Claude (Pro/Max) and Codex (ChatGPT plans).
A click on the widget opens a Fluent flyout with limit details, local cost estimates, and a usage history chart.
Lidget is a public open-source project with the MIT license, distributed through GitHub Releases with automatic updates.

The design takes ideas from two projects:

- [FluentFlyout](https://github.com/unchihugo/FluentFlyout) (GPL-3.0): a WPF app that puts a widget window into the Windows 11 taskbar.
- [OpenUsage](https://github.com/robinebers/openusage) (MIT): a macOS menu bar app that tracks AI subscription usage.

## Goals and success criteria

- Goal: a user with Claude Code or Codex CLI logged in sees current limit usage in the taskbar with no extra login.
  - Success looks like: after the first start, the widget shows a logo, a bar, and a percentage for each provider within 10 seconds.
- Goal: the data stays current without user action.
  - Success looks like: values refresh every 5 minutes by default, and reset countdowns are correct to the minute.
- Goal: Lidget never breaks the login of the user's CLI tools.
  - Success looks like: Claude Code and Codex CLI continue to work after one week of Lidget operation with normal CLI use.
- Goal: the widget stays visible through normal shell events.
  - Success looks like: the widget comes back after an Explorer restart, a DPI change, and a theme change, as the manual QA checklist records.
- Goal: users get a warning before they reach a limit.
  - Success looks like: a toast notification appears when a limit crosses 80% and 95%, one time for each limit window.
- Goal: a user can install and update Lidget from GitHub Releases.
  - Success looks like: the Velopack installer installs the app, and a newer release updates it automatically.

## Non-goals

Do not build these items in the MVP:

- Providers other than Claude and Codex (for example Cursor, Copilot, Gemini, OpenRouter).
- Account switching or multiple accounts for one provider.
- A CLI or a local HTTP API for limit data.
- A general widget platform, widgets that are not about AI usage, or a third-party plugin system.
- A system tray icon.
- Widgets on secondary monitors, or support for a vertical taskbar.
- Windows 10 support.
- MSIX packaging or a Microsoft Store listing.
- Code signing.
- Localization. The MVP is English only.
- A separate in-app OAuth login. Lidget uses the existing CLI logins.
- Code copied from FluentFlyout. Its license is GPL-3.0, and Lidget is MIT.

## Decisions

| # | Area | Decision | Rationale | Decided by |
| --- | --- | --- | --- | --- |
| D1 | Intent | Public open-source app with the MIT license | Matches OpenUsage, permits contributions, and keeps a later Store option | user |
| D2 | Intent | Future work deepens AI-usage features (more providers, history, alerts). No widget platform and no plugin system. | Keeps the core focused on one domain | user |
| D3 | Shape | MVP providers: Claude (Pro/Max) and Codex | The two best-understood integrations | user |
| D4 | Shape | MVP includes threshold notifications, local cost estimates, and a usage history chart | User selection | user |
| D5 | Shape | No tray icon. Settings and Quit are in the widget context menu and the flyout. | User selection | user |
| D6 | Experience | Widget shows logo, thin bar, and percentage for each provider | Readable at a glance | user |
| D7 | Experience | Native Fluent style: Segoe UI Variable, Mica, system light or dark theme, system accent color | Matches the taskbar and system flyouts | user |
| D8 | Approach | C# on .NET 10 with WPF and WPF-UI | Proven taskbar embedding technique (FluentFlyout) and mature Fluent libraries | user |
| D9 | Approach | Claude data: OAuth usage endpoint is the primary source | No official on-demand source exists | user |
| D10 | Approach | Claude token refresh: guarded refresh with write-back to the CLI credentials file | Data stays current, and the CLI keeps a valid single-use refresh token | user |
| D11 | Approach | Claude data: an optional statusline feed, off by default | Official Claude Code data. It is a second source if the endpoint changes. | user |
| D12 | Approach | Codex data: `codex app-server` JSON-RPC (`account/rateLimits/read` and `account/rateLimits/updated`) | OpenAI maintains the protocol. Codex owns its login and token refresh. | user |
| D13 | Delivery | GitHub Releases with Velopack installer and automatic updates, unsigned at first | One-click install, silent updates, no certificate cost | user |
| D14 | Approach | Agent Client Protocol (ACP) is not a data source | ACP `usage_update` reports context size and session cost only, not subscription limits | user |
| D15 | Shape | Windows 11 22H2 and later, horizontal taskbar, primary monitor only | Limits shell-integration scope | agent default |
| D16 | Experience | Widget position: left of the system tray, with an offset setting | Same location as FluentFlyout. Does not cover task buttons. | agent default |
| D17 | Experience | If taskbar embedding fails, show a floating pill window and one toast notification | Without a tray icon, the app must stay visible | agent default |
| D18 | Experience | Left-click toggles the flyout. Hover shows reset times. Right-click opens Refresh, Settings, and Quit. | Conventional taskbar interaction | agent default |
| D19 | Experience | Widget percentage shows the tightest limit by default. Settings can select session or weekly. | The tightest limit is the one that stops work | agent default |
| D20 | Experience | Colors: green below 75%, amber from 75%, red from 90% | Common warning thresholds | agent default |
| D21 | Experience | Flyout: one card for each provider with session and weekly bars, reset countdowns, cost tiles, history chart, last-updated time, refresh button, and settings button | Shows all MVP data in one surface | agent default |
| D22 | Experience | Settings: enable each provider, refresh interval, notification thresholds, widget metric, start with Windows, history retention, statusline feed, widget offset | Covers each configurable decision | agent default |
| D23 | Approach | Refresh every 5 minutes by default (range 1 to 30). Refresh when the flyout opens and data is older than 60 seconds. Exponential backoff after HTTP 429. | Same default as OpenUsage. Limits load on the endpoints. | agent default |
| D24 | Approach | Projects: `Lidget.Core`, `Lidget.Providers`, `Lidget.App`, `Lidget.StatusLine`, `Lidget.Tests` | Keeps WPF out of logic that tests cover | agent default |
| D25 | Approach | Storage in `%LOCALAPPDATA%\Lidget\`: `settings.json`, `history.db` (SQLite), `logs\` | Standard per-user location for an unpackaged app | agent default |
| D26 | Approach | History: one row for each limit at each refresh, kept 90 days by default | Enough data for 24-hour and 7-day charts and later trends | agent default |
| D27 | Approach | Cost estimates: incremental parse of Claude Code and Codex session logs, priced from a bundled table, labeled "API-equivalent estimate" | Same method as OpenUsage and ccusage | agent default |
| D28 | Approach | Notifications at 80% and 95% by default, one time for each limit window, plus an optional reset notice | Useful warning without repeated noise | agent default |
| D29 | Approach | Start with Windows is on by default, through the `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` registry value | A taskbar widget is useful only when it runs | agent default |
| D30 | Delivery | Logs roll daily and are kept 7 days. Tokens are redacted from all logs. | Diagnostics without credential exposure | agent default |
| D31 | Delivery | Unit tests for logic, a manual QA checklist for shell integration | The shell integration cannot run in CI | agent default |
| D32 | Delivery | GitHub Actions: build and test on each push, Velopack pack and GitHub Releases upload on a `v*` tag | Repeatable releases | agent default |
| D33 | Approach | The app name, package ID, and data folder name come from one constant | Makes the planned rename a small change | agent default |
| D34 | Approach | Charts use LiveCharts2. Notifications use `Microsoft.Toolkit.Uwp.Notifications`. | MIT libraries that work in unpackaged WPF apps | agent default |
| D35 | Delivery | Publish self-contained for `win-x64` | No .NET runtime prerequisite for users | agent default |

## Constraints

- Platform: Windows 11 22H2 or later. Target framework `net10.0-windows10.0.22621.0`.
- Stack: C# with WPF, [WPF-UI](https://github.com/lepoco/wpfui), CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, LiveCharts2, Velopack.
- License: MIT. Do not copy code from FluentFlyout (GPL-3.0). You can use its technique, and you must write the code yourself.
- You can port logic from OpenUsage (MIT). Keep its copyright notice in a `THIRD-PARTY-NOTICES.md` file for each ported part.
- Lidget must not write to `~/.codex/auth.json`.
- Lidget writes `~/.claude/.credentials.json` only in the guarded refresh (T1.3).
- Lidget writes `~/.claude/settings.json` only when the user changes the statusline feed setting (T1.6).
- Lidget must never log tokens, the contents of a credentials file, or authorization headers.
- High DPI: the app manifest must declare per-monitor DPI awareness v2.
- Git: follow the gitmoji commit format, one coherent change for each commit. Do not push or create a GitHub remote until the user asks.

## Context

### Current repository state

The directory contains only this plan.
It is not a git repository yet.
The workstation has .NET SDK 9.0.319 and does not have .NET 10.
The GitHub CLI (`gh`) is logged in.

### Taskbar embedding technique (FluentFlyout)

`FluentFlyoutWPF/Windows/TaskbarWindow.xaml.cs` in FluentFlyout uses this technique.
Read it to learn the technique. Do not copy it.

1. Find the taskbar with `FindWindow("Shell_TrayWnd", null)`.
2. Change the WPF window style from `WS_POPUP` to `WS_CHILD` with `SetWindowLong(GWL_STYLE)`.
3. Make the window a child of the taskbar with `SetParent`.
4. Find the tray area with `FindWindowEx(taskbar, "TrayNotifyWnd")`. Use UI Automation to find XAML taskbar elements by `AutomationId`.
5. Compute the widget position from the tray rectangle and the taskbar rectangle, in physical pixels.

The taskbar window can be absent at startup, and Explorer can restart.
Register the `TaskbarCreated` message with `RegisterWindowMessage` and embed again when it arrives.

### Claude data sources

OpenUsage source files show the current Claude integration:

- `Sources/OpenUsage/Providers/Claude/ClaudeAuthStore.swift`: credentials paths, OAuth refresh URL, and client ID.
- `Sources/OpenUsage/Providers/Claude/ClaudeUsageClient.swift`: usage request, headers, and refresh request.
- `Sources/OpenUsage/Providers/Claude/ClaudeUsageMapper.swift`: response fields and their meaning.
- `Sources/OpenUsage/Providers/Claude/ClaudeLogUsageScanner.swift`: session log parsing for cost estimates.

Known facts at planning time. Check each one in T0.2:

- Claude Code on Windows stores OAuth credentials in `~/.claude/.credentials.json`. The `CLAUDE_CONFIG_DIR` environment variable changes the directory.
- The usage request is `GET https://api.anthropic.com/api/oauth/usage` with `Authorization: Bearer <access token>` and `anthropic-beta: oauth-2025-04-20`.
- The refresh URL in OpenUsage is `https://platform.claude.com/v1/oauth/token`.
- Refresh tokens are single-use. If Lidget refreshes and does not write the new token back, the CLI loses its login.
- Claude Code gives a statusline command a JSON object on stdin. For Pro and Max users, that object has `rate_limits.five_hour` and `rate_limits.seven_day`, each with `used_percentage` and `resets_at` (Unix seconds). Some sources write `reset_at`. The object has `rate_limits` only after the first response in a session.

### Codex data sources

- `codex app-server` is a JSON-RPC 2.0 server over stdio in the Codex CLI.
- `account/rateLimits/read` returns the current rate limits without a model request ([openai/codex PR #5302](https://github.com/openai/codex/pull/5302)).
- A later change added `rate_limits_by_name` beside `rate_limits`.
- The server sends `account/rateLimits/updated` notifications.
- The protocol schema is marked unstable. The CLI can write the schema for its installed version: see `codex app-server --help` for the `generate-json-schema` and `generate-ts` subcommands.
- Codex session logs are in `~/.codex/sessions/`. The `CODEX_HOME` environment variable changes the directory. `Sources/OpenUsage/Providers/Codex/CodexLogUsageScanner.swift` and `CodexUsagePricing.swift` in OpenUsage show the parsing and pricing.

### Agent Client Protocol

The Agent Client Protocol (ACP) `usage_update` session notification has `used` and `size` (context tokens) and an optional `cost`.
It has no subscription limit fields, so Lidget does not use ACP (D14).

## Approach

### Architecture

```text
lidget/
├─ Lidget.sln
├─ Directory.Build.props          nullable, warnings as errors, LangVersion latest
├─ src/
│  ├─ Lidget.Core/                no WPF references
│  │  ├─ AppInfo.cs               name, package ID, data folder (D33)
│  │  ├─ Models/                  ProviderSnapshot, LimitWindow, CostSummary, ProviderStatus
│  │  ├─ Providers/IUsageProvider.cs
│  │  ├─ Refresh/RefreshScheduler.cs
│  │  ├─ Storage/                 SettingsStore, HistoryStore (SQLite), AtomicFile
│  │  ├─ Costs/                   PricingTable, cost aggregation
│  │  └─ Notifications/           ThresholdEvaluator (logic only)
│  ├─ Lidget.Providers/
│  │  ├─ Claude/                  ClaudeCredentialStore, ClaudeUsageClient, ClaudeUsageMapper,
│  │  │                           ClaudeStatusLineSource, ClaudeLogScanner, ClaudeProvider
│  │  └─ Codex/                   CodexLocator, CodexAppServerClient, CodexRateLimitMapper,
│  │                              CodexLogScanner, CodexProvider
│  ├─ Lidget.App/                 WPF
│  │  ├─ Shell/                   TaskbarHost, Win32 interop, FallbackPillWindow
│  │  ├─ Views/                   WidgetView, FlyoutWindow, SettingsWindow
│  │  ├─ ViewModels/
│  │  └─ Services/                ToastService, StartupRegistration, UpdateService
│  └─ Lidget.StatusLine/          NativeAOT console app: lidget-statusline.exe
├─ tests/Lidget.Tests/            xUnit, fixtures/ with sanitized recorded responses
├─ assets/pricing.json            model prices for each million tokens
└─ docs/
   ├─ plans/lidget-mvp.md         this plan
   └─ qa-checklist.md             manual shell-integration checks
```

### Data model

```csharp
public enum LimitKind { Session, Weekly, Other }
public enum ProviderStatus { Ok, Stale, NeedsLogin, NotInstalled, RateLimited, Error }

public sealed record LimitWindow(
    string Id,              // stable, for example "claude.five_hour"
    LimitKind Kind,
    string Label,           // "Session (5h)", "Weekly"
    double UsedPercent,     // 0 to 100
    DateTimeOffset? ResetsAt,
    string Source,          // "endpoint", "statusline", "app-server"
    DateTimeOffset ObservedAt);

public sealed record CostSummary(decimal Today, decimal Last7Days, decimal Last30Days, string Currency);

public sealed record ProviderSnapshot(
    string ProviderId,      // "claude", "codex"
    ProviderStatus Status,
    IReadOnlyList<LimitWindow> Limits,
    CostSummary? Costs,
    string? StatusMessage,  // user-facing action, for example "Run `codex login`"
    DateTimeOffset UpdatedAt);

public interface IUsageProvider : IAsyncDisposable
{
    string Id { get; }
    string DisplayName { get; }
    Task<ProviderSnapshot> RefreshAsync(CancellationToken ct);
    event EventHandler<ProviderSnapshot>? SnapshotPushed; // app-server notifications, statusline feed
}
```

The tightest limit is the `LimitWindow` with the highest `UsedPercent` (D19).
Use `TimeProvider` for all time access so that tests can control the clock.

### Data flow

1. `RefreshScheduler` calls `RefreshAsync` on each enabled provider at the interval (D23).
2. Each provider returns a `ProviderSnapshot`. A failure in one provider does not stop the others.
3. Push sources raise `SnapshotPushed` between refreshes.
4. The scheduler publishes each snapshot to the view models, `HistoryStore`, and `ThresholdEvaluator`.
5. Cost scanners run after each refresh and add `CostSummary` to the snapshot.

### Claude guarded refresh (D10)

1. Read `.credentials.json`. Keep the file bytes, the last write time, and a hash.
2. If the access token expires more than 5 minutes from now, use it. Do not refresh.
3. Read the file again. If it changed, use the new token and go back to step 2.
4. Send the refresh request.
5. Read the file again. If it changed since step 3, discard the refresh result and use the file token.
6. Write the new tokens to the file, and keep all other fields unchanged. Replace the file atomically with `AtomicFile`.
7. If the refresh returns `refresh_token_reused` or an equivalent error, read the file again and retry one time.
8. If the refresh fails, set the status to `NeedsLogin` with the message "Open Claude Code to sign in again".

On HTTP 401 from the usage endpoint, run one forced refresh and retry the request one time.

### Claude statusline feed (D11)

- When the user enables the feed, Lidget does these steps:
  1. Copy `~/.claude/settings.json` to `settings.json.lidget-backup`.
  2. Save the current `statusLine` object to `%LOCALAPPDATA%\Lidget\statusline-original.json`.
  3. Set `statusLine` to `{ "type": "command", "command": "<path>\\lidget-statusline.exe" }`.
- Edit the file with `System.Text.Json.Nodes` so that all other keys stay unchanged.
- When the user disables the feed, Lidget restores the saved `statusLine` object, or removes the key if no original existed.
- `lidget-statusline.exe` does these steps:
  1. Read stdin.
  2. If `rate_limits` is present, write it with a timestamp to `%LOCALAPPDATA%\Lidget\claude-statusline.json` atomically.
  3. Run the original command with the same stdin, and copy its stdout to stdout.
  4. If no original command exists, write nothing.
- `lidget-statusline.exe` must finish in less than 100 ms when no original command exists. Errors in the feed must not change the output of the original command.
- `ClaudeStatusLineSource` watches `claude-statusline.json` with `FileSystemWatcher` and raises `SnapshotPushed`.
- `ClaudeProvider` merges both sources: for each limit, the newer `ObservedAt` wins.
- Project-level `statusLine` settings override the user setting. The Settings window states this limitation.

### Codex app-server client (D12)

- `CodexLocator` finds `codex` on `PATH`, including the npm `codex.cmd` shim. If it does not find `codex`, the status is `NotInstalled` with the message "Install Codex CLI".
- `CodexAppServerClient` starts one `codex app-server` process with redirected stdio.
- Assign the process to a Windows job object with kill-on-close, so that it stops when Lidget stops.
- Send `initialize`, then call `account/rateLimits/read` at each refresh. Apply `account/rateLimits/updated` notifications as they arrive.
- Use hand-written request and response types in `System.Text.Json`. Base them on the schema that `codex app-server` writes for the installed version.
- Ignore unknown fields. A missing required field gives `Error` status with a log entry, not a crash.
- If the process exits, restart it with backoff: 2 s, 10 s, 60 s, then 5 minutes.
- If the response reports no ChatGPT login, the status is `NeedsLogin` with the message "Run `codex login`".

### Taskbar host and fallback (D15 to D17)

`TaskbarHost` embeds the widget and recomputes its position on these events:

- startup
- `TaskbarCreated`
- `WM_DISPLAYCHANGE` and `WM_DPICHANGED`
- `WM_SETTINGCHANGE` (taskbar alignment and theme)
- a 2-second timer that checks the tray rectangle

If `Shell_TrayWnd` is absent for 30 seconds, or the taskbar is not at the bottom, `TaskbarHost` shows `FallbackPillWindow`.
The pill is a topmost, draggable, borderless window with the same widget content.
`TaskbarHost` shows one toast that explains the fallback.

## Work breakdown

### M0 — Bootstrap and spikes

Outcome: the solution builds, and three spikes prove the three unknowns: the Claude endpoint, the Codex app-server, and taskbar embedding.

- [x] **T0.1 Bootstrap the repository**
  - Objective: create a buildable solution with CI.
  - Touches: `.gitignore`, `.gitattributes`, `LICENSE` (MIT), `README.md` (stub with purpose and "work in progress"), `Lidget.sln`, `Directory.Build.props`, all projects in [Architecture](#architecture), `src/Lidget.Core/AppInfo.cs`, `.github/workflows/ci.yml`.
  - Depends on: none.
  - Steps:
    1. Install the .NET 10 SDK: `winget install Microsoft.DotNet.SDK.10`.
    2. Run `git init -b main`.
    3. Create the solution and projects.
    4. Commit this plan with the bootstrap.
  - Acceptance: `dotnet build -c Release` and `dotnet test` pass. `Lidget.App` starts and shows an empty window. CI YAML runs `dotnet build` and `dotnet test` on `windows-latest`.
  - Execution skill: lean-build.
  - Parallel-safe: no.

- [x] **T0.2 Spike: Claude usage endpoint**
  - Objective: prove that the endpoint returns session and weekly limits with the user's real login.
  - Touches: `tests/Lidget.Tests/fixtures/claude/` (sanitized responses), plan evidence log.
  - Depends on: T0.1.
  - Steps:
    1. Read the OpenUsage Claude files in [Claude data sources](#claude-data-sources).
    2. Write a temporary console command that reads the token and calls the usage endpoint. The command must not refresh tokens.
    3. Save the response with all identifiers replaced by synthetic values.
    4. Record the response fields, the refresh URL, the client ID source, and the credentials file shape in the evidence log.
  - Acceptance: a fixture shows the 5-hour and 7-day windows with utilization and reset times.
  - Execution skill: investigate-first.
  - Parallel-safe: yes, with T0.3 and T0.4.

- [x] **T0.3 Spike: Codex app-server**
  - Objective: prove that `account/rateLimits/read` returns the 5-hour and weekly windows.
  - Touches: `tests/Lidget.Tests/fixtures/codex/` (sanitized JSON-RPC transcript and generated schema), plan evidence log.
  - Depends on: T0.1.
  - Steps:
    1. Run `codex --version` and record the version.
    2. Generate the JSON schema for the installed version.
    3. Start `codex app-server`. Send `initialize` and `account/rateLimits/read`.
    4. Record the message framing, the handshake, and the response shape.
  - Acceptance: a transcript fixture shows both windows with used percent and reset time, and the evidence log records the Codex version.
  - Execution skill: investigate-first.
  - Parallel-safe: yes, with T0.2 and T0.4.

- [x] **T0.4 Spike: taskbar embedding**
  - Objective: prove a WPF child window in the taskbar, left of the tray.
  - Touches: `src/Lidget.App/Shell/` (spike code that T2.1 can keep).
  - Depends on: T0.1.
  - Acceptance:
    - A colored placeholder shows left of the tray at 100% and 150% scale.
    - The placeholder comes back after `taskkill /f /im explorer.exe` and `start explorer.exe`.
    - The placeholder does not cover task buttons with centered or left alignment.
  - Execution skill: investigate-first.
  - Parallel-safe: yes, with T0.2 and T0.3.

### M1 — Core and providers

Outcome: `Lidget.Core` and `Lidget.Providers` produce correct snapshots for both providers, and unit tests prove the logic.

- [x] **T1.1 Core model and settings**
  - Objective: add the data model, the provider interface, the settings store, and the atomic file writer.
  - Touches: `src/Lidget.Core/Models/`, `Providers/IUsageProvider.cs`, `Storage/SettingsStore.cs`, `Storage/AtomicFile.cs`, tests.
  - Depends on: T0.1.
  - Acceptance: tests prove these points:
    - Settings round-trip with the defaults for D22 and D23.
    - The tightest-limit selection is correct.
    - `AtomicFile` writes a temporary file in the same directory, replaces the target, and leaves no temporary file.
  - Execution skill: lean-build.
  - Parallel-safe: no.

- [x] **T1.2 Refresh scheduler**
  - Objective: refresh providers on the interval, on demand, and with backoff.
  - Touches: `src/Lidget.Core/Refresh/`, tests.
  - Depends on: T1.1.
  - Acceptance: tests with a fake `TimeProvider` prove the interval, the 60-second staleness rule, the 429 backoff, provider isolation, and push snapshots.
  - Execution skill: lean-build.
  - Parallel-safe: yes, with T1.3 to T1.6.

- [x] **T1.3 Claude credential store with guarded refresh**
  - Objective: implement [Claude guarded refresh](#claude-guarded-refresh-d10).
  - Touches: `src/Lidget.Providers/Claude/ClaudeCredentialStore.cs`, tests.
  - Depends on: T0.2, T1.1.
  - Acceptance: tests with temporary directories and a fake HTTP handler prove these points:
    - The store does not refresh a valid token.
    - The written file keeps unknown fields.
    - The write is atomic.
    - A file changed during refresh wins.
    - The store retries one time after `refresh_token_reused`.
    - `CLAUDE_CONFIG_DIR` changes the path.
    - No token text appears in log output.
  - Execution skill: lean-build.
  - Parallel-safe: yes, with T1.2, T1.5, and T1.6.

- [x] **T1.4 Claude usage client, mapper, and provider**
  - Objective: map the endpoint response to a `ProviderSnapshot`.
  - Touches: `src/Lidget.Providers/Claude/ClaudeUsageClient.cs`, `ClaudeUsageMapper.cs`, `ClaudeProvider.cs`, tests.
  - Depends on: T1.3.
  - Acceptance: fixture tests prove the session and weekly mapping, the 401 retry, the 429 `RateLimited` status, and `NeedsLogin` when no credentials file exists.
  - Execution skill: lean-build.
  - Parallel-safe: yes, with T1.5.

- [x] **T1.5 Codex app-server client and provider**
  - Objective: implement [Codex app-server client](#codex-app-server-client-d12).
  - Touches: `src/Lidget.Providers/Codex/`, tests.
  - Depends on: T0.3, T1.1.
  - Acceptance: tests with a fake stdio transport replay the T0.3 transcript and prove these points:
    - The mapper produces both windows.
    - Notifications push snapshots.
    - Unknown fields are ignored.
    - The backoff sequence is correct.
    - The status is `NotInstalled` or `NeedsLogin` in each case.
    - A manual run against the real CLI shows current values.
  - Execution skill: lean-build.
  - Parallel-safe: yes, with T1.3 and T1.4.

- [ ] **T1.6 Claude statusline feed**
  - Objective: implement [Claude statusline feed](#claude-statusline-feed-d11).
  - Touches: `src/Lidget.StatusLine/`, `src/Lidget.Providers/Claude/ClaudeStatusLineSource.cs`, `src/Lidget.Core/Storage/ClaudeSettingsEditor.cs`, tests.
  - Depends on: T1.1, T1.4.
  - Acceptance:
    - Tests prove that enabling and disabling the feed in `settings.json` keeps other keys, restores the original, and removes the key when no original existed.
    - Tests prove that the wrapper forwards stdin and stdout of an original command.
    - Tests prove that the provider merge prefers the newer reading.
    - A manual check in Claude Code shows the original statusline unchanged, and `claude-statusline.json` updates.
  - Execution skill: lean-build.
  - Parallel-safe: no.

### M2 — Taskbar widget, flyout, and settings

Outcome: a user can run Lidget, see the widget in the taskbar, open the flyout, and change settings.

- [ ] **T2.1 Taskbar host and fallback pill**
  - Objective: make the T0.4 spike a complete `TaskbarHost` with the fallback in [Taskbar host and fallback](#taskbar-host-and-fallback-d15-to-d17).
  - Touches: `src/Lidget.App/Shell/`.
  - Depends on: T0.4.
  - Acceptance: these shell items of `docs/qa-checklist.md` pass:
    - Explorer restart.
    - 100%, 150%, and 200% scale.
    - Alignment change and auto-hide taskbar.
    - The fallback pill when embedding is forced to fail.
  - Execution skill: lean-build.
  - Parallel-safe: yes, with T2.4.

- [ ] **T2.2 Widget view**
  - Objective: show logo, bar, and percentage for each enabled provider (D6, D19, D20).
  - Touches: `src/Lidget.App/Views/WidgetView.xaml`, `ViewModels/WidgetViewModel.cs`, provider logo assets.
  - Depends on: T1.2, T1.4, T1.5, T2.1.
  - Acceptance:
    - The widget shows live data for both providers.
    - Colors change at 75% and 90%.
    - Non-`Ok` statuses show a gray bar and a status glyph.
    - The tooltip shows reset countdowns.
    - The right-click menu runs Refresh, Settings, and Quit.
    - The widget text matches the clock in light and dark themes.
  - Execution skill: lean-build.
  - Parallel-safe: no.

- [ ] **T2.3 Flyout**
  - Objective: show provider detail cards above the widget (D7, D21).
  - Touches: `src/Lidget.App/Views/FlyoutWindow.xaml`, `ViewModels/FlyoutViewModel.cs`.
  - Depends on: T2.2.
  - Acceptance:
    - Left-click opens the flyout above the widget with Mica, and a second click or focus loss closes it.
    - The cards show session and weekly bars, countdowns, status messages, and last-updated time.
    - The refresh button refreshes the providers.
    - The cost tiles and the chart have layout slots, hidden until M3.
  - Execution skill: lean-build.
  - Parallel-safe: no.

- [ ] **T2.4 Settings window and startup registration**
  - Objective: give the user control of each setting in D22.
  - Touches: `src/Lidget.App/Views/SettingsWindow.xaml`, `ViewModels/SettingsViewModel.cs`, `Services/StartupRegistration.cs`.
  - Depends on: T1.1, T1.6.
  - Acceptance:
    - Each setting saves and applies without a restart.
    - Start with Windows adds and removes the `Run` value.
    - The statusline toggle calls the T1.6 editor and states the project-level limitation.
  - Execution skill: lean-build.
  - Parallel-safe: yes, with T2.1.

### M3 — History, costs, and notifications

Outcome: the flyout shows the history chart and cost tiles, and threshold notifications work.

- [ ] **T3.1 History store**
  - Objective: keep limit history in SQLite (D26).
  - Touches: `src/Lidget.Core/Storage/HistoryStore.cs`, tests.
  - Depends on: T1.2.
  - Acceptance: tests prove the schema creation, one row for each limit at each snapshot, and the retention purge.
    Tests also prove range queries for 24 hours and 7 days.
  - Execution skill: lean-build.
  - Parallel-safe: yes, with T3.3 and T3.4.

- [ ] **T3.2 History chart**
  - Objective: show usage over time in the flyout with LiveCharts2.
  - Touches: `src/Lidget.App/Views/` (chart control), `FlyoutViewModel.cs`.
  - Depends on: T2.3, T3.1.
  - Acceptance: the chart shows each limit for 24 hours and 7 days, with a toggle between the two ranges.
    The chart follows the theme and shows an empty state when no data exists.
  - Execution skill: lean-build.
  - Parallel-safe: no.

- [ ] **T3.3 Local cost estimates**
  - Objective: show API-equivalent cost for Today, 7 days, and 30 days (D27).
  - Touches: `src/Lidget.Providers/Claude/ClaudeLogScanner.cs`, `src/Lidget.Providers/Codex/CodexLogScanner.cs`, `src/Lidget.Core/Costs/`, `assets/pricing.json`, `THIRD-PARTY-NOTICES.md`, tests.
  - Depends on: T1.4, T1.5, T3.1.
  - Steps:
    1. Port the parsing and deduplication rules from the OpenUsage scanners.
    2. Keep the read position of each file in SQLite, so that each refresh reads only new lines.
    3. Price input, output, cache-write, and cache-read tokens for each model from `pricing.json`.
    4. Group by local calendar day.
    5. Count tokens of unknown models, and show "some models not priced" in the flyout.
  - Acceptance: fixture tests prove the totals, the deduplication, the incremental reads, and the day boundaries. The flyout tiles show the label "API-equivalent estimate".
  - Execution skill: lean-build.
  - Parallel-safe: yes, with T3.1 and T3.4 after its dependencies.

- [ ] **T3.4 Threshold notifications**
  - Objective: show toasts at the configured thresholds (D28) and for the embedding fallback (D17).
  - Touches: `src/Lidget.Core/Notifications/ThresholdEvaluator.cs`, `src/Lidget.App/Services/ToastService.cs`, tests.
  - Depends on: T1.2, T2.1.
  - Acceptance:
    - Tests prove one notification for each threshold in each limit window, with the key (provider, limit ID, `ResetsAt`).
    - Tests prove that no repeat occurs after a restart (state persisted) and that the reset notice obeys its setting.
    - A manual check shows a real toast.
  - Execution skill: lean-build.
  - Parallel-safe: yes, with T3.1 and T3.3.

### M4 — Release

Outcome: a tagged commit produces an installable, self-updating release.
The release stays local until the user approves publication.

- [ ] **T4.1 Velopack integration and release workflow**
  - Objective: install and update with Velopack from GitHub Releases (D13, D35).
  - Touches: `src/Lidget.App/App.xaml.cs` (`VelopackApp.Build().Run()` first in `Main`), `Services/UpdateService.cs`, `.github/workflows/release.yml`.
  - Depends on: M2.
  - Steps:
    1. Check for updates at startup and every 24 hours.
    2. Download updates in the background, and apply them at the next start.
    3. On a `v*` tag, the workflow runs `dotnet publish -r win-x64 --self-contained`, then `vpk pack`, then `vpk upload github`.
    4. Include `lidget-statusline.exe` in the package.
  - Acceptance:
    - A local `vpk pack` installs Lidget.
    - A second local package with a higher version updates the installed app from a local feed.
    - The workflow YAML passes `actionlint` or a dry run.
  - Execution skill: lean-build.
  - Parallel-safe: yes, with T4.2.

- [ ] **T4.2 Documentation**
  - Objective: write the minimum docs for users and contributors.
  - Touches: `README.md`, `docs/qa-checklist.md`, `THIRD-PARTY-NOTICES.md`.
  - Depends on: M3.
  - The README contains these parts:
    - What Lidget does, with a screenshot.
    - Install steps, including the SmartScreen warning for unsigned builds.
    - The data sources and the files that Lidget reads and writes.
    - A statement that the Claude endpoint is undocumented and can change.
    - Build and test commands.
  - Load the `repo-docs` skill before you write these docs.
  - Acceptance: the `repo-docs` audit script passes on the changed docs, and each command in the README runs.
  - Execution skill: lean-build.
  - Parallel-safe: yes, with T4.1.

- [ ] **T4.3 Release candidate QA**
  - Objective: prove the MVP success criteria on a clean install.
  - Touches: plan evidence log.
  - Depends on: T4.1, T4.2.
  - Acceptance:
    - Every item in `docs/qa-checklist.md` passes on an installed build, and the evidence log records the results with the revision.
    - Do not publish a release or push a tag until the user approves.
  - Execution skill: verify-and-stop.
  - Parallel-safe: no.

## Verification strategy

- **Unit tests** (`dotnet test`) cover these items with recorded fixtures and fake clocks:
  - mappers
  - the guarded refresh
  - the settings editor
  - the scheduler
  - threshold logic
  - history queries
  - log scanners
  - pricing
- No unit test calls a live endpoint or the real CLI.
- **Manual live checks** prove each provider against the real logins of the developer (T0.2, T0.3, T1.5, T1.6).
- **Manual shell QA** uses `docs/qa-checklist.md`:
  - Explorer restart
  - 100%, 150%, and 200% scale
  - light and dark themes, and an accent color change
  - left and centered alignment, and auto-hide
  - a full-screen app
  - sleep and resume
  - the forced fallback pill
  - the CLI login after one week of use
- **CI** builds and runs unit tests on each push.
- **Final gate:** T4.3 passes on an installed Velopack build, and each success criterion in [Goals and success criteria](#goals-and-success-criteria) has a recorded check.

## Risks and mitigations

| Risk | Likelihood / impact | Mitigation or early signal |
| --- | --- | --- |
| Anthropic changes or removes the OAuth usage endpoint | Medium / high | All endpoint code is in `ClaudeUsageClient` and `ClaudeUsageMapper`. The statusline feed gives a second source. The status shows `Error` with a clear message. |
| Guarded refresh races with a Claude Code refresh and logs the CLI out | Low / high | Read the file again before and after refresh, write atomically, retry one time. The one-week CLI login check in QA. |
| Windows update changes the taskbar structure and embedding fails | Medium / high | Fallback pill and toast (D17). All taskbar code is in `TaskbarHost`. |
| `codex app-server` protocol changes between Codex versions | Medium / medium | Tolerant parsing, recorded tested version, `Error` status with the Codex version in the log. |
| Codex CLI is not installed for a user who has a ChatGPT plan | Medium / low | `NotInstalled` status with an install instruction. A direct endpoint fallback is listed in [Later](#later). |
| Unsigned installer triggers SmartScreen and stops installs | High / medium | README explains the warning. Signing is listed in [Later](#later). |
| Session log formats change and cost estimates become wrong | Medium / low | Tolerant parsing, an "estimate" label, a count of unparsed lines in the log. |
| A rename after the first public release breaks automatic updates, because the Velopack package ID changes | Medium / high | Choose the final name before the first public release (D33). |

## Assumptions to confirm

- **Final app name.** The plan uses "Lidget". Confirm the final name before T4.1. A name change before T4.1 changes `AppInfo`, project names, and the repository name.
- **Claude endpoint.** The plan assumes the endpoint and refresh URL in [Claude data sources](#claude-data-sources). Confirm in T0.2, before T1.3.
- **Codex app-server.** The plan assumes `account/rateLimits/read` gives the 5-hour and weekly windows. Confirm in T0.3, before T1.5.
- **Toast library.** The plan assumes `Microsoft.Toolkit.Uwp.Notifications` works in an unpackaged .NET 10 WPF app with Velopack shortcuts. Confirm in T3.4. If it fails, use the Windows App SDK `AppNotificationManager`.

## Later

- Choose the final name and rename before the first public release.
- More providers: Cursor, GitHub Copilot, Gemini or Antigravity, OpenRouter, Z.ai.
- A direct Codex endpoint fallback for users without the Codex CLI.
- Account switching and multiple accounts.
- A CLI and a local HTTP API for limit data, like OpenUsage.
- Widgets on secondary monitors and support for a vertical taskbar.
- An optional tray icon.
- Code signing with Azure Trusted Signing.
- MSIX packaging, winget publication, and a Microsoft Store listing.
- `win-arm64` builds.
- Usage trends and forecasts from history ("at this rate, the weekly limit runs out on Thursday").

## Evidence log

Each entry has a date, a revision, the commands, the results, and the open gates.

### 2026-10-22 — T0.2 Claude usage endpoint spike (revision `f784c92`)

- Command: a temporary Node.js script outside the repository read `~/.claude/.credentials.json` and sent one `GET https://api.anthropic.com/api/oauth/usage`. The script did not refresh tokens.
- Request headers: `Authorization: Bearer <access token>`, `Accept: application/json`, `anthropic-beta: oauth-2025-04-20`, `User-Agent: claude-cli/2.1.308 (external, cli)`.
- Result: HTTP 200. Fixture: `tests/Lidget.Tests/fixtures/claude/usage-pro.json` (Pro plan). The response has no account identifiers.
- The response gives each limit in two forms:
  - Legacy keys `five_hour` and `seven_day`, each with `utilization` (0 to 100) and `resets_at` (ISO 8601).
  - A `limits` array. Each entry has `kind` (`session`, `weekly_all`, `weekly_scoped`), `group`, `percent`, `severity`, `resets_at`, `scope`, and `is_active`.
- OpenUsage reads the `limits` array for model-scoped weekly limits, because the legacy `seven_day_<model>` keys are now `null`. The mapper must prefer `limits` and use the legacy keys as a fallback.
- Credentials file shape: top-level keys `claudeAiOauth` and `mcpOAuth`. `claudeAiOauth` has `accessToken`, `refreshToken`, `expiresAt` (Unix milliseconds), `refreshTokenExpiresAt`, `scopes`, `subscriptionType`, `subscriptionTypeSource`, and `rateLimitTier`.
- Refresh, from OpenUsage `ClaudeAuthStore.swift` and `ClaudeUsageClient.swift`: `POST https://platform.claude.com/v1/oauth/token` with a JSON body. The body has `grant_type: refresh_token`, `refresh_token`, `client_id: 9d1c250a-e61b-44d9-88ed-5944d1962f5e`, and `scope: user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload`. The response has `access_token`, `refresh_token`, and `expires_in` (seconds). OpenUsage refreshes when the token expires within 5 minutes.
- Open gates: the refresh request is not tested live. T1.3 tests it with a fake HTTP handler, and the one-week QA check tests it live.

### 2026-10-22 — T0.3 Codex app-server spike (revision `f784c92`)

- Codex version: `codex-cli 0.163.0`, installed through npm (`%APPDATA%\npm\codex.cmd`).
- Command: `codex app-server generate-json-schema --out <dir>`. The relevant schema files are in `tests/Lidget.Tests/fixtures/codex/schema-0.163.0/`.
- Command: a temporary Node.js script started `codex.cmd app-server` and exchanged messages over stdio. The sanitized transcript is `tests/Lidget.Tests/fixtures/codex/app-server-transcript-0.163.0.json`.
- Framing: one JSON object on each line (JSONL). Messages have no `jsonrpc` field. Server notifications include `emittedAtMs`.
- Handshake: request `initialize` with `clientInfo` (`name`, `title`, `version`), then the notification `initialized`.
- `account/rateLimits/read` with `{ "excludeResetCreditDetails": true }` returned `rateLimits` with these fields:
  - `primary`: `usedPercent` 86, `windowDurationMins` 300, `resetsAt` in Unix seconds.
  - `secondary`: `usedPercent` 47, `windowDurationMins` 10080.
  - `planType` `plus`, plus `rateLimitsByLimitId` keyed by `codex`.
- `account/read` with `{ "refreshToken": false }` returned the auth mode and the plan. Use it to detect a missing login.
- `account/rateLimits/updated` is a sparse update. Merge its values into the last full read.
- The server also sends unrelated notifications, for example `remoteControl/status/changed` and `account/updated`. The client must ignore unknown methods.
- Map windows by `windowDurationMins`: 300 is the session window, and 10080 is the weekly window.
- Open gates: none for M1.

## Handoff brief

Build the Lidget MVP in `E:\Documents\Projects\lidget`, a new and empty project directory.
Follow the plan at `docs/plans/lidget-mvp.md`, and start at M0 task T0.1.
Lidget is an MIT Windows 11 taskbar widget in C# on .NET 10 with WPF and WPF-UI.
It shows Claude limits from the OAuth usage endpoint, with guarded refresh and an optional statusline feed.
It shows Codex limits from `codex app-server`.

Do not copy FluentFlyout code (GPL-3.0). You can port OpenUsage logic (MIT) with attribution.
Do not write `~/.codex/auth.json`, and never log tokens.
Run the three M0 spikes before M1, and record their results in the plan evidence log.

Each task must pass its acceptance checks and `dotnet test` before you commit it.
Use one gitmoji commit for each task on `main`.
Do not create a GitHub remote, push, tag, or publish a release until the user asks.

### 2026-10-22 — T0.4 taskbar embedding spike (revision after `85700c2`)

- Environment: Windows 11 build 26200, 2560 × 1440 display at 125% scale, centered taskbar alignment. FluentFlyout was running with its own taskbar widget.
- Taskbar children found: `TrayNotifyWnd`, `ReBarWindow32`, `Windows.UI.Composition.DesktopWindowContentBridge`, and FluentFlyout's `HwndWrapper`. FluentFlyout's window covers the full taskbar width.
- Code: `src/Lidget.App/Shell/TaskbarHost.cs` changes the window style from `WS_POPUP` to `WS_CHILD`, calls `SetParent` into `Shell_TrayWnd`, and positions the window left of `TrayNotifyWnd` with `SetWindowPos(HWND_TOP)`.
- Result: a transparent WPF window with `AllowsTransparency` showed the placeholder left of the tray chevron. The placeholder did not cover task buttons.
- Explorer restart: `Stop-Process -Name explorer -Force`. Explorer restarted automatically. Lidget embedded a new window in less than 10 seconds, and its position followed the changed tray width.
- Finding: Explorer destroys the child window with the taskbar. A hidden top-level `HwndSource` receives `TaskbarCreated`, and a 2-second timer embeds again.
- Open gates for T2.1: 150% and 200% scale, left alignment, auto-hide, mouse input and context menus in the child window, and the fallback pill.

### 2026-10-22 — T1.4 and T1.5 live provider check

- Command: a temporary console program outside the repository called `ClaudeProvider.RefreshAsync` and `CodexProvider.RefreshAsync` (from `CodexProvider.CreateDefault`) one time each, then refreshed Codex again and disposed it.
- Claude result: `Ok`, plan `Pro`, session 64%, weekly 54%. The token was valid, so no refresh request was sent.
- Codex result: `Ok`, plan `Plus`, session 86%, weekly 47%. The second refresh reused the running app-server.
- Process check after dispose: no `codex app-server` process from the npm installation remained.
- The usage endpoint accepted the `Lidget/0.1.0` User-Agent. Lidget does not send a Claude Code User-Agent.
