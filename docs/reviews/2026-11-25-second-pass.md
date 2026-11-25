# Second real-project review — November 25, 2026

This pass follows the [first four-library review](2026-11-25-review.md) of
HooSharper 0.3.13. It adds Polly.Core 8.5.2 at
`a953ba8119074d7f63cc69643d4ef1d1de52ee3f`. Both reviews compare native
InspectCode diagnostics against HooSharper and execute HooSharper’s rewrites.
They do not compare edits made by interactive JetBrains quick fixes.

## What the second pass fixed

- **Readonly reference arguments (HOO1006).** If a method changes a static
  flag before it reads its `in bool` parameter, `Read(Flag == true)` reads the
  original value, while `Read(Flag)` can read the new one. The fix now retains
  the temporary where removing it would introduce a reference to storage.
- **Compiler-captured source.** A call that captures an expanded `params`
  argument can observe the text of the whole invocation on the .NET 10 SDK,
  including named sibling arguments. On SDK 8.0.100 the compiler instead
  captures the supplied element: `Flag   ==   true` became `Flag` in the
  failing packaged reproduction. The analyzer now uses the source span that
  its compiler actually captures. HOO1001, HOO1003, HOO1004 and HOO1005 now
  format just the statements they change, so a capture in an unrelated
  statement no longer prevents the fix.
- **Terminating branches (HOO1003).** ReSharper reported missed `else if`
  opportunities in Polly’s `PredicateBuilder.TResult.cs` and
  `ResilienceProperties.cs`. HooSharper can now remove the outer `else`
  when the preceding branch cannot continue, while preserving variable
  binding and ownership of an inner `else`. In FluentValidation, a first
  implementation removed one redundant `else` only to expose a second, so
  both methods still had a diagnostic. The fix now handles consecutive
  terminating arms together. An embedded loop regression checks that a
  trailing comment survives exactly once.
- **Indentation inside switch cases.** In Newtonsoft.Json’s
  `Bson/BsonReader.cs`, parts of a fixed case block moved four columns while
  its opening brace stayed put. Formatted changes now preserve the existing
  alignment of the brace and label unless EditorConfig sets that option.
  A mixed-style regression checks that separate cases keep their own layout.
- **Guard examples now work without braces.** HOO1011 (`Dictionary.TryAdd`),
  HOO1012 (`HashSet.Add`), and HOO1018 (null-conditional access) accept the
  single-statement form. Regression tests check comments, named arguments,
  nested guards and variable scope. HOO1018 also rechecks the receiver and
  expression before offering a fix for a stale diagnostic.
- **Source exports.** The previous smoke exporter normalized every file to
  UTF-8 without a preamble. The new exporter preserves encoding, preambles
  and untouched bytes. Replay checks the original raw hashes and verifies
  that the export manifest belongs to the analysis report before changing
  any file. A mixed-encoding test covers UTF-8 and both UTF-16 byte orders.

## What inspecting the rewritten programs caught

ReSharper also inspected a fresh checkout containing the exact output of
HooSharper’s Fix All. This surfaced problems that comparing both tools on
the original checkout did not show:

- **Exception parameter names.** Four projects gained 71 redundant
  caller-expression arguments. HOO1009 now binds the single-argument
  framework call and checks what string the compiler actually supplies.
  It keeps the explicit name for qualified members, escaped identifiers,
  comments, or targets where omission would change `ParamName`.
- **Double negation.** In three Dapper guards, two consecutive fixes produced
  `is not (not null)`. The result is now `is null`. Grouped patterns, enum
  constants and comments have their own regression cases.
- **Nested conditions.** In FluentValidation’s `IncludeRuleSets`, removing
  the outer braces put two `if` statements on the same line and made the
  `else` look attached to the wrong one. The inner `if` and `else` now
  stay together, one indent inside the outer condition.
