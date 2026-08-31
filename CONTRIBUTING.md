# Contributing

Open an issue before changing analyzer IDs, default severities, diagnostics, or
code-fix semantics. Small fixes may go directly to a pull request.

## Development

Use the .NET SDK from `global.json` and the Bun version pinned by workflows.

```sh
dotnet restore HooSharper.slnx
dotnet build HooSharper.slnx -c Release --no-restore
dotnet test HooSharper.slnx -c Release --no-build
bun install --frozen-lockfile
bun run check-readme-version
```

Analyzer changes need positive, negative, trivia, malformed-code, and fix-all
coverage. Shipped diagnostic IDs and meanings are compatibility contracts.

Commits use Conventional Commits. Pull requests must explain compatibility and
diagnostic impact. Maintainers squash-merge using the Conventional Commit pull
request title. Lockfile and analyzer-release metadata changes must accompany
their source changes.
