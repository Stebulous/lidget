# Contributing to Lidget

Thank you for your help with Lidget.
This page describes how to report a problem, propose a change, and prepare a pull request.

## Report a problem

Open an issue on GitHub with this information:

- The Lidget version, from the bottom of the Settings window.
- The Windows version, the display scale, and the taskbar alignment.
- What you did, what you expected, and what occurred.
- The relevant lines from `%LOCALAPPDATA%\LidgetData\logs`.

Logs do not contain tokens. Remove personal paths and account details before you attach them.
Never attach `~/.claude/.credentials.json`, `~/.codex/auth.json`, or other login files.

## Propose a change

For a small correction, open a pull request.
For a new provider, a new data source, or a change to the taskbar integration, open an issue first.
Lidget tracks AI subscription limits only. It is not a general widget platform, and it has no plugin system.

## Set up

Follow [Development](docs/development.md) to build, test, and run the Debug build.
[Architecture](docs/architecture.md) shows which project owns each behavior.

## Rules for code

- Do not copy code from [FluentFlyout](https://github.com/unchihugo/FluentFlyout). Its license is GPL-3.0, and Lidget uses MIT.
  You can use the same technique, with code that you write.
- You can port code from [OpenUsage](https://github.com/robinebers/openusage), which uses MIT.
  Add the port to `THIRD-PARTY-NOTICES.md`, with the copyright notice.
- Lidget must never log a token, the contents of a login file, or an authorization header.
- Lidget must not write to Codex files, such as `~/.codex/auth.json`.
- Lidget writes `~/.claude/.credentials.json` only after a token refresh, and `~/.claude/settings.json` only when the user changes the statusline feed setting.
- Keep logic in `Lidget.Core` or `Lidget.Providers`, and add tests for it. `Lidget.App` has no tests.
- Use `TimeProvider` for all time access, so that tests can control the clock.
- The build treats warnings as errors.

## Rules for documentation

- Update the guide that owns a behavior in the same pull request as the behavior. [The documentation index](docs/README.md#documentation-authority) names the owners.
- Write to [the writing guide](docs/writing-guide.md). Use its glossary terms.
- Put decisions and dated test results in [the MVP plan](docs/plans/lidget-mvp.md), not in the guides.

## Commits

Use the [gitmoji](https://gitmoji.dev/specification) format: `<emoji> (<scope>): <subject>`.
Write the subject in the imperative mood. Keep each commit to one coherent change.

Examples from this repository:

```text
✨ (claude): Read Claude limits from T3 Code and request the usage endpoint less
🐛 (shell): Re-embed after Explorer restarts and avoid covering taskbar buttons
📝 (plan): Record the T4.3 release candidate QA
```

## Pull requests

Before you open a pull request, do these checks:

1. `dotnet build Lidget.sln` and `dotnet test Lidget.sln` pass.
2. For a change to the taskbar, the flyout, or the settings window, you checked the result in the Debug build.
   Add a screenshot to the pull request.
3. If the change affects the shell integration, you did the relevant rows of [the QA checklist](docs/qa-checklist.md).
4. The documentation describes the new behavior.

CI runs the build and the tests on each pull request.
