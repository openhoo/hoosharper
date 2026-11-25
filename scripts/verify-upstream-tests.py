#!/usr/bin/env python3
"""Run pinned Dapper, Serilog, FluentValidation, and Polly.Core tests before and after HooSharper Fix All."""

import argparse
from collections import Counter
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import xml.etree.ElementTree as ET

from smoke_support import capture, replay_export, validate_export

spec = importlib.util.spec_from_file_location("qualification", Path(__file__).with_name("verify-resharper-smoke.py"))
qualification = importlib.util.module_from_spec(spec)
spec.loader.exec_module(qualification)

SUITES = {"dapper": "tests/Dapper.Tests/Dapper.Tests.csproj",
          "serilog": "test/Serilog.Tests/Serilog.Tests.csproj",
          "fluentvalidation": "src/FluentValidation.Tests/FluentValidation.Tests.csproj",
          "polly": "test/Polly.Core.Tests/Polly.Core.Tests.csproj"}
DAPPER_FILTER = "|".join(f"FullyQualifiedName~Dapper.Tests.{name}"
                        for name in ("Sqlite", "DuckDBTests", "WrappedReaderTests"))


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def environment(dotnet):
    return dict(os.environ, DOTNET_PROCESSOR_COUNT="2", DOTNET_ROOT=str(dotnet.parent),
                PATH=str(dotnet.parent) + os.pathsep + os.environ["PATH"], HOOSHARPER_SMOKE_DOTNET=str(dotnet))


def check_repeated_run(name, first, repeated):
    # A later round can hide an earlier batch merge that dropped an edit. Check
    # both isolated rules and their cumulative progress, as well as final bytes.
    for field in ("sourceByteHashes", "findings", "fixes", "cumulativeRounds"):
        if first[field] != repeated[field]:
            raise RuntimeError(f"{name}: repeated Fix All disagreed on {field}; compare both smoke reports")


def test_library(label, library, suite, work, host, args, test_filter):
    # Rebuild even when two file copies have the same timestamp. Verify which
    # library the test output contains before launching its upstream suite.
    capture([str(args.dotnet), "build", str(suite), "-t:Rebuild", "-c", "Release", "-m:1",
             "-p:TargetFrameworks=net8.0", "-p:TreatWarningsAsErrors=false", "-p:UseArtifactsOutput=false"],
            work / f"{label}-build.log", environment(args.dotnet), cwd=qualification.ROOT)
    binaries = Path("bin/Release/net8.0")
    library_dll = library.parent / binaries / (library.stem + ".dll")
    test_dll = suite.parent / binaries / (suite.stem + ".dll")
    digest = sha256(library_dll)
    if sha256(test_dll.parent / library_dll.name) != digest:
        raise RuntimeError(f"{label}: test output contains a different library assembly")
    trx = work / f"{label}.trx"
    command = [str(args.test_dotnet), "vstest", str(test_dll), f"--logger:trx;LogFileName={trx.name}",
               f"--ResultsDirectory:{work}"]
    if test_filter:
        command.append(f"--TestCaseFilter:{test_filter}")
    capture(command, work / f"{label}-test.log", environment(args.test_dotnet), cwd=host)
    results = Counter((r.attrib["testName"], r.attrib["outcome"])
                      for r in ET.parse(trx).findall(".//{*}UnitTestResult"))
    counts = Counter()
    for (_, outcome), count in results.items():
        counts[outcome] += count
    if not counts["Passed"] or set(counts) - {"Passed", "NotExecuted"}:
        raise RuntimeError(f"{label}: no tests passed or unexpected outcomes: {counts}")
    if test_filter == DAPPER_FILTER and counts != {"Passed": 12}:
        raise RuntimeError(f"{label}: the pinned SQLite, DuckDB, and wrapped-reader tests must all run: {counts}")
    result = {"librarySha256": digest, "testAssembly": str(test_dll), "trx": str(trx), "counts": dict(counts)}
    if test_filter == DAPPER_FILTER:
        # Run while this exact build is on disk. The probe verifies the hash of
        # the assembly it loaded before checking row mapping and parser reuse.
        probe = qualification.ROOT / "tests/smoke/DapperReader/DapperReader.csproj"
        output = work / f"{label}-reader"
        capture([str(args.dotnet), "build", str(probe), "-c", "Release", f"-p:DapperDll={library_dll}",
                 "--artifacts-path", str(output / "artifacts"), "--output", str(output / "bin")],
                work / f"{label}-reader-build.log", environment(args.dotnet), cwd=qualification.ROOT)
        log = work / f"{label}-reader.log"
        capture([str(args.test_dotnet), str(output / "bin/DapperReader.dll"), str(library_dll)],
                log, environment(args.test_dotnet), cwd=host)
        if log.read_text().splitlines() != ["123:abc;456:<NULL>"]:
            raise RuntimeError(f"{label}: unexpected reader result in {log}")
        result["readerOutput"] = log.read_text().strip()
    return result, results


