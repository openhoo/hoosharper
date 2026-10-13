#!/usr/bin/env python3
"""Run all 20 analyzers/Fix Alls and compare executable behavior, without ReSharper."""

import argparse
import json
import os
from pathlib import Path
import shutil
import tempfile

from smoke_support import capture, verify_runtime

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default=shutil.which("dotnet"))
    parser.add_argument("--work-dir", type=Path, default=ROOT / "artifacts/matrix-smoke")
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args()
    if not args.dotnet:
        parser.error("--dotnet must identify an installed SDK")
    dotnet = str(Path(args.dotnet).resolve())
    work = args.work_dir.resolve()
    work.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ, DOTNET_PROCESSOR_COUNT="2", DOTNET_ROOT=str(Path(dotnet).parent),
                       PATH=str(Path(dotnet).parent) + os.pathsep + os.environ["PATH"])
    tool = ROOT / "tools/HooSharper.SmokeTests/HooSharper.SmokeTests.csproj"
    matrix = ROOT / "tests/smoke/RuleMatrix/RuleMatrix.csproj"
    if not args.no_build:
        for name, project in [("smoke-tool", tool), ("matrix", matrix)]:
            capture([dotnet, "build", str(project), "-c", "Release", "-m:1"],
                    work / f"{name}-build.log", environment, cwd=ROOT)
    fixed = Path(tempfile.mkdtemp(prefix="matrix-fixed-", dir=work))
    capture([dotnet, str(tool.parent / "bin/Release/net10.0/HooSharper.SmokeTests.dll"),
             str(matrix), str(work / "matrix-hoosharper.json"), "--fix-all", "--require-all-rules",
             "--write-fixed", str(fixed)], work / "matrix-smoke.log", environment, cwd=ROOT)
    runtime = verify_runtime(ROOT, work, fixed, dotnet, environment)
    (work / "matrix-runtime.json").write_text(json.dumps(runtime, indent=2) + "\n")
    print(json.dumps(runtime, indent=2))


if __name__ == "__main__":
    main()
