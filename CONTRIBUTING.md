# Contributing

Open an issue before changing analyzer IDs, default severities, diagnostics, or
code-fix semantics. Small fixes may go directly to a pull request.

## Development

Use the .NET SDK from `global.json` and the Bun version pinned by workflows.

```sh
dotnet restore HooSharper.slnx
dotnet build HooSharper.slnx -c Release --no-restore
dotnet test HooSharper.slnx -c Release --no-build
dotnet run --project benchmarks/HooSharper.Performance/HooSharper.Performance.csproj \
  -c Release --no-build -- --validate-fixtures
bun install --frozen-lockfile
bun run check-readme-version
```

Analyzer changes need positive, negative, trivia, malformed-code, and fix-all
coverage. Shipped diagnostic IDs and meanings are compatibility contracts.

Commits use Conventional Commits. Pull requests must explain compatibility and
diagnostic impact. Maintainers squash-merge using the Conventional Commit pull
request title. Lockfile and analyzer-release metadata changes must accompany
their source changes.

## Release pull requests

Preserve the generated Hooversion release commit subject, including the package
name, when squash-merging a release pull request (for example,
`chore(release): HooSharper.Analyzers 0.3.11`). Preserve its generated release
notes as the squash commit body. Hooversion uses this message to resume tagging
and publication without creating another version commit.