def run_suite(item, work, host, args):
    name, library, properties, source = qualification.prepare_project(item, work, environment(args.dotnet))
    suite = work / "projects" / item[1] / SUITES[name]
    test_filter = DAPPER_FILTER if name == "dapper" else None
    before, original_results = test_library(f"{name}-before", library, suite, work, host, args, test_filter)
    fixed = work / f"{name}-fixed"
    report = qualification.smoke(name, library, properties, work, args, environment(args.dotnet), fixed)
    manifest = validate_export(fixed, report)
    # Equal diagnostic counts can conceal different rewrites of overlapping
    # syntax. Run on the unchanged checkout again and compare every final file.
    repeated = work / f"{name}-fixed-repeat"
    repeat_report = qualification.smoke(f"{name}-repeat", library, properties, work, args,
                                        environment(args.dotnet), repeated)
    repeat_manifest = validate_export(repeated, repeat_report)
    qualification.check_assembly_hashes(work, [name, f"{name}-repeat"])
    check_repeated_run(name, report, repeat_report)
    differences = sorted(path for path in manifest.keys() | repeat_manifest.keys()
                         if manifest.get(path) != repeat_manifest.get(path))
    if differences:
        raise RuntimeError(f"{name}: repeated Fix All produced different source in: {', '.join(differences)}")
    if any(finding["file"] not in manifest for finding in report["findings"]):
        raise RuntimeError(f"{name}: cannot test rewrites outside the local source export")
    changed = replay_export(library.parent, fixed, report)
    if not changed:
        raise RuntimeError(f"{name}: no source file changed")
    after, fixed_results = test_library(f"{name}-after", library, suite, work, host, args, test_filter)
    if fixed_results != original_results:
        raise RuntimeError(f"{name}: test names, outcomes, or multiplicity changed; see the two TRX reports")
    return {"source": source, "analyzerSha256": report["analyzerSha256"], "fixerSha256": report["fixerSha256"],
            "changedFiles": changed, "fixAllRounds": report["cumulativeRounds"],
            "repeatedFixAllMatches": True, "comparedSourceFiles": len(manifest),
            "testFilter": test_filter, "before": before, "after": after, "sameNamedTestOutcomes": True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", type=Path, required=True, help="repository .NET 10 SDK executable")
    parser.add_argument("--test-dotnet", type=Path, required=True, help=".NET 8 SDK executable with its test runtime")
    parser.add_argument("--work-dir", type=Path, required=True, help="new or empty disposable directory")
    parser.add_argument("--project", choices=sorted(SUITES), action="append",
                        help="run only this upstream suite; repeat to select multiple libraries")
    args = parser.parse_args()
    args.dotnet, args.test_dotnet = args.dotnet.resolve(), args.test_dotnet.resolve()
    work = args.work_dir.resolve()
    work.mkdir(parents=True, exist_ok=True)
    if any(work.iterdir()):
        parser.error("--work-dir must be new or empty; its library checkouts will be rewritten")
    host = work / "test-host"
    host.mkdir()
    (host / "global.json").write_text('{"sdk":{"version":"8.0.100","rollForward":"latestFeature"}}\n')
    capture([str(args.test_dotnet), "--info"], work / "test-host-info.log", environment(args.test_dotnet), cwd=host)
    capture([str(args.dotnet), "build", "tools/HooSharper.SmokeTests/HooSharper.SmokeTests.csproj",
             "-c", "Release", "-m:1"], work / "smoke-tool-build.log", environment(args.dotnet), cwd=qualification.ROOT)
    selected = set(args.project or SUITES)
    result = {item[0]: run_suite(item, work, host, args) for item in qualification.PROJECTS if item[0] in selected}
    qualification.check_assembly_hashes(work, result)
    (work / "upstream-tests.json").write_text(json.dumps(result, indent=2) + "\n")
    for name, suite in result.items():
        print(f"{name}: {len(suite['changedFiles'])} files changed; both runs {suite['after']['counts']}")


if __name__ == "__main__":
    main()
