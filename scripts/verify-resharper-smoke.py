#!/usr/bin/env python3
"""Run pinned real-project smoke tests and ReSharper opportunity comparisons.

Requires the repository SDK plus `jb` from JetBrains.ReSharper.GlobalTools
2026.2.3.1. Sources are cloned under an owned work directory; only SDK policy
and comparison EditorConfig settings are adapted. Library source stays intact.
The .NET 10 SDK builds native net8.0/net6.0 targets. Run separately from ordinary
CI to avoid downloading and re-inspecting third-party projects on every PR.
"""

import argparse
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

from smoke_support import capture as capture_process, verify_runtime


ROOT = Path(__file__).resolve().parents[1]
PROJECTS = [
    ("dapper", "Dapper", "https://github.com/DapperLib/Dapper.git", "2.1.66",
     "bd4f75b512de3e00f2c2631d5309961a1ecfea23", "Dapper/Dapper.csproj",
     {"TargetFramework": "net8.0", "TargetFrameworks": "net8.0"}, "global.json"),
    ("newtonsoft", "Newtonsoft.Json", "https://github.com/JamesNK/Newtonsoft.Json.git", "13.0.3",
     "0a2e291c0d9c0c7675d445703e51750363a549ef", "Src/Newtonsoft.Json/Newtonsoft.Json.csproj",
     {"TargetFramework": "net6.0", "LibraryFrameworks": "net6.0",
      "_SkipUpgradeNetAnalyzersNuGetWarning": "true"}, "Src/global.json"),
    ("serilog", "Serilog", "https://github.com/serilog/serilog.git", "v4.0.0",
     "573a62874260d7a1708f620076b5469f69bc79b3", "src/Serilog/Serilog.csproj",
     {"TargetFramework": "net8.0", "TargetFrameworks": "net8.0"}, "global.json"),
]
SETTINGS = """[*.cs]
csharp_prefer_braces = false:suggestion
resharper_csharp_braces_for_ifelse = not_required
resharper_remove_redundant_braces_highlighting = hint
resharper_use_throw_if_null_method_highlighting = hint
max_line_length = 140
hoosharper_max_line_length = 140
"""


def capture(command, log, environment):
    capture_process(command, log, environment, cwd=ROOT)


def smoke(name, project, properties, work, args, environment, fixed=None):
    smoke = ROOT / "tools/HooSharper.SmokeTests/bin/Release/net10.0/HooSharper.SmokeTests.dll"
    hoo_report = work / f"{name}-hoosharper.json"
    command = [args.dotnet, str(smoke), str(project), str(hoo_report), "--fix-all"]
    if name == "matrix":
        command += ["--require-all-rules", "--write-fixed", str(fixed)]
    for key, value in properties.items():
        command += ["--property", f"{key}={value}"]
    capture(command, work / f"{name}-smoke.log", environment)
    return json.loads(hoo_report.read_text())


