"""Shared bounded process and fixed-source checks for smoke qualification."""

import codecs
import collections
import hashlib
import json
import os
import re
from pathlib import Path
import shutil
import signal
import subprocess


CSHARP_NEWLINE = re.compile(r"\r\n|[\r\n\u0085\u2028\u2029]")
NEWLINE_NAMES = {"\r\n": "CRLF", "\n": "LF", "\r": "CR", "\u0085": "NEL", "\u2028": "LS", "\u2029": "PS"}


def source_text(path):
    data = path.read_bytes()
    # Roslyn consumes the byte-order mark, including the UTF-16 sources still
    # common in older projects. Test UTF-32 first: the LE marks share a prefix.
    if data.startswith((codecs.BOM_UTF32_LE, codecs.BOM_UTF32_BE)):
        return data.decode("utf-32")
    if data.startswith((codecs.BOM_UTF16_LE, codecs.BOM_UTF16_BE)):
        return data.decode("utf-16")
    return data.decode("utf-8-sig")


def capture(command, log, environment, timeout=1200, cwd=None):
    print("Running", log.name, flush=True)
    with log.open("wb") as output:
        process = subprocess.Popen(command, cwd=cwd, env=environment, stdout=output,
                                   stderr=subprocess.STDOUT, start_new_session=os.name == "posix")
        try:
            code = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired as error:
            if os.name == "posix":
                try:
                    os.killpg(process.pid, signal.SIGTERM)
                except ProcessLookupError:
                    pass
            else:
                process.terminate()
            try:
                process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                pass
            if os.name == "posix":
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            elif process.poll() is None:
                process.kill()
            process.wait(timeout=5)
            raise RuntimeError(f"Command timed out after {timeout}s; see {log}") from error
    if code:
        raise RuntimeError(f"Command failed ({code}); see {log}")


def validate_export(fixed, report):
    encoded = (fixed / ".hoosharper-export.json").read_bytes()
    if hashlib.sha256(encoded).hexdigest() != report.get("exportManifestSha256"):
        raise RuntimeError("Export manifest is not paired with this smoke report")
    manifest = json.loads(encoded)
    expected = {name for name in report["sourceHashes"]
                if not Path(name).is_absolute() and ".." not in Path(name).parts
                and not {"obj", "bin"}.intersection(Path(name).parts)}
    if set(manifest) != expected or set(report.get("sourceByteHashes", ())) != expected:
        raise RuntimeError("Fixed export does not contain exactly the project's local source documents")
    actual = {path.relative_to(fixed).as_posix() for path in fixed.rglob("*") if path.is_file()}
    if actual != expected | {".hoosharper-export.json"}:
        raise RuntimeError("Fixed export contains missing or unexpected files")
    for name, digest in manifest.items():
        if hashlib.sha256((fixed / name).read_bytes()).hexdigest() != digest:
            raise RuntimeError(f"Fixed export hash mismatch: {name}")
    return manifest


def validate_export_newlines(fixed, report):
    """Refuse new line endings in files that used a single kind before Fix All."""
    manifest = validate_export(fixed, report)
    root = Path(report["project"]).parent
    counts = {"filesCompared": len(manifest), "consistentSources": 0, "mixedSources": 0, "sourcesWithoutNewlines": 0}
    errors = []
    for name in sorted(manifest):
        original = root / name
        if not original.is_file() or hashlib.sha256(original.read_bytes()).hexdigest() != report["sourceByteHashes"][name]:
            raise RuntimeError(f"Source no longer matches the exported input: {name}")
        before = set(CSHARP_NEWLINE.findall(source_text(original)))
        after = collections.Counter(CSHARP_NEWLINE.findall(source_text(fixed / name)))
        if len(before) == 1:
            counts["consistentSources"] += 1
            introduced = sorted(after.keys() - before)
            if introduced:
                details = ", ".join(f"{after[kind]} {NEWLINE_NAMES[kind]}" for kind in introduced)
                errors.append(f"{name}: previously {NEWLINE_NAMES[next(iter(before))]}, added {details}")
        elif before:
            counts["mixedSources"] += 1
        else:
            counts["sourcesWithoutNewlines"] += 1
    if errors:
        raise RuntimeError(f"Fix All introduced a new line ending in {len(errors)} file(s):\n" + "\n".join(errors))
    return counts


def replay_export(checkout, fixed, report):
    """Verify the original bytes for every input before copying any changes."""
    manifest = validate_export(fixed, report)
    changed = []
    for name, digest in manifest.items():
        original = report["sourceByteHashes"][name]
        path = checkout / name
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != original:
            raise RuntimeError(f"Source no longer matches the exported input: {name}")
        if digest != original:
            changed.append(name)
    for name in changed:
        path = checkout / name
        shutil.copyfile(fixed / name, path)
        if hashlib.sha256(path.read_bytes()).hexdigest() != manifest[name]:
            raise RuntimeError(f"Copied source differs from Fix All export: {name}")
    return sorted(changed)


def verify_runtime(root, work, fixed, dotnet, environment):
    project = root / "tests/smoke/RuleMatrix/RuleMatrix.csproj"
    report = json.loads((work / "matrix-hoosharper.json").read_text())
    line_endings = validate_export_newlines(fixed, report)
    shutil.copyfile(project, fixed / project.name)
    capture([dotnet, "build", str(fixed / project.name), "-c", "Release", "-m:1"],
            work / "matrix-fixed-build.log", environment, cwd=root)
    before_file, after_file = work / "matrix-before.txt", work / "matrix-after.txt"
    capture([dotnet, str(project.parent / "bin/Release/net10.0/RuleMatrix.dll")],
            before_file, environment, timeout=30, cwd=root)
    capture([dotnet, str(fixed / "bin/Release/net10.0/RuleMatrix.dll")],
            after_file, environment, timeout=30, cwd=root)
    before, after = before_file.read_bytes(), after_file.read_bytes()
    if before != after:
        raise RuntimeError("Executable matrix changed behavior after cumulative Fix All")
    if (fixed / "Program.cs").read_text().count("/* keep once */") != 1:
        raise RuntimeError("Fix All lost or duplicated the comment control")
    unsafe_ids = {"HOO1005", "HOO1006", "HOO1007", "HOO1009", "HOO1013", "HOO1015", "HOO1019"}
    if any(item["file"] == "SafetyControls.cs" and item["id"] in unsafe_ids for item in report["findings"]):
        raise RuntimeError("Unsafe negative control received a transformation diagnostic")
    return {"matches": True, "stdoutSha256": hashlib.sha256(before).hexdigest(), "output": before.decode(),
            "fixedDirectory": str(fixed), "exportLineEndings": line_endings}
