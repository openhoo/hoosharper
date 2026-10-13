"""Shared bounded process and fixed-source checks for smoke qualification."""

import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess


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
    manifest = json.loads((fixed / ".hoosharper-export.json").read_text())
    expected = {name for name in report["sourceHashes"]
                if not Path(name).is_absolute() and ".." not in Path(name).parts
                and not {"obj", "bin"}.intersection(Path(name).parts)}
    if set(manifest) != expected:
        raise RuntimeError("Fixed export does not contain exactly the project's local source documents")
    actual = {path.relative_to(fixed).as_posix() for path in fixed.rglob("*") if path.is_file()}
    if actual != expected | {".hoosharper-export.json"}:
        raise RuntimeError("Fixed export contains missing or unexpected files")
    for name, digest in manifest.items():
        if hashlib.sha256((fixed / name).read_bytes()).hexdigest() != digest:
            raise RuntimeError(f"Fixed export hash mismatch: {name}")


def verify_runtime(root, work, fixed, dotnet, environment):
    import shutil

    project = root / "tests/smoke/RuleMatrix/RuleMatrix.csproj"
    report = json.loads((work / "matrix-hoosharper.json").read_text())
    validate_export(fixed, report)
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
            "fixedDirectory": str(fixed)}
