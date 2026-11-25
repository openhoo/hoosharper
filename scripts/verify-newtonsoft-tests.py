#!/usr/bin/env python3
"""Test the exact net6.0 Release Newtonsoft.Json export from verify-resharper-smoke.py."""

import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET

from smoke_support import capture, replay_export, validate_export

ROOT = Path(__file__).resolve().parents[1]
COMMIT = "0a2e291c0d9c0c7675d445703e51750363a549ef"
REPOSITORY = "https://github.com/JamesNK/Newtonsoft.Json.git"
EXPECTED_TESTS = 3498
LIBRARY = Path("Src/Newtonsoft.Json")
SUITE = Path("Src/Newtonsoft.Json.Tests/Newtonsoft.Json.Tests.csproj")
PROPERTIES = {"TargetFramework": "net6.0", "LibraryFrameworks": "net6.0", "Configuration": "Release",
              "_SkipUpgradeNetAnalyzersNuGetWarning": "true", "TreatWarningsAsErrors": "false"}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def host_environment(host):
    return dict(os.environ, DOTNET_ROOT=str(host.parent), DOTNET_ROOT_ARM64=str(host.parent),
                DOTNET_ROOT_X64=str(host.parent), DOTNET_HOST_PATH=str(host), DOTNET_PROCESSOR_COUNT="2",
                PATH=str(host.parent) + os.pathsep + os.environ["PATH"])


def read_tests(trx):
    root = ET.parse(trx)
    outcomes, failures = Counter(), Counter()
    for row in root.findall(".//{*}UnitTestResult"):
        identity = (row.attrib["testId"], row.attrib["testName"])
        outcome = row.attrib["outcome"]
        outcomes[(*identity, outcome)] += 1
        if outcome == "Failed":
            failures[(*identity, row.findtext("{*}Output/{*}ErrorInfo/{*}Message", ""))] += 1
    counts = Counter(outcome for _, _, outcome in outcomes.elements())
    if sum(counts.values()) != EXPECTED_TESTS or not counts["Passed"] or set(counts) - {"Passed", "Failed"}:
        raise RuntimeError(f"Incomplete or unexpected upstream results in {trx}: {counts}")
    return outcomes, failures, dict(counts)


