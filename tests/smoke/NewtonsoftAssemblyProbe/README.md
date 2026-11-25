# Verify the Newtonsoft.Json Release rewrite

Run the pinned native comparison with `--project newtonsoft` first. Then pass
its `newtonsoft-hoosharper.json` and matching `newtonsoft-fixed-*` directory to
the test verifier. The build host uses the SDK in the repository's `global.json`.
The test host also needs that SDK and the .NET 6 runtime used by Newtonsoft.Json
13.0.3. Both paths may point to the same installation if it contains both.

```sh
python3 scripts/verify-newtonsoft-tests.py \
  --dotnet /path/to/sdk/dotnet \
  --test-dotnet /path/to/sdk-with-net6-runtime/dotnet \
  --report /path/to/native/newtonsoft-hoosharper.json \
  --fixed /path/to/native/newtonsoft-fixed-XXXXXX \
  --work-dir /path/to/new-and-empty-directory
```

The script builds the original library, runs all 3,498 tests, verifies each
original source hash, applies the exact export, then rebuilds and reruns the
suite. This startup hook records the library path and SHA-256 inside VSTest's
testhost process. An existing local upstream clone can be supplied with
`--source`; only a new checkout in the work directory is modified.

The work directory contains the original and rewritten DLLs, TRX reports,
build and test logs, failure messages, source hashes, and `summary.json`.
A baseline failure is accepted only when the same test ID and name have the
same outcome and failure message after fixing. Missing, skipped, or
changed outcomes fail verification. A passing comparison does not mean the
upstream baseline was green.
