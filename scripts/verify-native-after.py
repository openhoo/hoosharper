#!/usr/bin/env python3
"""Inspect the exact rewritten sources from a pinned before-run, in fresh clones."""

import argparse
from collections import Counter
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import time
import xml.etree.ElementTree as ET

from smoke_support import capture, replay_export

def load_script(name, script):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(script))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

runner = load_script("qualification", "verify-resharper-smoke.py")
comparison = load_script("comparison", "compare-resharper.py")


def warnings(report):
    root = ET.parse(report).getroot()
    types = {item.get("Id"): item for item in root.findall(".//IssueType")}
    counts = Counter()
    for item in root.findall(".//Issue"):
        rule = item.get("TypeId")
        description = types.get(rule)
        if description is None:
            raise RuntimeError(f"Unknown InspectCode inspection {rule} in {report}")
        severity = item.get("Severity") or description.get("Severity")
        if severity in ("WARNING", "ERROR"):
            origin = "external" if description.get("CategoryId") == "RoslynAnalyzers" else "native"
            counts[f"{origin}/{severity}/{rule}"] += 1
    return counts


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def validate_after(name, before, after, before_counts, after_counts):
    if not after["passed"] or after["compilerErrors"] or after["findings"]:
        raise RuntimeError(f"{name}: rewritten sources still have diagnostics or failed smoke validation")
    if (after_counts["toolsVersion"] != before_counts["toolsVersion"]
            or any(after[key] != before[key] for key in ("effectiveOptions", "references", "analyzerSha256", "fixerSha256"))):
        raise RuntimeError(f"{name}: tools, compilation references or analysis options differ from the baseline")
    added = Counter(after["compilerWarningCounts"]) - Counter(before["compilerWarningCounts"])
    if after["compilerWarnings"] > before["compilerWarnings"] or added:
        raise RuntimeError(f"{name}: Fix All introduced additional compiler warnings: {dict(added)}")


