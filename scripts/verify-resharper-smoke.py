#!/usr/bin/env python3
"""Run pinned real-project smoke tests and ReSharper opportunity comparisons.

Requires the repository SDK plus `jb` from JetBrains.ReSharper.GlobalTools
2026.2.3.1. Sources are cloned under an owned work directory; only SDK policy
and comparison EditorConfig settings are adapted. Library source stays intact.
The .NET 10 SDK builds native net8.0/net6.0 targets. Run separately from ordinary
CI to avoid downloading and re-inspecting third-party projects on every PR.
"""

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import stat
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
    hoo_report.unlink(missing_ok=True)
    command = [args.dotnet, str(smoke), str(project), str(hoo_report), "--fix-all"]
    if name == "matrix":
        command += ["--require-all-rules", "--write-fixed", str(fixed)]
    for key, value in properties.items():
        command += ["--property", f"{key}={value}"]
    capture(command, work / f"{name}-smoke.log", environment)
    if not hoo_report.is_file():
        raise RuntimeError(f"Smoke command did not produce a fresh report: {hoo_report}")
    return json.loads(hoo_report.read_text())


def inspect(name, project, properties, work, args, environment, fixed=None):
    smoke(name, project, properties, work, args, environment, fixed)
    hoo_report = work / f"{name}-hoosharper.json"
    reference_report = work / f"{name}-resharper.xml"
    reference_report.unlink(missing_ok=True)
    (work / f"{name}-parity.json").unlink(missing_ok=True)
    spec = importlib.util.spec_from_file_location("comparison", ROOT / "scripts/compare-resharper.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.capture_path(reference_report).unlink(missing_ok=True)
    module.validate_hoo_inputs(json.loads(hoo_report.read_text()))
    command = [args.jb, "inspectcode", str(project), "--no-build", "--severity=HINT",
               "--format=Xml", f"--output={reference_report}", "--jobs=1", "--no-updates",
               "--no-buildin-settings", f"--dotnetcore={args.dotnet}"]
    if properties:
        command += ["--properties=" + ";".join(f"{key}={value}" for key, value in properties.items())]
    if "TargetFramework" in properties:
        command += ["--target-framework=" + properties["TargetFramework"]]
    capture(command, work / f"{name}-resharper.log", environment)
    if not reference_report.is_file():
        raise RuntimeError(f"Native inspection did not produce a fresh report: {reference_report}")
    module.record_native_capture(hoo_report, reference_report)
    report = module.compare(hoo_report, reference_report)
    (work / f"{name}-parity.json").write_text(json.dumps(report, indent=2) + "\n")
    return report


def validate_checkout_inputs(checkout, adaptations, commit="HEAD"):
    """Accept only pristine inputs or exact, reproducible runner adaptations."""
    # Read the pinned tree and actual bytes rather than trusting index flags such
    # as assume-unchanged/skip-worktree, which hide edits from git diff/status.
    tree = subprocess.check_output(
        ["git", "-C", str(checkout), "ls-tree", "-r", "-z", commit], timeout=30)
    algorithm = subprocess.check_output(
        ["git", "-C", str(checkout), "rev-parse", "--show-object-format"], text=True, timeout=30).strip()
    tracked = set()
    for entry in tree.split(b"\0"):
        if not entry:
            continue
        metadata, raw_name = entry.split(b"\t", 1)
        mode, kind, expected = metadata.decode().split()
        name = os.fsdecode(raw_name)
        tracked.add(name)
        path = checkout / name
        if kind != "blob" or not path.exists() and not path.is_symlink():
            raise RuntimeError(f"Missing or unsupported pinned input: {path}")
        attributes = path.lstat()
        if mode == "120000" and stat.S_ISLNK(attributes.st_mode):
            content = os.fsencode(os.readlink(path))
        elif mode in {"100644", "100755"} and stat.S_ISREG(attributes.st_mode):
            executable = bool(attributes.st_mode & stat.S_IXUSR)
            if executable != (mode == "100755"):
                raise RuntimeError(f"Modified third-party input mode: {path}")
            content = path.read_bytes()
        else:
            raise RuntimeError(f"Modified third-party input type: {path}")
        digest = hashlib.new(algorithm, b"blob " + str(len(content)).encode() + b"\0" + content).hexdigest()
        if name not in adaptations and digest != expected:
            # A committed eol=crlf attribute expands LF blobs on checkout. Honor
            # that specific transformation without trusting index clean flags.
            attribute = subprocess.check_output(
                ["git", "-C", str(checkout), "check-attr", f"--source={commit}", "-z", "eol", "--", name],
                timeout=30).split(b"\0")
            normalized = content.replace(b"\r\n", b"\n")
            normalized_digest = hashlib.new(
                algorithm, b"blob " + str(len(normalized)).encode() + b"\0" + normalized).hexdigest()
            if attribute[2] != b"crlf" or normalized_digest != expected:
                raise RuntimeError(f"Modified third-party inputs in {checkout}: {name}")
    # Include newly staged and ignored files too. Only conventional generated
    # bin/obj trees are exempt; ignored source or build inputs still invalidate
    # the pinned checkout.
    actual_paths = subprocess.check_output(
        ["git", "-C", str(checkout), "ls-files", "--cached", "--others", "-z"], timeout=30)
    actual_paths = {os.fsdecode(name) for name in actual_paths.split(b"\0") if name}
    extra = actual_paths - tracked - set(adaptations)
    unexpected = sorted(name for name in extra if not {"bin", "obj"}.intersection(Path(name).parts))
    if unexpected:
        raise RuntimeError(f"Modified third-party inputs in {checkout}: {unexpected}")
    for name, accepted in adaptations.items():
        path = checkout / name
        if path.is_symlink():
            raise RuntimeError(f"Modified third-party adaptation input type: {path}")
        actual = path.read_bytes() if path.is_file() else None
        if actual not in accepted:
            raise RuntimeError(f"Modified third-party adaptation input: {path}")


def pinned_file(checkout, commit, relative, optional=False):
    result = subprocess.run(["git", "-C", str(checkout), "show", f"{commit}:{relative}"],
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=30, check=False)
    if result.returncode:
        if optional:
            return None
        raise RuntimeError(f"Missing pinned input: {relative}")
    return result.stdout


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
    policy = checkout / sdk_file
    backup = work / f"{name}-original-sdk.json"
    config = project.parent / ".editorconfig"
    config_name = config.relative_to(checkout).as_posix()
    marker = "# HooSharper ReSharper smoke comparison\n"
    original_sdk = pinned_file(checkout, commit, sdk_file)
    original_config = pinned_file(checkout, commit, config_name, optional=True)
    adapted_sdk = (ROOT / "global.json").read_bytes()
    adapted_config = ((original_config or b"").decode("utf-8").rstrip() + "\n\n" + marker + SETTINGS).encode("utf-8")
    validate_checkout_inputs(checkout, {sdk_file: (original_sdk, adapted_sdk),
                                        config_name: (original_config, adapted_config)}, commit)
    backup.write_bytes(original_sdk)
    policy.write_bytes(adapted_sdk)
    config.write_bytes(adapted_config)
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
