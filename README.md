# HooSharper

HooSharper is an open-source collection of Roslyn analyzers and code fixes for opinionated C# formatting and code style. It aims to provide the everyday inspections and quick-fixes developers commonly rely on ReSharper for, while running through the standard .NET compiler and IDE analyzer infrastructure.

The package currently focuses on guard clauses and compact single-statement conditionals.

## Requirements

- A C# project using an SDK-style project file
- A Roslyn-capable editor or build environment, such as Visual Studio, Rider, VS Code with C# tooling, or `dotnet build`
- This repository itself builds with .NET SDK 10.0.110 or a compatible .NET 10 patch

The analyzer package targets `netstandard2.0` so it can run in a broad range of Roslyn hosts. Projects consuming the analyzer do not need to target .NET 10.

## Installation

### Install from NuGet

Once the package is published, add it to each project that should be analyzed:

```bash
dotnet add package HooSharper.Analyzers
```

For central package management, add the version to `Directory.Packages.props`:

```xml
<Project>
  <ItemGroup>
    <PackageVersion Include="HooSharper.Analyzers" Version="1.0.0" />
  </ItemGroup>
</Project>
```

Then reference it from each project:

```xml
<ItemGroup>
  <PackageReference Include="HooSharper.Analyzers" PrivateAssets="all" />
</ItemGroup>
```

Without central package management:

```xml
<ItemGroup>
  <PackageReference Include="HooSharper.Analyzers"
                    Version="1.0.0"
                    PrivateAssets="all" />
</ItemGroup>
```

`PrivateAssets="all"` prevents an application or library from exposing HooSharper as a transitive runtime dependency. The package contains both the analyzer assembly and its code-fix assembly under `analyzers/dotnet/cs`.

### Install a locally built package

Build the NuGet package:

```bash
dotnet build HooSharper.slnx -c Release
dotnet pack src/HooSharper.Analyzers/HooSharper.Analyzers.csproj \
  -c Release \
  --no-build \
  -o artifacts
```

Add the local package directory as a source and install it into another project:

```bash
dotnet nuget add source /absolute/path/to/hoosharper/artifacts \
  --name HooSharperLocal

dotnet add package HooSharper.Analyzers \
  --version 1.0.0 \
  --source /absolute/path/to/hoosharper/artifacts
```

Alternatively, add the local source in `NuGet.config`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="HooSharperLocal" value="/absolute/path/to/hoosharper/artifacts" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

## Using diagnostics and code fixes

After adding the package:

1. Restore and build the project.
2. Open a C# file in a Roslyn-capable IDE.
3. Place the caret on a HooSharper diagnostic.
4. Open Quick Actions. In Visual Studio and VS Code this is normally `Ctrl+.`; Rider commonly uses `Alt+Enter`.
5. Select the HooSharper action.

Both current fixers use Roslyn's batch Fix All provider. Where the host supports it, the action can be applied to the document, project, or solution.

Analyzer diagnostics also run during `dotnet build`. Code fixes are interactive IDE operations; a command-line build reports diagnostics but does not rewrite source files.

## Configuration

HooSharper uses normal Roslyn diagnostic configuration. Add settings under the `[*.cs]` section of the repository's `.editorconfig`.

### Severity values

Each rule can use one of these values:

- `error` — fails the build
- `warning` — appears as a warning
- `suggestion` — appears as an IDE suggestion/build message
- `silent` — hidden but available to IDE features
- `none` — disables the rule
- `default` — restores the descriptor's default severity

Example configuration:

```ini
root = true

[*.cs]
dotnet_diagnostic.HOO1001.severity = warning
dotnet_diagnostic.HOO1002.severity = suggestion
```

Both current diagnostics are enabled by default with Roslyn `Info` severity.

### Disable a rule

```ini
[*.cs]
dotnet_diagnostic.HOO1002.severity = none
```

### Treat a rule as a build error

```ini
[*.cs]
dotnet_diagnostic.HOO1001.severity = error
```

### Configure a subtree differently

EditorConfig sections and file hierarchy determine scope. For example, enforce early returns in production code but disable the rule in generated compatibility sources:

```ini
[*.cs]
dotnet_diagnostic.HOO1001.severity = warning

[src/Compatibility/**/*.cs]
dotnet_diagnostic.HOO1001.severity = none
```

### Suppress a specific occurrence

Prefer correcting the code or configuring the rule by scope. When a single occurrence must remain, use a standard diagnostic suppression:

```csharp
#pragma warning disable HOO1001
// Intentionally nested control flow.
#pragma warning restore HOO1001
```

A project-wide MSBuild suppression also works, but is less visible than `.editorconfig`:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);HOO1001</NoWarn>
</PropertyGroup>
```

## Rules

### HOO1001: Prefer an early return

- Category: `HooSharper.CodeStyle`
- Default severity: Info
- Enabled by default: Yes
- Code fix: **Invert condition and return early**
- Fix All: Yes

HOO1001 reports a final `if` statement that wraps the remaining work of a `void` method.

Before:

```csharp
void Run(bool enabled)
{
    Prepare();

    if (enabled)
    {
        Execute();
        Finish();
    }
}
```

After:

```csharp
void Run(bool enabled)
{
    Prepare();

    if (!enabled)
        return;

    Execute();
    Finish();
}
```

The fixer simplifies common negations and comparisons. Examples include:

```csharp
if (!disabled)  // becomes: if (disabled) return;
if (value == 0) // becomes: if (value != 0) return;
if (value < 10) // becomes: if (value >= 10) return;
```

The current implementation is intentionally conservative. A diagnostic is reported only when all of these conditions hold:

- The containing declaration is a method with the exact return type `void`.
- The `if` is the last statement in the method body.
- The `if` has no `else` branch.
- The body is a nonempty block.

It currently does not report for:

- Value-returning methods
- Local functions, constructors, accessors, operators, or lambdas
- An `if` followed by another statement
- An `if` with an `else`
- Empty blocks

Configure it with:

```ini
[*.cs]
dotnet_diagnostic.HOO1001.severity = warning
```

### HOO1002: Omit braces for a single-statement if

- Category: `HooSharper.CodeStyle`
- Default severity: Info
- Enabled by default: Yes
- Code fix: **Remove braces**
- Fix All: Yes

HOO1002 reports braces around a safe single-statement `if` or `else` branch.

Before:

```csharp
if (enabled)
{
    Execute();
}
```

After:

```csharp
if (enabled)
    Execute();
