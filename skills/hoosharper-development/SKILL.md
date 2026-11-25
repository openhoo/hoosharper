---
name: hoosharper-development
description: Develop and verify HooSharper Roslyn analyzers and code fixes, including diagnostic compatibility, semantic safety, Fix All, tests, and NuGet packaging. Use in a HooSharper source checkout.
---

# HooSharper development

Run commands at the source root. Read `CONTRIBUTING.md`; SDK and tool versions
come from `global.json`, `package.json`, and CI. The analyzer targets
`netstandard2.0` with Roslyn 4.8 dependencies; tests/benchmarks may use newer
Roslyn packages without raising the shipped host requirement. The development SDK is .NET 10.0.109 with latest-patch rollforward.

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
dotnet run --project benchmarks/HooSharper.Performance/HooSharper.Performance.csproj \
  -c Release --no-build -- --validate-fixtures
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
- For query rewrites, inspect the bound selector's conversion. A query method's
  return type does not tell you whether its caller supplied an expression tree.
- Account for `CallerArgumentExpression`: both rewriting its argument and
  formatting an enclosing statement can change a string observed at runtime.
- Preserve comments, directives, evaluation order, declaration scope, and
  disposal lifetime. Include malformed/incomplete editor code and trivia cases.
- Collection rewrites require standard framework types and callback-stable
  receivers/keys/values. Do not assume a comparer has no side effects.
- Fluent-chain edits preserve tabs, visual widths, CRLF, and existing multiline
  layout. HOO1020's specific line-length key overrides `max_line_length`.
- Use `CodeFixTrivia` when formatting a changed subtree. Exercise
  `indent_size = tab` with a nondefault tab width and check the indentation of
  untouched neighboring statements. Return text changes relative to the
  original document so Fix All can combine separate edits in one file. Check
  new line breaks against both explicit settings and a CRLF file without one.

## Package verification

```bash
dotnet pack src/HooSharper.Analyzers/HooSharper.Analyzers.csproj \
  -c Release --no-build -o artifacts
python3 scripts/verify-package.py artifacts/HooSharper.Analyzers.*.nupkg
python3 scripts/verify-consumer.py artifacts/HooSharper.Analyzers.*.nupkg
```

Build both projects before packing with `--no-build`. Inspect the `.nupkg` for
both assemblies under `analyzers/dotnet/cs`; a compiler-only analyzer package
does not prove IDE code-fix availability. For packaging changes install it into
a disposable SDK-style consumer. `verify-consumer.py` builds an intentional
HOO1006 violation, applies the packed fix with `dotnet format analyzers`, and
checks that the corrected project builds cleanly. Keep local packages/build
output out of Git.

Report actual tests, coverage/package evidence where applicable, and any
unavailable gates. Documentation/skill changes need link and installation checks;
they do not require new semantic tests.

## Project smoke and native comparison

After building the Release solution, run `python3 scripts/verify-matrix.py --no-build`
and `python3 -m unittest discover -s scripts -p 'test_*.py' -v`. The matrix must
exercise all 20 IDs, compile each project Fix All, converge cumulatively, preserve
nullable warning counts and comments, and produce the same executable output.

For extended qualification use `scripts/verify-resharper-smoke.py` with the pinned
ReSharper CLI, SDK, and an owned work directory; see README. Preserve raw reports,
source/configuration/reference hashes, and analyzer/fixer assembly hashes. Review
unmatched native opportunities before classifying them as defects. This command
adapts only cloned SDK policy and comparison settings, and refuses modified C#
sources. Run `scripts/verify-native-after.py` against the same evidence to
inspect the actual rewritten source; the original comparison cannot reveal
warnings introduced by otherwise valid fixes. Compare native warnings,
project-supplied analyzer warnings, and compiler diagnostics separately.
Native inspection results do not qualify interactive IDE quick-fix UI.
For behavior changes, `scripts/verify-upstream-tests.py` also rebuilds pinned
libraries. It compares original and rewritten outcomes for Serilog,
FluentValidation and Polly.Core, plus Dapper's SQLite, DuckDB and wrapped-reader tests.
It repeats Fix All on each original checkout and
compares the final source hashes: zero diagnostics can hide different rewrites.
It requires a fresh disposable directory.

For Newtonsoft.Json, pass the paired native report and fixed-source directory
to `scripts/verify-newtonsoft-tests.py`. It compares all named results and any
baseline failure messages, and verifies the loaded assembly inside the test host.
When a fix depends on compiler behavior, exercise the installed package on
both the minimum SDK and the development SDK. Test CRLF files both with and
without a configured line ending. The hosts' Roslyn operation models
and the source text supplied for caller argument expressions can differ.