def test_library(label, args, work, src, hook):
    variant = work / label
    loads = variant / "loaded"
    loads.mkdir(parents=True)
    project = src / SUITE
    options = ["-c", "Release", "-f", "net6.0", "-p:TestFrameworks=net6.0"]
    options += [f"-p:{key}={value}" for key, value in PROPERTIES.items() if key not in {"TargetFramework", "Configuration"}]
    capture([str(args.dotnet), "build", str(project), "-t:Rebuild", "-m:1", *options],
            variant / "build.log", host_environment(args.dotnet), cwd=project.parent)
    library = src / LIBRARY / "bin/Release/net6.0/Newtonsoft.Json.dll"
    loaded = project.parent / "bin/Release/net6.0" / library.name
    expected = digest(library)
    if digest(loaded) != expected:
        raise RuntimeError(f"{label}: test output does not contain the built assembly")
    shutil.copyfile(loaded, variant / library.name)
    trx = variant / "tests.trx"
    env = host_environment(args.test_dotnet)
    env.update(DOTNET_STARTUP_HOOKS=str(hook), HOOSHARPER_NEWTONSOFT_LOADS=str(loads))
    # A baseline may legitimately fail on this platform. Exit 1 is accepted only
    # when the completed results contain failed tests and all cases ran.
    command = [str(args.test_dotnet), "test", str(project), "--no-build", "--no-restore", *options,
               "--logger", "trx;LogFileName=tests.trx", "--results-directory", str(variant)]
    with (variant / "test.log").open("wb") as log:
        code = subprocess.run(command, env=env, cwd=project.parent, stdout=log,
                              stderr=subprocess.STDOUT, timeout=1200).returncode
    outcomes, failures, counts = read_tests(trx)
    if code != (1 if failures else 0):
        raise RuntimeError(f"{label}: test exit code {code} does not match the TRX results")
    observed = [line.split("\t", 1) for file in loads.glob("*.tsv")
                for line in file.read_text(encoding="utf-8").splitlines()]
    if not observed or any(h != expected or Path(path).resolve() != loaded.resolve() for h, path in observed):
        raise RuntimeError(f"{label}: actual testhost loaded an unexpected assembly: {observed}")
    write_json(variant / "failures.json", sorted(failures.elements()))
    return {"testOutcomes": counts, "librarySha256": expected, "testhostLoadedSha256": expected,
            "verifiedLoadedPath": str(loaded), "trxSha256": digest(trx)}, outcomes, failures


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True, help="newtonsoft-hoosharper.json from native qualification")
    parser.add_argument("--fixed", type=Path, required=True, help="paired newtonsoft-fixed-* export directory")
    parser.add_argument("--dotnet", type=Path, required=True, help="repository .NET 10 SDK executable")
    parser.add_argument("--test-dotnet", type=Path, required=True, help="SDK host with .NET 6 runtime and repository SDK installed")
    parser.add_argument("--work-dir", type=Path, required=True, help="new or empty disposable directory")
    parser.add_argument("--source", type=Path, help="optional local Git clone at the same pinned upstream revision")
    args = parser.parse_args()
    args.dotnet, args.test_dotnet = args.dotnet.resolve(), args.test_dotnet.resolve()
    work = args.work_dir.resolve()
    work.mkdir(parents=True, exist_ok=True)
    if any(work.iterdir()):
        parser.error("--work-dir must be new or empty")
    report = json.loads(args.report.read_bytes())
    if not (report.get("passed") and report["properties"] == PROPERTIES and report["cumulativeRounds"][-1]["after"] == 0):
        parser.error("expected a passing, converged net6.0 Release report with native comparison settings")
    manifest = validate_export(args.fixed, report)
    src = work / "source"
    env = host_environment(args.dotnet)
    clone = (["--no-checkout", str(args.source.resolve())] if args.source else
             ["--depth", "1", "--branch", "13.0.3", REPOSITORY])
    capture(["git", "clone", *clone, str(src)], work / "clone.log", env)
    capture(["git", "-C", str(src), "checkout", "--detach", COMMIT], work / "checkout.log", env)
    actual_commit = subprocess.check_output(["git", "-C", str(src), "rev-parse", "HEAD"], text=True).strip()
    if actual_commit != COMMIT:
        raise RuntimeError(f"Expected pinned upstream {COMMIT}, got {actual_commit}")
    shutil.copyfile(ROOT / "global.json", src / "Src/global.json")
    hook_project = ROOT / "tests/smoke/NewtonsoftAssemblyProbe/NewtonsoftAssemblyProbe.csproj"
    bin_path = work / "probe-bin"
    capture([str(args.dotnet), "build", str(hook_project), "-c", "Release",
             "--artifacts-path", str(work / "probe-artifacts"), "--output", str(bin_path)],
            work / "build-probe.log", env, cwd=ROOT)
    hook = bin_path / "NewtonsoftAssemblyProbe.dll"
    before, original, baseline_failures = test_library("before", args, work, src, hook)
    changed = replay_export(src / LIBRARY, args.fixed, report)
    if not changed or any(digest(src / LIBRARY / name) != sha for name, sha in manifest.items()):
        raise RuntimeError("The applied source does not match the exact native export")
    write_json(work / "applied-files.json", {"changedFiles": changed, "rawInputSha256": report["sourceByteHashes"],
                                            "rawExportSha256": manifest})
    after, rewritten, after_failures = test_library("after", args, work, src, hook)
    if subprocess.check_output(["git", "-C", str(src), "diff", "--name-only", "--", str(SUITE.parent)], text=True):
        raise RuntimeError("An upstream test source changed")
    passed = original == rewritten and baseline_failures == after_failures and before["librarySha256"] != after["librarySha256"]
    summary = {"passed": passed, "sourceCommit": actual_commit, "nativeReportSha256": digest(args.report),
               "nativeExportManifestSha256": report["exportManifestSha256"], "nativeReport": str(args.report.resolve()),
               "nativeExport": str(args.fixed.resolve()), "properties": report["properties"], "analyzerSha256": report["analyzerSha256"],
               "fixerSha256": report["fixerSha256"], "exportedFilesVerifiedOnDisk": len(manifest), "changedSourceFiles": len(changed),
               "initialFindings": len(report["findings"]), "afterByRound": [r["after"] for r in report["cumulativeRounds"]],
               "allNamedOutcomesIdentical": original == rewritten, "failureMessagesIdentical": baseline_failures == after_failures,
               "baselineOnly": sorted((original - rewritten).elements()), "afterOnly": sorted((rewritten - original).elements()),
               "before": before, "after": after}
    write_json(work / "summary.json", summary)
    if not passed:
        raise RuntimeError(f"Upstream test outcomes, failures, or library identity check failed: {work / 'summary.json'}")
    print(f"Newtonsoft.Json: {len(changed)} files changed, all {EXPECTED_TESTS} outcomes matched; {after['testOutcomes']}")


if __name__ == "__main__":
    main()