```

It also handles an `else` branch:

```csharp
if (enabled)
    Execute();
else
    Finish();
```

The analyzer does not remove braces when the block:

- Contains zero or multiple statements
- Declares a local variable
- Declares a local function
- Contains preprocessor directives
- Is the block of an `else if`; the nested `if` is analyzed independently

These restrictions avoid changing declaration scope or damaging conditional-compilation structure.

Configure it with:

```ini
[*.cs]
dotnet_diagnostic.HOO1002.severity = suggestion
```

The standard .NET style option expresses the same general brace preference for built-in IDE tooling:

```ini
csharp_prefer_braces = false:warning
```

HOO1002 remains independently configurable through `dotnet_diagnostic.HOO1002.severity`. The built-in option and HooSharper rule may both report in hosts that enable both analyzers. Disable one diagnostic if duplicate suggestions appear.

## Recommended configuration

A reasonable starting point is:

```ini
[*.cs]
# Prefer guard clauses but introduce them gradually.
dotnet_diagnostic.HOO1001.severity = suggestion

# Enforce compact single-statement conditionals.
dotnet_diagnostic.HOO1002.severity = warning
csharp_prefer_braces = false:warning
```

For CI enforcement:

```ini
[*.cs]
dotnet_diagnostic.HOO1001.severity = warning
dotnet_diagnostic.HOO1002.severity = warning
```

Projects using `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` will fail when either rule is configured as a warning.

## Development

Clone and build:

```bash
git clone https://github.com/openhoo/hoosharper.git
cd hoosharper
dotnet restore HooSharper.slnx
dotnet build HooSharper.slnx
dotnet test HooSharper.slnx
```

Run a release build and package:

```bash
dotnet build HooSharper.slnx -c Release
dotnet test HooSharper.slnx -c Release --no-build
dotnet pack src/HooSharper.Analyzers/HooSharper.Analyzers.csproj \
  -c Release \
  --no-build \
  -o artifacts
```

Check dependency updates:

```bash
dotnet list HooSharper.slnx package --outdated
```

### Project layout

```text
src/HooSharper.Analyzers/       DiagnosticAnalyzer implementations
src/HooSharper.CodeFixes/       CodeFixProvider implementations
tests/HooSharper.Analyzers.Tests/ Analyzer and code-fix tests
artifacts/                       Local NuGet packages; ignored by Git
```

Each rule has:

- One analyzer file
- One code-fix provider file
- One dedicated test file
- One entry in `AnalyzerReleases.Unshipped.md`

Analyzer rules use the `HOO` diagnostic prefix.

### Testing conventions

Tests use Microsoft's generic Roslyn testing packages with `DefaultVerifier`, xUnit v3, .NET 10 reference assemblies, and the latest C# parse mode.

A code-fix test should verify:

- Exact diagnostic ID
- Exact diagnostic location using Roslyn markup such as `{|#0:if|}`
- Exact fixed source text
- That fixable diagnostics are removed
- Incremental application
- Fix All behavior when supported
- Negative cases where no diagnostic should be emitted

Run one test class while developing:

```bash
dotnet test tests/HooSharper.Analyzers.Tests/HooSharper.Analyzers.Tests.csproj \
  --filter FullyQualifiedName~PreferEarlyReturnAnalyzerTests
```

The custom test verifier is in `tests/HooSharper.Analyzers.Tests/AnalyzerVerifier.cs`.

## Packaging

`HooSharper.Analyzers.csproj` produces `HooSharper.Analyzers.<version>.nupkg`. The package contains:

```text
analyzers/dotnet/cs/HooSharper.Analyzers.dll
analyzers/dotnet/cs/HooSharper.CodeFixes.dll
README.md
```

The analyzer assembly is loaded by the compiler and IDE. The code-fix assembly is used by IDE hosts that discover Roslyn code-fix providers. Neither assembly is a runtime application dependency.

Inspect a locally produced package with:

```bash
unzip -l artifacts/HooSharper.Analyzers.1.0.0.nupkg
```

## Contributing a rule

1. Choose the next `HOO` diagnostic ID.
2. Add one analyzer under `src/HooSharper.Analyzers`.
3. Add one code-fix provider under `src/HooSharper.CodeFixes` when the transformation is safe and deterministic.
4. Add a dedicated test file under `tests/HooSharper.Analyzers.Tests`.
5. Register the rule in `src/HooSharper.Analyzers/AnalyzerReleases.Unshipped.md`.
6. Document the rule and its configuration in this README.
7. Run the full build, tests, and package verification.

New fixers should preserve trivia, request Roslyn formatting only for changed nodes, support Fix All when transformations are independent, and avoid offering fixes that could change behavior.

## License

HooSharper is licensed under the [MIT License](LICENSE).