- **Project formatting and scope.** On Polly.Core, brace removal, line
  wrapping, and guard or `else` rewrites interacted with explicit StyleCop
  settings. The fixes now respect conditional brace consistency, multiline
  children, and blank lines around blocks and comments. An `else` stays in
  place when its removal would need a standalone declaration scope that the
  project explicitly prohibits.
- **Ordered comparisons.** Polly’s time-based circuit breaker exposed
  `!(duration >= threshold)`. Guard fixes can now use `duration < threshold`
  for the framework’s non-nullable `TimeSpan`. They retain the original
  operator for floating point (including NaN), nullable values and custom
  operators. A separate regression covers a type that converts to `int` for
  `>=` but defines its own `<`; reversing that comparison changed the result.
  For equality, the fixer also binds the proposed opposite operator. A
  compiled dependency can supply a custom `!=` while `==` still uses reference
  equality, or vice versa. In that case the guard now negates the original
  comparison instead.
- **Line endings without an EditorConfig setting.** A byte-level audit of
  Dapper caught 400 lone LF line breaks introduced across 17 CRLF-only files
  on macOS. Both SDK formatters reproduced the error on a small nested `if`.
  Fixes now take new separators from the explicit setting or the source near
  the edit. This also applies when moving an inline guard comment onto its
  own line. The qualification checks file bytes as well as diagnostics.
- **Fix All in files with several edits.** One Dapper run compiled and another
  duplicated a switch label in `SqlMapper.cs` (CS0152). After formatting a
  single change, the fixer was returning an entire replacement file. Fix All
  could then merge it with another edit to the same file incorrectly. The
  fixer now reports the changed ranges against the original text, including
  for `using` declarations. A regression puts an untouched switch between
  two edited methods and verifies they compose in either order. The package
  fixture separately checks all 16 labels and both return values per case.

## ReSharper on original and rewritten sources

We ran JetBrains InspectCode 2026.2.3.1 with the same target, settings and
references before and after applying the HooSharper edits. The five libraries
had **3,899 HooSharper findings on modern targets** and **3,774 on .NET
Standard 2.0**. Both sets cleared completely. Across all 20 rules, Fix All
changed 278 of 701 exported source files on the modern targets. All 13,143
original source comments were retained.

| Library | HooSharper, modern | HooSharper, .NET Standard 2.0 | Mapped native findings, before → after | All native findings, before → after |
| --- | ---: | ---: | ---: | ---: |
| Dapper | 295 → 0 | 267 → 0 | 385 → 104 | 1,348 → 1,066 |
| Newtonsoft.Json | 3,128 → 0 | 3,074 → 0 | 3,493 → 599 | 7,987 → 5,084 |
| Serilog | 121 → 0 | 93 → 0 | 155 → 53 | 1,058 → 957 |
| FluentValidation | 239 → 0 | 220 → 0 | 290 → 79 | 1,180 → 967 |
| Polly.Core | 116 → 0 | 120 → 0 | 191 → 85 | 703 → 597 |
| **Total** | **3,899 → 0** | **3,774 → 0** | **4,514 → 920** | **12,276 → 8,671** |

A *mapped native finding* is an InspectCode finding in a category corresponding
to one of 17 HooSharper rules; each finding is counted once. On the originals,
3,550 of 3,805 mapped HooSharper findings overlapped a native finding on the
same source lines. Of the 920 remaining mapped findings, InspectCode classified
861 as hints and 59 as suggestions; none were warnings or errors. Examples
include loop braces, arbitrary
conditional returns, and edits across preprocessor directives. For example,
Dapper’s `SqlMapper.GetValue<T>` contains a warning pragma inside its final
`else` arm; HooSharper leaves the directive-bearing chain intact. The
Newtonsoft.Json `JToken` line-number getter can return either a property value
or zero, outside HOO1017’s opposite-boolean-literals rule. ReSharper also
suggests `ThrowIfNull` on eight `System.Type` arguments tested with an
overloaded equality operator. HooSharper retains that operator.