def inspect(name, project, properties, work, args, environment, fixed=None):
    smoke(name, project, properties, work, args, environment, fixed)
    hoo_report = work / f"{name}-hoosharper.json"
    reference_report = work / f"{name}-resharper.xml"
    command = [args.jb, "inspectcode", str(project), "--no-build", "--severity=HINT",
               "--format=Xml", f"--output={reference_report}", "--jobs=1", "--no-updates",
               "--no-buildin-settings", f"--dotnetcore={args.dotnet}"]
    if properties:
        command += ["--properties=" + ";".join(f"{key}={value}" for key, value in properties.items())]
    if "TargetFramework" in properties:
        command += ["--target-framework=" + properties["TargetFramework"]]
    capture(command, work / f"{name}-resharper.log", environment)
    spec = importlib.util.spec_from_file_location("comparison", ROOT / "scripts/compare-resharper.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    report = module.compare(hoo_report, reference_report)
    (work / f"{name}-parity.json").write_text(json.dumps(report, indent=2) + "\n")
    return report


def prepare_project(item, work, environment):
    name, directory, url, tag, commit, relative, properties, sdk_file = item
    checkout = work / "projects" / directory
    if not checkout.exists():
        capture(["git", "clone", "--depth", "1", "--branch", tag, url, str(checkout)],
                work / f"{name}-clone.log", environment)
    actual = subprocess.check_output(["git", "-C", str(checkout), "rev-parse", "HEAD"], text=True).strip()
    if actual != commit:
        raise RuntimeError(f"{checkout}: expected {commit}, found {actual}")
    remote = subprocess.check_output(["git", "-C", str(checkout), "remote", "get-url", "origin"], text=True).strip()
    if remote != url:
        raise RuntimeError(f"Unexpected source remote for {checkout}")
    if name == "dapper" and (checkout / ".git/shallow").exists():
        capture(["git", "-C", str(checkout), "fetch", "--unshallow", "origin"],
                work / "dapper-history.log", environment)
    project = checkout / relative
    # Refuse to silently qualify modified third-party library sources.
    changed = subprocess.check_output(["git", "-C", str(checkout), "diff", "--name-only", "HEAD", "--", "*.cs"], text=True)
    untracked = subprocess.check_output(["git", "-C", str(checkout), "ls-files", "--others",
                                         "--exclude-standard", "--", "*.cs"], text=True, timeout=30)
    if changed.strip() or untracked.strip():
        raise RuntimeError(f"Modified third-party C# source in {checkout}: {changed}{untracked}")
    policy = checkout / sdk_file
    backup = work / f"{name}-original-sdk.json"
    backup.write_bytes(subprocess.check_output(
        ["git", "-C", str(checkout), "show", f"{commit}:{sdk_file}"], timeout=30))
    policy.write_text((ROOT / "global.json").read_text())
    config = project.parent / ".editorconfig"
    marker = "# HooSharper ReSharper smoke comparison\n"
    original = config.read_text() if config.exists() else ""
    config.write_text(original.split(marker)[0].rstrip() + "\n\n" + marker + SETTINGS)
    properties = dict(properties, Configuration="Release", TreatWarningsAsErrors="false")
    command = [environment["HOOSHARPER_SMOKE_DOTNET"], "build", str(project), "-c", "Release", "-m:1"]
    command += [f"-p:{key}={value}" for key, value in properties.items()]
    capture(command, work / f"{name}-build.log", environment)
    return name, project, properties, {"repository": url, "commit": commit, "tag": tag}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default=shutil.which("dotnet"))
    parser.add_argument("--jb", default=shutil.which("jb"))
    parser.add_argument("--work-dir", type=Path, default=ROOT / "artifacts/resharper-smoke")
    args = parser.parse_args()
    if not args.dotnet or not args.jb:
        parser.error("--dotnet and --jb must identify installed tools")
    args.dotnet = str(Path(args.dotnet).resolve())
    args.jb = str(Path(args.jb).resolve())
    work = args.work_dir.resolve()
    work.mkdir(parents=True, exist_ok=True)
    (work / "summary.json").unlink(missing_ok=True)
    environment = dict(os.environ, DOTNET_PROCESSOR_COUNT="2", DOTNET_ROOT=str(Path(args.dotnet).parent),
                       HOOSHARPER_SMOKE_DOTNET=args.dotnet,
                       PATH=str(Path(args.dotnet).parent) + os.pathsep + os.environ["PATH"])
    capture([args.dotnet, "--version"], work / "sdk-version.log", environment)
    capture([args.jb, "inspectcode", "--version"], work / "resharper-version.log", environment)
    if "2026.2.3.1" not in (work / "resharper-version.log").read_text():
        raise RuntimeError("This comparison is pinned to ReSharper 2026.2.3.1")
    capture([args.dotnet, "build", str(ROOT / "tools/HooSharper.SmokeTests/HooSharper.SmokeTests.csproj"), "-c", "Release", "-m:1"],
            work / "smoke-tool-build.log", environment)
    matrix = ROOT / "tests/smoke/RuleMatrix/RuleMatrix.csproj"
    capture([args.dotnet, "build", str(matrix), "-c", "Release", "-m:1"], work / "matrix-build.log", environment)
    fixed = Path(tempfile.mkdtemp(prefix="matrix-fixed-", dir=work))
    reports = {"matrix": inspect("matrix", matrix, {}, work, args, environment, fixed)}
    provenance = {}
    runtime = verify_runtime(ROOT, work, fixed, args.dotnet, environment)
    portable = {}
    for item in PROJECTS:
        name, project, properties, source = prepare_project(item, work, environment)
        reports[name] = inspect(name, project, properties, work, args, environment)
        provenance[name] = source
        portable_properties = dict(properties, TargetFramework="netstandard2.0")
        if name == "newtonsoft":
            portable_properties["LibraryFrameworks"] = "netstandard2.0"
        else:
            portable_properties["TargetFrameworks"] = "netstandard2.0"
        capture([args.dotnet, "build", str(project), "-c", "Release", "-m:1"] +
                [f"-p:{key}={value}" for key, value in portable_properties.items()],
                work / f"{name}-netstandard-build.log", environment)
        portable_report = smoke(f"{name}-netstandard", project, portable_properties, work, args, environment)
        if any(finding["id"] == "HOO1009" for finding in portable_report["findings"]):
            raise RuntimeError(f"{name}: ThrowIfNull suggested against .NET Standard 2.0")
        portable[name] = {"framework": "netstandard2.0", "documents": portable_report["documents"],
                          "findings": len(portable_report["findings"]), "passed": portable_report["passed"],
                          "cumulativeRounds": portable_report["cumulativeRounds"]}
    summary = {"passed": True, "developmentSdk": (work / "sdk-version.log").read_text().strip(),
               "referenceVersion": "2026.2.3.1", "sources": provenance,
               "runtime": runtime, "portableTargets": portable, "projects": {name: {key: report[key] for key in
                   ["documents", "hooFindings", "nativeReferenceFindings", "referenceFindings"]}
                   for name, report in reports.items()}}
    (work / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
