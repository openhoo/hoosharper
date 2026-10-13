"""Regression checks for report normalization and fixed-source provenance."""

import hashlib
import importlib.util
import json
import subprocess
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

from smoke_support import validate_export

spec = importlib.util.spec_from_file_location("comparison", Path(__file__).with_name("compare-resharper.py"))
comparison = importlib.util.module_from_spec(spec)
spec.loader.exec_module(comparison)
spec = importlib.util.spec_from_file_location("smoke_runner", Path(__file__).with_name("verify-resharper-smoke.py"))
smoke_runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke_runner)


class ComparisonTests(unittest.TestCase):
    def test_absolute_linked_source_matches_project_relative_source(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve() / "project"
            root.mkdir()
            linked = root.parent / "shared.cs"
            linked.write_text("// first\nclass Shared {}\n")
            issue = ET.Element("Issue", File=str(linked), Offset="9-21", Line="2")
            normalized = comparison.normalize_issue(issue, root)
            self.assertEqual("../shared.cs", normalized["file"])
            self.assertTrue(comparison.overlaps(normalized, {"file": "../shared.cs", "line": 2, "endLine": 2}))

    def test_utf16_offsets_follow_all_csharp_newline_forms(self):
        for newline in ["\n", "\r", "\r\n"]:
            with self.subTest(newline=newline), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                text = "// 😀" + newline + "class C {}" + newline
                (root / "C.cs").write_bytes(text.encode())
                start = len(("// 😀" + newline).encode("utf-16-le")) // 2
                issue = ET.Element("Issue", File="C.cs", Offset=f"{start}-{start + 10}", Line="1")
                normalized = comparison.normalize_issue(issue, root)
                self.assertEqual((2, 2), (normalized["line"], normalized["endLine"]))

    def test_native_filter_preserves_multiplicity_and_uses_inclusive_lines(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "C.cs").write_text("// first\n// second\nclass C\n{}\n")
            hoo = root / "hoo.json"
            hoo.write_text(json.dumps({"project": str(root / "P.csproj"), "passed": True, "documents": 1,
                                      "sourceHashes": {"C.cs": comparison.source_hash(root / "C.cs")},
                                      "inputHashes": {},
                                      "findings": [{"id": "HOO1007", "file": "C.cs", "line": 3, "endLine": 4}]}))
            report = root / "reference.xml"
            report.write_text('''<Report ToolsVersion="2026.2.3.1"><IssueTypes>
              <IssueType Id="CanSimplifyDictionaryLookupWithTryGetValue" CategoryId="CodeSmell" />
              <IssueType Id="HOO1007" CategoryId="RoslynAnalyzers" /></IssueTypes><Issues><Project Name="P">
              <Issue TypeId="CanSimplifyDictionaryLookupWithTryGetValue" File="C.cs" Line="4" />
              <Issue TypeId="CanSimplifyDictionaryLookupWithTryGetValue" File="C.cs" Line="4" />
              <Issue TypeId="HOO1007" File="C.cs" Line="4" /></Project></Issues></Report>''')
            comparison.record_native_capture(hoo, report)
            result = comparison.compare(hoo, report)
            row = next(row for row in result["rows"] if row["rule"] == "HOO1007")
            self.assertEqual(2, row["referenceCount"])
            self.assertEqual(1, row["hooWithReferenceOverlap"])
            self.assertEqual(1, result["roslynReferenceFindings"])
            self.assertEqual([], row["referenceOnly"])

    def test_comparison_rejects_edited_sources_inputs_and_unpaired_reports(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, config = root / "C.cs", root / ".editorconfig"
            source.write_text("class C {}\n")
            config.write_text("root = true\n")
            hoo, report = root / "hoo.json", root / "reference.xml"
            payload = {"project": str(root / "P.csproj"), "passed": True, "documents": 1,
                       "sourceHashes": {"C.cs": comparison.source_hash(source)},
                       "inputHashes": {".editorconfig": comparison.source_hash(config)}, "findings": []}
            hoo.write_text(json.dumps(payload))
            report.write_text('<Report><Issues><Project Name="P"><Issue TypeId="InvertIf" '
                              'File="C.cs" Offset="0-10" Line="1" /></Project></Issues></Report>')
            with self.assertRaisesRegex(RuntimeError, "missing capture provenance"):
                comparison.compare(hoo, report)
            comparison.record_native_capture(hoo, report)
            comparison.compare(hoo, report)
            for path in [source, config]:
                original = path.read_bytes()
                path.write_bytes(original + b"// changed\n")
                with self.assertRaisesRegex(RuntimeError, "hash mismatch"):
                    comparison.compare(hoo, report)
                path.write_bytes(original)
            original_report = report.read_bytes()
            report.write_bytes(original_report + b"\n")
            with self.assertRaisesRegex(RuntimeError, "paired reports"):
                comparison.compare(hoo, report)
            report.write_bytes(original_report)
            payload["project"] = str(root / "Other.csproj")
            hoo.write_text(json.dumps(payload))
            with self.assertRaisesRegex(RuntimeError, "paired reports"):
                comparison.compare(hoo, report)
            with self.assertRaisesRegex(RuntimeError, "project does not match"):
                comparison.record_native_capture(hoo, report)

    def test_checkout_inputs_reject_build_changes_and_only_accept_exact_adaptations(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            def git(*args):
                subprocess.run(["git", "-C", str(root), *args], check=True,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            git("init", "-q")
            originals = {"P.csproj": b"<Project />\n", "Directory.Build.props": b"<Project />\n",
                         "global.json": b'{"sdk": {}}\n', ".editorconfig": b"root = true\n",
                         ".gitignore": b"ignored.props\nobj/\n",
                         ".gitattributes": b"*.sln text eol=crlf\n", "P.sln": b"solution\n"}
            for name, content in originals.items():
                (root / name).write_bytes(content)
            git("add", ".")
            # Only disposable fixtures bypass the user's source signing setup.
            git("-c", "commit.gpgsign=false", "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                "commit", "-qm", "test: fixture")
            adaptations = {"global.json": (originals["global.json"], b'{"sdk": {"version": "10"}}\n'),
                           ".editorconfig": (originals[".editorconfig"], b"root = true\n# adapted\n")}
            smoke_runner.validate_checkout_inputs(root, adaptations)
            (root / "P.sln").write_bytes(b"solution\r\n")
            smoke_runner.validate_checkout_inputs(root, adaptations)
            (root / "P.sln").write_bytes(b"changed solution\r\n")
            with self.assertRaisesRegex(RuntimeError, "Modified third-party inputs"):
                smoke_runner.validate_checkout_inputs(root, adaptations)
            (root / "P.sln").write_bytes(originals["P.sln"])
            for name in ["P.csproj", "Directory.Build.props"]:
                (root / name).write_bytes(b"changed\n")
                with self.assertRaisesRegex(RuntimeError, "Modified third-party inputs"):
                    smoke_runner.validate_checkout_inputs(root, adaptations)
                (root / name).write_bytes(originals[name])
            git("update-index", "--assume-unchanged", "P.csproj")
            (root / "P.csproj").write_bytes(b"hidden change\n")
            with self.assertRaisesRegex(RuntimeError, "Modified third-party inputs"):
                smoke_runner.validate_checkout_inputs(root, adaptations)
            (root / "P.csproj").write_bytes(originals["P.csproj"])
            git("update-index", "--no-assume-unchanged", "P.csproj")
            (root / "Directory.Build.targets").write_bytes(b"new input\n")
            with self.assertRaisesRegex(RuntimeError, "Modified third-party inputs"):
                smoke_runner.validate_checkout_inputs(root, adaptations)
            git("add", "Directory.Build.targets")
            with self.assertRaisesRegex(RuntimeError, "Modified third-party inputs"):
                smoke_runner.validate_checkout_inputs(root, adaptations)
            git("reset", "--", "Directory.Build.targets")
            (root / "Directory.Build.targets").unlink()
            (root / "ignored.props").write_bytes(b"ignored build input\n")
            with self.assertRaisesRegex(RuntimeError, "Modified third-party inputs"):
                smoke_runner.validate_checkout_inputs(root, adaptations)
            (root / "ignored.props").unlink()
            (root / "obj").mkdir()
            (root / "obj/generated.cs").write_bytes(b"class Generated {}\n")
            smoke_runner.validate_checkout_inputs(root, adaptations)
            for name, accepted in adaptations.items():
                (root / name).write_bytes(accepted[1])
            smoke_runner.validate_checkout_inputs(root, adaptations)
            (root / ".editorconfig").write_bytes(b"root = true\n# unexpected\n")
            with self.assertRaisesRegex(RuntimeError, "adaptation input"):
                smoke_runner.validate_checkout_inputs(root, adaptations)

    def test_successful_commands_without_output_cannot_reuse_old_reports(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "C.cs"
            source.write_text("class C {}\n")
            project = root / "P.csproj"
            payload = {"project": str(project), "passed": True, "documents": 1,
                       "sourceHashes": {"C.cs": comparison.source_hash(source)},
                       "inputHashes": {}, "findings": []}
            hoo, report = root / "p-hoosharper.json", root / "p-resharper.xml"
            hoo.write_text(json.dumps(payload))
            report.write_text('<Report><Issues><Project Name="P"><Issue TypeId="InvertIf" '
                              'File="C.cs" Offset="0-10" Line="1" /></Project></Issues></Report>')
            args = SimpleNamespace(dotnet="unused", jb="unused")
            # A zero-exit tool which fails to produce output must fail closed.
            with patch.object(smoke_runner, "capture"):
                with self.assertRaisesRegex(RuntimeError, "Smoke command did not produce a fresh report"):
                    smoke_runner.smoke("p", project, {}, root, args, {})
                self.assertFalse(hoo.exists())
                hoo.write_text(json.dumps(payload))
                with patch.object(smoke_runner, "smoke"):
                    with self.assertRaisesRegex(RuntimeError, "Native inspection did not produce a fresh report"):
                        smoke_runner.inspect("p", project, {}, root, args, {})
            self.assertFalse(report.exists())
            self.assertFalse(comparison.capture_path(report).exists())

    def test_null_guard_mapping_tracks_native_transformation_outcome(self):
        self.assertEqual(["UseNullPropagation"], comparison.MAPPING["HOO1018"])
        self.assertIn("MergeConditionalExpression", comparison.MAPPING["HOO1014"])
        self.assertIn("MergeConditionalExpression", comparison.MAPPING["HOO1015"])

    def test_export_rejects_stale_missing_and_modified_sources(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "C.cs"
            source.write_text("class C {}")
            manifest = {"C.cs": hashlib.sha256(source.read_bytes()).hexdigest()}
            (root / ".hoosharper-export.json").write_text(json.dumps(manifest))
            report = {"sourceHashes": {"C.cs": "original", "../linked.cs": "external", "obj/generated.cs": "generated"}}
            validate_export(root, report)
            (root / "stale.cs").write_text("class Stale {}")
            with self.assertRaisesRegex(RuntimeError, "unexpected"):
                validate_export(root, report)
            (root / "stale.cs").unlink()
            source.write_text("class Changed {}")
            with self.assertRaisesRegex(RuntimeError, "hash mismatch"):
                validate_export(root, report)
            source.unlink()
            with self.assertRaisesRegex(RuntimeError, "missing"):
                validate_export(root, report)


if __name__ == "__main__":
    unittest.main()
