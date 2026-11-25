# Dapper reader smoke test

This standalone probe targets Dapper 2.1.66 on .NET 8. It checks parser caching,
column mapping in reversed order, and conversion of `DBNull` to null without a
database server. It references the DLL you supply at build time and checks the
hash of the loaded assembly at runtime.

From the HooSharper repository root, set both DLL paths to separately built
`net8.0` assemblies. Use the repository's .NET SDK and a .NET 8 runtime. A fresh
directory for each build keeps the intermediate files and copied DLLs separate.

```sh
set -eu
before=/absolute/path/to/original/Dapper.dll
after=/absolute/path/to/rewritten/Dapper.dll
work=$(mktemp -d)

probe() {
  label=$1
  dll=$2
  dotnet build tests/smoke/DapperReader/DapperReader.csproj -c Release \
    "-p:DapperDll=$dll" --artifacts-path "$work/$label/artifacts" \
    --output "$work/$label/run"
  dotnet "$work/$label/run/DapperReader.dll" "$dll" > "$work/$label/output.txt"
}

probe before "$before"
probe after "$after"
diff -u "$work/before/output.txt" "$work/after/output.txt"
cat "$work/after/output.txt"
```

Both runs should print `123:abc;456:<NULL>`.
