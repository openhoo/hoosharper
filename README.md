# HooSharper

HooSharper is an open-source collection of Roslyn analyzers and code fixes aimed at replacing the everyday C# inspections and quick-fixes commonly provided by ReSharper.

## Development

Requirements: .NET SDK 10.0.110 or a compatible 10.0 patch.

```bash
dotnet build
dotnet test
```

Analyzer rules use the `HOO` diagnostic prefix. The first production rule should replace the bootstrap-only analyzer currently included in the project.

## Project layout

- `src/HooSharper.Analyzers` — analyzers and code-fix providers, packaged as a NuGet analyzer package.
- `tests/HooSharper.Analyzers.Tests` — Roslyn analyzer and code-fix tests.
