# Third-party notices

Lidget contains code, data, and graphics from the projects in this file.
Lidget also uses NuGet packages. Each package includes its own license.

## OpenUsage

- Source: <https://github.com/robinebers/openusage>
- License: MIT
- Use in Lidget:
  - The session log parsing, deduplication, and pricing rules in `ClaudeLogScanner.cs`, `CodexLogScanner.cs`, and `src/Lidget.Core/Costs/` are ports of the OpenUsage scanners.
  - The provider marks in `src/Lidget.App/Resources/ProviderIcons.xaml` use path data from OpenUsage.
  - `assets/pricing.json` contains the OpenUsage pricing supplement.

```text
MIT License

Copyright (c) 2026 Robin Ebers

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## LiteLLM

- Source: <https://github.com/BerriAI/litellm>
- License: MIT
- Use in Lidget: the model prices in `assets/pricing.json` come from the LiteLLM model price list, through the OpenUsage snapshot.

```text
MIT License

Copyright (c) 2023 Berri AI

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## NuGet packages

| Package | License |
| --- | --- |
| [WPF-UI](https://github.com/lepoco/wpfui) | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT |
| [LiveChartsCore.SkiaSharpView.WPF](https://github.com/beto-rodriguez/LiveCharts2) | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp) (through LiveCharts2) | MIT |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | MIT |
| [Microsoft.Toolkit.Uwp.Notifications](https://github.com/CommunityToolkit/WindowsCommunityToolkit) | MIT |
| [Microsoft.Extensions.Logging](https://github.com/dotnet/runtime) | MIT |
| [Serilog.Extensions.Logging](https://github.com/serilog/serilog-extensions-logging) and [Serilog.Sinks.File](https://github.com/serilog/serilog-sinks-file) | Apache-2.0 |
