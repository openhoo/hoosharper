## Release 0.1.0

### New rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
HOO1001 | HooSharper.CodeStyle | Info | Prefer a guard clause when an if wraps the remainder of a void method.
HOO1002 | HooSharper.CodeStyle | Info | Omit braces from safe single-statement if branches.

## Release 0.2.0

### New rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
HOO1003 | HooSharper.CodeStyle | Info | Remove a redundant else after a terminating if branch.
HOO1004 | HooSharper.CodeStyle | Info | Prefer a continue guard when an if wraps the remaining loop body.
HOO1005 | HooSharper.CodeStyle | Info | Replace an as cast and null check with a type pattern.
HOO1006 | HooSharper.CodeStyle | Info | Simplify comparisons between bool expressions and boolean literals.
HOO1007 | HooSharper.CodeStyle | Info | Use TryGetValue instead of ContainsKey followed by an index access.
HOO1008 | HooSharper.CodeStyle | Info | Replace a null check and assignment with the null-coalescing assignment operator.
HOO1009 | HooSharper.CodeStyle | Info | Replace a classic argument null guard with ArgumentNullException.ThrowIfNull.


## Release 0.3.0

### New rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
HOO1010 | HooSharper.CodeStyle | Info | Merge nested if statements without else branches.
HOO1011 | HooSharper.CodeStyle | Info | Use Dictionary.TryAdd instead of ContainsKey followed by Add.
HOO1012 | HooSharper.CodeStyle | Info | Use the result of HashSet.Add instead of a separate Contains check.
HOO1013 | HooSharper.CodeStyle | Info | Replace a terminal using statement with a using declaration.
HOO1014 | HooSharper.CodeStyle | Info | Replace a conditional null expression with the null-coalescing operator.
HOO1015 | HooSharper.CodeStyle | Info | Replace a conditional null expression with null-conditional access.
HOO1016 | HooSharper.CodeStyle | Info | Use string.Contains when only IndexOf presence is tested.
HOO1017 | HooSharper.CodeStyle | Info | Simplify adjacent opposite boolean returns.
HOO1018 | HooSharper.CodeStyle | Info | Remove a redundant null guard around null-conditional access.
HOO1019 | HooSharper.CodeStyle | Info | Replace a negated is expression with a not pattern.
HOO1020 | HooSharper.CodeStyle | Info | Wrap long fluent chains with continuation dots at line starts.