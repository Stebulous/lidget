# Documentation index

Use this index to find the guide for your task.
The guides describe the current `main` branch.
[The development plan](plans/lidget-plan.md) records the decisions, the task status, and the dated evidence.

## Use Lidget

| Guide | Use it to |
| --- | --- |
| [User guide](user-guide.md) | Install Lidget, read the widget and the flyout, change settings, and correct common problems |
| [Data sources and files](data-sources.md) | See what Lidget reads, what it writes, and when it sends requests |

## Change Lidget

| Guide | Use it to |
| --- | --- |
| [CONTRIBUTING.md](../CONTRIBUTING.md) | Propose a change, and prepare a pull request |
| [Development](development.md) | Build, test, run, and package Lidget |
| [Architecture](architecture.md) | Find the project and the class that owns a behavior |
| [QA checklist](qa-checklist.md) | Do the manual checks for a release candidate |
| [Release Lidget](releasing.md) | Tag, review, and publish a release |
| [Writing guide and glossary](writing-guide.md) | Write documentation, and use the project terms |
| [AGENTS.md](../AGENTS.md) | Give instructions to a coding agent |

## Documentation authority

Source code and tests define the behavior of Lidget.
`src/Lidget.Core/Models/LidgetSettings.cs` defines the settings and their defaults.
`src/Lidget.App/Views/SettingsWindow.xaml` defines the setting labels.
The guides explain that behavior. If a guide disagrees with source, the guide has a defect.

Each fact has one owner page. Other pages link to it:

| Fact | Owner |
| --- | --- |
| Settings, notifications, and troubleshooting | [User guide](user-guide.md) |
| Sources, requests, and written files | [Data sources and files](data-sources.md) |
| Commands and environment variables | [Development](development.md) |
| Release steps | [Release Lidget](releasing.md) |
| Decisions and evidence | [The development plan](plans/lidget-plan.md) |

[The writing guide](writing-guide.md#status-words) defines the status words.
