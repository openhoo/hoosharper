"""Regression checks for report normalization and fixed-source provenance."""

import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

from smoke_support import validate_export

spec = importlib.util.spec_from_file_location("comparison", Path(__file__).with_name("compare-resharper.py"))
comparison = importlib.util.module_from_spec(spec)
spec.loader.exec_module(comparison)


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
            hoo = root / "hoo.json"
            hoo.write_text(json.dumps({"project": str(root / "P.csproj"), "passed": True, "documents": 1,
                                      "findings": [{"id": "HOO1007", "file": "C.cs", "line": 3, "endLine": 4}]}))
            report = root / "reference.xml"
            report.write_text('''<Report ToolsVersion="2026.2.3.1"><IssueTypes>
              <IssueType Id="CanSimplifyDictionaryLookupWithTryGetValue" CategoryId="CodeSmell" />
              <IssueType Id="HOO1007" CategoryId="RoslynAnalyzers" /></IssueTypes><Issues>
              <Issue TypeId="CanSimplifyDictionaryLookupWithTryGetValue" File="C.cs" Line="4" />
              <Issue TypeId="CanSimplifyDictionaryLookupWithTryGetValue" File="C.cs" Line="4" />
              <Issue TypeId="HOO1007" File="C.cs" Line="4" /></Issues></Report>''')
            result = comparison.compare(hoo, report)
            row = next(row for row in result["rows"] if row["rule"] == "HOO1007")
            self.assertEqual(2, row["referenceCount"])
            self.assertEqual(1, row["hooWithReferenceOverlap"])
            self.assertEqual(1, result["roslynReferenceFindings"])
            self.assertEqual([], row["referenceOnly"])

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