def inspect_rewrite(item, args, env):
    name = item[0]
    old, work = args.before, args.work_dir / name
    # A writer creates the parity JSON only after recording and validating the
    # original native capture. This lets independent baseline/replay jobs overlap.
    if args.wait_for_baseline:
        ready = old / f"{name}-parity.json"
        deadline = time.monotonic() + args.wait_for_baseline
        while not ready.is_file():
            if time.monotonic() >= deadline:
                raise RuntimeError(f"{name}: timed out waiting for baseline {ready}")
            time.sleep(1)
    work.mkdir()
    before_hoo = old / f"{name}-hoosharper.json"
    before_xml = old / f"{name}-resharper.xml"
    before = json.loads(before_hoo.read_text())
    before_counts = comparison.compare(before_hoo, before_xml)
    if not before["passed"] or before["compilerErrors"]:
        raise RuntimeError(f"{name}: the original smoke run failed")
    # A baseline directory may contain multiple exports from earlier runs.
    # Select only the one whose raw manifest belongs to the paired report.
    manifests = [path for path in old.glob(f"{name}-fixed-*/.hoosharper-export.json")
                 if sha(path) == before.get("exportManifestSha256")]
    if not manifests:
        raise RuntimeError(f"{name}: no export matches the captured manifest")
    fixed = sorted(manifests)[0].parent
    original = old / "projects" / item[1]
    project_path = original / item[5]
    if project_path.resolve() != Path(before["project"]).resolve():
        raise RuntimeError(f"{name}: project does not match the pinned baseline checkout")

    checkout = work / "projects" / item[1]
    checkout.parent.mkdir()
    capture(["git", "clone", "--local", str(original), str(checkout)], work / "clone.log", env)
    capture(["git", "-C", str(checkout), "remote", "set-url", "origin", item[2]], work / "origin.log", env)
    _, project, properties, source = runner.prepare_project(item, work, env)
    if properties != before["properties"]:
        raise RuntimeError(f"{name}: baseline and rewritten build properties differ")
    changed = replay_export(project.parent, fixed, before)
    runner.capture([args.dotnet, "build", str(project), "-c", "Release", "-m:1", "--no-restore",
                    *(f"-p:{key}={value}" for key, value in properties.items())], work / "rewritten-build.log", env)
    after_counts = runner.inspect("rewritten", project, properties, work, args, env)
    after_hoo = work / "rewritten-hoosharper.json"
    after_xml = work / "rewritten-resharper.xml"
    after = json.loads(after_hoo.read_text())
    validate_after(name, before, after, before_counts, after_counts)
    expected = json.loads(manifests[0].read_text())
    if any(sha(project.parent / key) != digest for key, digest in expected.items()):
        raise RuntimeError(f"{name}: inspected source bytes no longer match the export")
    ids = sorted({inspection for group in comparison.MAPPING.values() for inspection in group})
    mapped = {key: [before_counts["referenceCounts"].get(key, 0), after_counts["referenceCounts"].get(key, 0)] for key in ids}
    wc = [warnings(path) for path in (before_xml, after_xml)]
    info = {"name": name, "source": source, "properties": properties, "fixedExport": str(fixed),
            "exportManifestSha256": before["exportManifestSha256"], "changedFiles": len(changed), "localFiles": len(expected),
            "beforeXML": str(before_xml), "afterXML": str(after_xml), "beforeXmlSha": sha(before_xml), "afterXmlSha": sha(after_xml),
            "hoo": [len(before["findings"]), len(after["findings"])],
            "native": [before_counts["nativeReferenceFindings"], after_counts["nativeReferenceFindings"]],
            "mappedUnique": [sum(v[i] for v in mapped.values()) for i in (0, 1)], "inspections": mapped,
            "compilerWarnings": [doc["compilerWarnings"] for doc in (before, after)],
            "compilerWarningCodes": [doc["compilerWarningCounts"] for doc in (before, after)],
            "roslynCompilerErrors": [doc["compilerErrors"] for doc in (before, after)],
            "warningAndErrorCounts": {key: [wc[0][key], wc[1][key]] for key in sorted(wc[0].keys() | wc[1].keys())},
            "analyzer": after["analyzerSha256"], "fixer": after["fixerSha256"]}
    (work / "summary.json").write_text(json.dumps(info, indent=2) + "\n")
    print(f"{name}: native findings {info['native']}, distinct mapped inspection findings {info['mappedUnique']}", flush=True)
    return info


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--before", type=Path, required=True, help="original verify-resharper-smoke.py evidence directory")
    parser.add_argument("--work-dir", type=Path, required=True, help="new or empty output directory for rewritten clones")
    parser.add_argument("--dotnet", default=shutil.which("dotnet"))
    parser.add_argument("--jb", default=shutil.which("jb"))
    parser.add_argument("--project", action="append", choices=[item[0] for item in runner.PROJECTS])
    parser.add_argument("--wait-for-baseline", type=int, default=0, metavar="SECONDS",
                        help="wait up to this long for each project's original native capture")
    args = parser.parse_args()
    if not args.dotnet or not args.jb:
        parser.error("--dotnet and --jb must identify the same SDK and InspectCode used for baseline capture")
    if args.wait_for_baseline < 0:
        parser.error("--wait-for-baseline cannot be negative")
    args.dotnet, args.jb = str(Path(args.dotnet).resolve()), str(Path(args.jb).resolve())
    args.before, args.work_dir = args.before.resolve(), args.work_dir.resolve()
    if args.before == args.work_dir or args.work_dir.is_relative_to(args.before):
        parser.error("--work-dir must be outside the original evidence directory")
    args.work_dir.mkdir(parents=True, exist_ok=True)
    if any(args.work_dir.iterdir()):
        parser.error("--work-dir must be new or empty")
    sdk = str(Path(args.dotnet).parent)
    env = dict(os.environ, PATH=sdk + os.pathsep + os.environ["PATH"], DOTNET_ROOT=sdk,
               DOTNET_PROCESSOR_COUNT="2", HOOSHARPER_SMOKE_DOTNET=args.dotnet)
    for label, cmd in [("sdk-version", [args.dotnet, "--version"]), ("resharper-version", [args.jb, "inspectcode", "--version"])]:
        log = args.work_dir / f"{label}.log"
        runner.capture(cmd, log, env)
        reference = args.before / log.name
        if label == "sdk-version":
            same = log.read_text().strip() == reference.read_text().strip()
        else:
            same = all("Version: 2026.2.3.1" in path.read_text().splitlines() for path in (log, reference))
        if not same:
            raise RuntimeError(f"{label}: tool version does not match the baseline")
    projects = {item[0]: inspect_rewrite(item, args, env) for item in runner.PROJECTS
                if not args.project or item[0] in args.project}
    if len({(project["analyzer"], project["fixer"]) for project in projects.values()}) != 1:
        raise RuntimeError("The projects were not inspected with the same analyzer and fixer")
    totals = {k: [sum(project[k][i] for project in projects.values()) for i in (0, 1)]
              for k in ("hoo", "native", "mappedUnique", "compilerWarnings")}
    (args.work_dir / "summary.json").write_text(json.dumps({"projects": projects, "totals": totals}, indent=2) + "\n")


if __name__ == "__main__":
    main()
