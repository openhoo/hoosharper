---
name: hoosharper-development
description: Develop and verify HooSharper Roslyn analyzers and code fixes, including diagnostic compatibility, semantic safety, Fix All, tests, and NuGet packaging. Use in a HooSharper source checkout.
---

# HooSharper development

Run commands at the source root. Read `CONTRIBUTING.md`; SDK and tool versions
come from `global.json`, `package.json`, and CI. The analyzer targets
`netstandard2.0`; the development SDK is .NET 10.0.109 with latest-patch rollforward.

## Find the implementation

| Task | Source |
| --- | --- |
| Diagnose a rule | `src/HooSharper.Analyzers/<Rule>Analyzer.cs` |
| Change its rewrite | `src/HooSharper.CodeFixes/<Rule>CodeFixProvider.cs` |
| Prove diagnostic and fix behavior | `tests/HooSharper.Analyzers.Tests/<Rule>AnalyzerTests.cs` |
| Test harness and language/reference defaults | `tests/HooSharper.Analyzers.Tests/AnalyzerVerifier.cs` |
| Diagnostic lifecycle | `src/HooSharper.Analyzers/AnalyzerReleases.Shipped.md`, `AnalyzerReleases.Unshipped.md` |
| Package layout | `src/HooSharper.Analyzers/HooSharper.Analyzers.csproj` |
| Performance | `benchmarks/HooSharper.Performance/` |

Each rule has an analyzer, fixer, tests, and release metadata. Preserve shipped
HOO diagnostic IDs, default severities, and meanings. Update README rule guidance
when changing public behavior; do not move unshipped entries or bump versions
outside release work.

## Development loop

```bash
dotnet restore HooSharper.slnx
dotnet build HooSharper.slnx -c Release --no-restore
dotnet test HooSharper.slnx -c Release --no-build
bun install --frozen-lockfile
bun run check-readme-version
```

For a focused rule, use the test project's `--filter` with its actual test-class
name. A fix needs the expected diagnostic location and final source, not merely
a successful compilation. Use the verifier's batch-fixed source to exercise
Fix All and convergence where relevant. CI enforces at least 91% line coverage;
use its coverage command when adding analyzer/fixer paths.

## Semantic safety

- Verify symbols/types and framework API availability before reporting a
  transformation; syntactically similar custom types are not interchangeable.
- Test no-diagnostic cases for overloaded equality, nullable booleans,
  expression trees, unsupported C# versions, and side-effectful expressions.
- Preserve comments, directives, evaluation order, declaration scope, and
  disposal lifetime. Include malformed/incomplete editor code and trivia cases.
- Collection rewrites require standard framework types and callback-stable
  receivers/keys/values. Do not assume a comparer has no side effects.
- Fluent-chain edits preserve tabs, visual widths, CRLF, and existing multiline
  layout. HOO1020's specific line-length key overrides `max_line_length`.

## Package verification

```bash
dotnet pack src/HooSharper.Analyzers/HooSharper.Analyzers.csproj \
  -c Release --no-build -o artifacts
```

Build both projects before packing with `--no-build`. Inspect the `.nupkg` for
both assemblies under `analyzers/dotnet/cs`; a compiler-only analyzer package
does not prove IDE code-fix availability. For packaging changes install it into
a disposable SDK-style consumer and verify a real HOO diagnostic plus a clean
negative example. Keep local packages/build output out of Git.

Report actual tests, coverage/package evidence where applicable, and any
unavailable gates. Documentation/skill changes need link and installation checks;
they do not require new semantic tests.