There were no compiler warnings or errors on either side. Native warnings went
from 1,141 to 1,140, and the one hosted Roslyn analyzer warning was present on
both sides. Polly had the same two native style diagnostics classified as errors
in `TelemetryUtil.cs`. Newtonsoft.Json’s defensive null check on a parameter
declared non-nullable still produces a nullable-contract warning: after
`comparer ??= …`, ReSharper reports it against the coalescing operand. On the
original it flags the null test and also suggests that coalescing assignment.

## Rebuilt upstream programs

The five upstream comparisons covered **5,966 test cases** before and after
Fix All: 5,955 passed, 10 failed on both builds, and the same benchmark was
skipped. The checks compared individual names and outcomes, including
multiplicity. For Dapper, Serilog, FluentValidation, and Polly, the native
comparison and two independent upstream test exports produced byte-identical
source. Dapper cleared 295 → 2 → 0 in all three runs; all 51 files kept their
original line endings, including the 20 that changed.

| Library | Files changed | Test results before and after |
| --- | ---: | --- |
| Dapper 2.1.66 | 20 | All 12 selected SQLite, DuckDB, and wrapped-reader tests passed. The reader probe checked its loaded DLL, parser reuse, and column mapping, and printed `123:abc;456:<NULL>` both times. Database-server suites were not run. |
| Newtonsoft.Json 13.0.3 | 136 | 3,488 passed; the same 10 failed with identical messages. Test IDs and the SHA-256 of the assembly loaded inside each test process were verified. |
| Serilog 4.0.0 | 28 | All 785 passed. |
| FluentValidation 11.11.0 | 45 | 927 passed; the same benchmark was skipped. |
| Polly.Core 8.5.2 | 49 | All 743 passed. |

The Newtonsoft.Json verifier checked all 233 exported files on disk, including
preambles. In `BsonReader.ReadReference`, both affected case labels and their
brace lines still match the original bytes: 16 spaces for each label and
brace, 20 for direct statements.

## Package and regression tests

The Release build produced no warnings or errors. All **694 HooSharper tests**
passed (618 at the end of the first pass), covering 4,441 of 4,707 instrumented
lines (**94.35%**). The 13 Python comparison/export tests and all 16 analyzer
and 16 code-fix benchmark fixtures passed. The executable rule matrix covered
all 20 IDs and cleared 48 findings in one round with identical program output.

We installed the same local package on SDK 8.0.100 and 10.0.109 and ran the
actual SDK formatters. Both preserved the readonly snapshot and the precise
captured argument text; both applied the unrelated simplification and the
three unbraced fixes. The older SDK could simplify an extra sibling that its
compiler did not capture. Deliberately applying the unsafe rewrites changed
both the snapshot and the captured string on each SDK. Both also passed the
configured and unconfigured CRLF checks. The [CI package fixture](../../tests/smoke/PackageConsumer/Program.cs)
runs before and after formatting on both SDKs. The separate switch fixture
checks the rewritten bytes and compiles with diagnostics treated as errors.

On DScaf (`14650181`, SDK 10.0.401), the package fixed all 38 findings across
14 files. The rebuilt project had zero warnings and errors, and all 701 tests
passed. DScaf is a package consumer and is separate from the native comparison.

| Unpublished 0.3.13 artifact | SHA-256 |
| --- | --- |
| Analyzer | `ecbcc9d6bc93d14a4c3c886965b740e04b02739832e23d3a81e189a738eb3fdc` |
| Code fixes | `4221df3d50f62d3ba2f592a1d5b58b11783697b1833754cf6b92207a087d7942` |
| Local NuGet package | `1dbb080da6fd85179e48d9f38e1e0ecf444fe79606c3e3fed8774798f65ed87a` |

The [comparison commands](../../README.md#executable-smoke-tests-and-resharper-comparisons)
recreate the native and upstream runs in disposable checkouts. For Newtonsoft.Json,
pass the native report and its paired export to
[`verify-newtonsoft-tests.py`](../../scripts/verify-newtonsoft-tests.py).
