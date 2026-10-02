# Diagnostic configuration and fix boundaries

Current HOO rule families:

| IDs | Behavior |
| --- | --- |
| HOO1001, HOO1004 | Early return / loop continue |
| HOO1002 | Omit braces for a single-statement if |
| HOO1003, HOO1010 | Redundant else / nested if |
| HOO1005, HOO1019 | Type / not patterns |
| HOO1006, HOO1017 | Boolean comparison / return simplification |
| HOO1007, HOO1011, HOO1012 | Dictionary lookup / TryAdd / HashSet.Add result |
| HOO1008, HOO1014, HOO1015, HOO1018 | Null assignment, coalescing, conditional access, redundant guard |
| HOO1009 | ArgumentNullException.ThrowIfNull |
| HOO1013 | Terminal using declaration |
| HOO1016 | String presence test with Contains |
| HOO1020 | Long fluent-chain wrapping |

Defaults are enabled Info diagnostics in category `HooSharper.CodeStyle`.
EditorConfig severities: `error`, `warning`, `suggestion`, `silent`, `none`,
`default`. `TreatWarningsAsErrors` also affects rules configured as warnings.
An Info/suggestion may not be prominent in a build; use the IDE or temporarily
set a narrowly scoped rule to warning to verify loading.

```ini
[*.cs]
dotnet_analyzer_diagnostic.category-HooSharper.CodeStyle.severity = suggestion
dotnet_diagnostic.HOO1007.severity = warning
hoosharper_max_line_length = 140

[src/Compatibility/**/*.cs]
dotnet_diagnostic.HOO1009.severity = none
```

HOO1020's specific key takes precedence over `max_line_length`. HOO1002 may
conflict with a project's braces preference; reconcile that preference explicitly
rather than accepting contradictory formatting churn.

All current fixers use Roslyn's batch Fix All provider, where the IDE supports
it. They deliberately skip ambiguous or unsafe cases: custom/overloaded
operators, unstable expressions, directives, unavailable APIs, language-version
restrictions, expression trees, scope collisions, changed disposal lifetime,
dictionary writes/by-ref accesses, and mutable comparer inputs.
Conditional-access or already-multiline chains are not rewritten by HOO1020.
Absence of a diagnostic in these cases can be intentional.

Prefer an EditorConfig subtree setting or one occurrence's `#pragma warning`
with an explanation over project-wide `NoWarn`. Keep diagnostic IDs and expected
behavior in the consumer's adoption notes; the full upstream README describes
each rule's finer eligibility boundaries.
