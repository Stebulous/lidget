# Writing guide and glossary

This guide adapts ASD-STE100 Simplified Technical English to the Lidget documentation.
See [the official description](https://www.asd-ste100.org/about.html).
The project uses an adaptation, without a formal dictionary compliance claim.

## Scope

Use these rules for the README, `AGENTS.md`, `CONTRIBUTING.md`, the guides in `docs/`, and new entries in the plan.

Do not change the words of this text:

- Code, commands, settings keys, file paths, log messages, and other identifiers.
- Text that Lidget shows, such as setting labels and status messages. Copy it exactly, in bold for labels.
- `LICENSE` and the license text in `THIRD-PARTY-NOTICES.md`.
- Dated evidence entries in [the development plan](plans/lidget-plan.md#evidence-log).

## Sentence rules

- Use no more than 20 words in a procedure step.
- Use no more than 25 words in a descriptive sentence.
- Put each sentence on its own line in the Markdown source.
- Use active voice and the simple present tense for current behavior.
- Use `must` for a requirement and `can` for a capability. Do not use `should` or `may` for a requirement.
- Put a condition before its action: "If X, do Y."
- Do not use semicolons in prose.
- Use a table to compare settings, files, commands, or symptoms.

## Preferred words

| Use | Do not use |
| --- | --- |
| start | launch, initiate |
| stop | terminate, kill, unless a command uses the word |
| use | utilize, leverage |
| shows | displays, renders, for text that a user reads |
| limit | quota, cap |
| share that is left | remaining capacity, headroom |
| sign in | log in, login as a verb |

## Project glossary

| Term | Meaning |
| --- | --- |
| Widget | The Lidget element in the Windows taskbar. It shows a logo, a bar, and a percentage for each provider. |
| Flyout | The window that opens above the widget when you click it. |
| Pill | The small floating window that shows the widget when Lidget cannot put it in the taskbar. |
| Provider | A subscription service that Lidget tracks: Claude or Codex. |
| Limit | A usage limit of a provider plan, for example the 5-hour session limit. |
| Limit window | One period of a limit, from its start to its reset time. |
| Session limit | The limit that resets 5 hours after the first request of a window. |
| Weekly limit | The limit that resets 7 days after the start of its window. |
| Tightest limit | The limit with the highest used share. It is the limit that stops work first. |
| Used share | The percentage of a limit that is used, from 0% to 100%. |
| Share that is left | 100% minus the used share. Lidget shows this value by default. |
| Even pace | A use of a limit at one rate for the full limit window, so that the limit runs out at the reset time. |
| Pace | The used share minus the even-pace share at this time: in reserve, on pace, or in deficit. |
| Usage endpoint | `https://api.anthropic.com/api/oauth/usage`, the Claude limit source that Lidget requests. |
| Saved response | The last usage response that Claude Code saved in `~/.claude.json`. Lidget reads it as a source, and it delays the next endpoint request. |
| Feed | A local source of Claude readings that arrive while you work: the statusline feed or the T3 Code feed. |
| Statusline feed | The optional wrapper around the Claude Code statusline command. It records the limits that Claude Code sends to the command. |
| T3 Code feed | The Claude limits that Lidget reads from the T3 Code provider logs. |
| App-server | `codex app-server`, the JSON-RPC process of the Codex CLI that reports Codex limits. |
| Cost estimate | The API price of the tokens in the local session logs. It is not a charge. |
| Data folder | `%LOCALAPPDATA%\LidgetData`. It keeps settings, history, caches, and logs. |
| Install folder | `%LOCALAPPDATA%\Lidget`. Velopack owns it and deletes it at uninstall. |
| Snapshot | The result of one provider refresh: the status, the limits, and the time. |

## Status words

| Status | Meaning |
| --- | --- |
| Implemented | Source and tests provide the behavior. |
| Released | A published GitHub release contains the behavior. |
| Planned | The plan includes the work, and no source provides it. |
| Pending | An acceptance gate remains open. |

## Review a documentation change

1. Compare each behavior claim with its source and the relevant tests.
2. Compare each command with `scripts/pack.ps1` and the workflows in `.github/workflows`.
3. Compare each setting label with `src/Lidget.App/Views/SettingsWindow.xaml`.
4. Run the documentation audit, if you have it, and `git diff --check`.
5. Read the change again for meaning, step order, and glossary terms.
