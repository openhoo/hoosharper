---
name: hoosharper-usage
description: Install HooSharper in C# consumer projects, configure analyzer severities and scope, understand HOO diagnostics, and apply or verify code fixes without changing application behavior.
---

# Use HooSharper in a C# project

Work in the consuming project. Inspect its SDK-style project files, central
package management, `.editorconfig`, target frameworks, and warning policy.
Installing this skill supplies guidance; install the analyzer package separately.

## Install and configure

The supported NuGet package is `HooSharper.Analyzers`; it includes analyzers and
code fixes. Version 0.3.10 is a concrete example, not a latest-version claim.
Use the project's approved version when one exists:

```bash
dotnet add package HooSharper.Analyzers --version 0.3.10
```

Keep `PrivateAssets="all"` on its PackageReference. For central package
management put the version in `Directory.Packages.props` and omit it from the
project reference. The package targets `netstandard2.0`; consumer applications
do not have to target the .NET 10 SDK used to build HooSharper itself.

Start with suggestions and promote selected rules only when the project intends
to enforce them. Preserve existing EditorConfig scope and other analyzers:

```ini
[*.cs]
dotnet_diagnostic.HOO1001.severity = suggestion
dotnet_diagnostic.HOO1002.severity = suggestion
```

Read [diagnostics.md](references/diagnostics.md) for supported rules, precedence,
severity, and fix boundaries. Avoid changing the entire project's warning policy
merely to introduce one analyzer.

## Apply and verify

1. Restore/build the consuming project and confirm the expected HOO ID appears
   on an intentional example. Check its effective EditorConfig severity.
2. Use the IDE's Quick Actions for the matching HooSharper fix. A command-line
   `dotnet build` reports diagnostics; it does not rewrite source.
3. Review the resulting diff for comments, evaluation order, and scope. Run the
   consuming project's normal build/tests. For broad Fix All, begin with a
   document or bounded project before expanding to a solution.
4. Include a nearby example where no change should occur. A clean build alone
   does not show that the analyzer loaded or that its code fix is available.

For missing diagnostics check project/package restore, rule severity, file
scope, host Roslyn compatibility, language/framework availability, and the rule's
conservative exclusions. For diagnostics without actions verify the package
contains/loads its code-fix assembly and that the IDE supports Roslyn code fixes.
Use narrow suppressions with a reason for intentional exceptions. Do not disable
all HOO rules to make one disputed warning disappear.
