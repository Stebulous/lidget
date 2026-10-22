# Lidget

Lidget is a Windows 11 taskbar widget that shows the usage limits of AI subscription plans.
The first version tracks Claude (Pro/Max) and Codex.

## Status

Lidget is in development. No feature is complete yet.
[The MVP plan](docs/plans/lidget-mvp.md) describes the planned work and its order.

## Build and test

Prerequisites: Windows 11 and the .NET 10 SDK.

```powershell
dotnet build Lidget.sln
dotnet test Lidget.sln
```

## License

Lidget uses the [MIT license](LICENSE).
