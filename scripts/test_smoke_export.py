"""Exercise the real Roslyn exporter with changed and untouched mixed-encoding source."""

import codecs
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

from smoke_support import replay_export, validate_export

ROOT = Path(__file__).resolve().parents[1]
TOOL = ROOT / "tools/HooSharper.SmokeTests/bin/Release/net10.0/HooSharper.SmokeTests.dll"


class ExportTests(unittest.TestCase):
    def test_fix_all_preserves_encoding_and_replays_only_actual_changes(self):
        dotnet = shutil.which("dotnet")
        if not dotnet or not TOOL.is_file():
            self.skipTest("requires the repository SDK and the Release smoke tool")
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            project = work / "input/Probe.csproj"
            project.parent.mkdir()
            project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                               '<TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
            (project.parent / ".editorconfig").write_text("root = true\n[*.cs]\ndotnet_diagnostic.HOO1006.severity = warning\n")
            original, expected, normalized = {}, {}, {}
            for ordinal, (encoding, bom) in enumerate([
                    ("utf-8", b""), ("utf-8", codecs.BOM_UTF8),
                    ("utf-16-le", codecs.BOM_UTF16_LE), ("utf-16-be", codecs.BOM_UTF16_BE)]):
                for kind, expression in [("Fixed", "b == true"), ("Untouched", "b")]:
                    name = f"{kind}{ordinal}"
                    before = f"// café 😀\r\npublic class {name} {{ public bool F(bool b) => {expression}; }}\r\n"
                    after = before.replace("b == true", "b")
                    # A valid local filename can start with two dots without being a parent path.
                    path = (".." if name == "Untouched3" else "") + name + ".cs"
                    normalized[path] = hashlib.sha256(before.encode("utf-8")).hexdigest()
                    original[path] = bom + before.encode(encoding)
                    expected[path] = bom + after.encode(encoding)
                    (project.parent / path).write_bytes(original[path])
            env = dict(os.environ, DOTNET_ROOT=str(Path(dotnet).resolve().parent), DOTNET_PROCESSOR_COUNT="2")
            def run(*command):
                result = subprocess.run([dotnet, *command], cwd=ROOT, env=env, text=True,
                                        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=120)
                self.assertEqual(0, result.returncode, result.stdout)
            run("build", str(project), "-c", "Release")
            fixed = work / "fixed"
            output = work / "report.json"
            run(str(TOOL), str(project), str(output), "--fix-all", "--write-fixed", str(fixed))
            report = json.loads(output.read_text())
            self.assertEqual(4, len(report["findings"]))
            self.assertEqual(normalized, {p: report["sourceHashes"][p] for p in original})
            for path, content in expected.items():
                self.assertEqual(content, (fixed / path).read_bytes(), path)
            validate_export(fixed, report)
            self.assertEqual({p: hashlib.sha256(data).hexdigest() for p, data in original.items()},
                             report["sourceByteHashes"])
            # Changing only a BOM after capture must fail before any rewrite is copied.
            stale = project.parent / "Untouched1.cs"
            stale.write_bytes(original[stale.name][len(codecs.BOM_UTF8):])
            with self.assertRaisesRegex(RuntimeError, "exported input: Untouched1.cs"):
                replay_export(project.parent, fixed, report)
            self.assertEqual(original["Fixed0.cs"], (project.parent / "Fixed0.cs").read_bytes())
            stale.write_bytes(original[stale.name])
            with self.assertRaisesRegex(RuntimeError, "not paired"):
                validate_export(fixed, dict(report, exportManifestSha256="old-run"))
            unchanged = [p for p in original if original[p] == expected[p]]
            times = {p: (project.parent / p).stat().st_mtime_ns for p in unchanged}
            self.assertEqual(sorted(set(original) - set(unchanged)), replay_export(project.parent, fixed, report))
            self.assertEqual(expected, {p: (project.parent / p).read_bytes() for p in expected})
            self.assertEqual(times, {p: (project.parent / p).stat().st_mtime_ns for p in unchanged})
            run("build", str(project), "-c", "Release", "--no-restore")
