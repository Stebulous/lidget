# Agent instructions

Use these instructions for changes to Lidget, a Windows 11 taskbar widget for AI subscription limits.
Source code and tests take precedence over a conflicting documentation claim.
Read [the documentation index](docs/README.md) before broad repository work.

## Ownership

| Project | Owns | Rule |
| --- | --- | --- |
| `src/Lidget.Core` | Models, settings, refresh, history, costs, notification rules, formats | No WPF and no provider code |
| `src/Lidget.Providers` | Claude and Codex sources, feeds, and log scanners | Depends on `Lidget.Core` only |
| `src/Lidget.App` | WPF shell, views, view models, Windows services | No business rules that tests can cover elsewhere |
| `src/Lidget.StatusLine` | `lidget-statusline.exe` | Links `AppInfo.cs` and `StatusLineFiles.cs`. Keep both free of other dependencies. |

[Architecture](docs/architecture.md) describes the data flow.

## Rules that must not change without the maintainer

- Do not copy code from FluentFlyout (GPL-3.0). Lidget uses the MIT license.
  OpenUsage (MIT) ports need an entry in `THIRD-PARTY-NOTICES.md`.
- Never log tokens, login file contents, or authorization headers. Never print them in tool output.
- Do not write to `~/.codex/auth.json` or other Codex files.
- Write `~/.claude/.credentials.json` only in the guarded token refresh of `ClaudeCredentialStore`.
- Write `~/.claude/settings.json` only through `ClaudeStatusLineInstaller`, when the user changes the setting.
- Keep the Velopack package ID `Lidget` in `AppInfo`. Installed copies update only from releases with that ID.
- Keep user data in `%LOCALAPPDATA%\LidgetData`. Velopack deletes the install folder at install and uninstall.
- Respect the `Retry-After` time of the Claude usage endpoint. Do not add requests to that endpoint.

## Verification

Run these commands from the repository root:

```powershell
dotnet build Lidget.sln
dotnet test Lidget.sln
```

For a change to the widget, the flyout, the settings window, or the taskbar host, also run the Debug build and look at the result.
[Development](docs/development.md#run-the-debug-build) gives the steps.
Use `LIDGET_DATA_DIR` with a copy of the data folder, so that the user's history and settings stay unchanged.

Only one copy of Lidget can run. Quitting the user's installed copy to test is a change to their system:
start it again from the Start menu shortcut when you finish.
Stop each process that you start, including a `codex app-server` that a Debug build started.
Restore each Windows setting that a check changes, such as the display scale or the taskbar alignment.

Do not push a tag, publish a release, or edit a GitHub release without explicit permission.
[Release Lidget](docs/releasing.md) is the runbook.

## Documentation

Use [the writing guide](docs/writing-guide.md) for the writing standard and the glossary.
Update the guide that owns a behavior in the same change as the behavior.
Keep current behavior in the guides, and decisions and dated evidence in [the development plan](docs/plans/lidget-plan.md).

## Completion

Use gitmoji commits with one coherent change each: `<emoji> (<scope>): <subject>`.
Do not rewrite published history.
Report the branch, the commits, whether they are pushed, the verification, and the open gates.
