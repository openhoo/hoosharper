# Unshipped analyzer releases

## New rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
HOO1001 | HooSharper.CodeStyle | Info | Prefer a guard clause when an if wraps the remainder of a void method.
HOO1002 | HooSharper.CodeStyle | Info | Omit braces from safe single-statement if branches.
HOO1003 | HooSharper.CodeStyle | Info | Remove a redundant else after a terminating if branch.
HOO1004 | HooSharper.CodeStyle | Info | Prefer a continue guard when an if wraps the remaining loop body.
HOO1005 | HooSharper.CodeStyle | Info | Replace an as cast and null check with a type pattern.
HOO1006 | HooSharper.CodeStyle | Info | Simplify comparisons between bool expressions and boolean literals.
HOO1007 | HooSharper.CodeStyle | Info | Use TryGetValue instead of ContainsKey followed by an index access.
HOO1008 | HooSharper.CodeStyle | Info | Replace a null check and assignment with the null-coalescing assignment operator.
HOO1009 | HooSharper.CodeStyle | Info | Replace a classic argument null guard with ArgumentNullException.ThrowIfNull.

## Removed rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
