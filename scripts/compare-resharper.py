#!/usr/bin/env python3
"""Compare captured HooSharper and native ReSharper findings without hiding gaps.

Usage: compare-resharper.py HOO.json RESHARPER.xml OUTPUT.json
Matching uses project-relative files and overlapping inclusive line ranges.
This is an opportunity comparison, not a semantic-equivalence oracle. Keep the
original reports: diagnostic spans and inspections are not one-to-one.
"""

import argparse
import collections
import hashlib
import json
import os
import re
from pathlib import Path
import xml.etree.ElementTree as ET


# Only IDs verified in ReSharper's native 2026.2.3.1 inspection catalog.
# Some HooSharper transformations are ReSharper context actions or formatting
# operations rather than matching InspectCode inspections.
MAPPING = {
    "HOO1001": ["InvertIf"],
    "HOO1002": ["RemoveRedundantBraces"],
    "HOO1003": ["RedundantIfElseBlock"],
    "HOO1004": ["InvertIf"],
    "HOO1005": ["UsePatternMatching", "UseNegatedPatternMatching"],
    "HOO1006": ["RedundantBoolCompare"],
    "HOO1007": ["CanSimplifyDictionaryLookupWithTryGetValue"],
    "HOO1008": ["ConvertIfStatementToNullCoalescingAssignment"],
    "HOO1009": ["UseThrowIfNullMethod"],
    "HOO1010": [],
    "HOO1011": ["CanSimplifyDictionaryLookupWithTryAdd"],
    "HOO1012": ["CanSimplifySetAddingWithSingleCall"],
    "HOO1013": ["ConvertToUsingDeclaration"],
    "HOO1014": ["ReplaceConditionalExpressionWithNullCoalescing", "MergeConditionalExpression"],
    "HOO1015": ["MergeConditionalExpression"],
    "HOO1016": [],
    "HOO1017": ["ConvertIfStatementToReturnStatement"],
    "HOO1018": ["UseNullPropagation"],
    "HOO1019": ["UseNegatedPatternInIsExpression"],
    "HOO1020": [],
}


def utf16_source(source):
    return source.read_bytes().decode("utf-8-sig").encode("utf-16-le")


def relative_source(file, root):
    file = file.replace("\\", "/")
    if Path(file).is_absolute():
        return Path(os.path.relpath(Path(file).resolve(), root.resolve())).as_posix()
    return Path(file).as_posix()


def source_hash(path):
    # Roslyn SourceText excludes an encoding preamble, while retaining newlines.
    return hashlib.sha256(path.read_bytes().decode("utf-8-sig").encode("utf-8")).hexdigest()


def verify_hashes(root, hashes):
    for name, expected in hashes.items():
        path = root / name
        if not path.is_file() or source_hash(path) != expected:
            raise RuntimeError(f"Captured source/input hash mismatch: {name}")


def validate_hoo_inputs(hoo):
    if "sourceHashes" not in hoo or "inputHashes" not in hoo:
        raise RuntimeError("HooSharper report is missing captured input hashes")
    root = Path(hoo["project"]).parent
    verify_hashes(root, hoo["sourceHashes"])
    verify_hashes(root, hoo["inputHashes"])


def capture_path(resharper_path):
    return resharper_path.with_suffix(resharper_path.suffix + ".capture.json")


def validate_native_project(hoo, reference):
    names = {item.get("Name") for item in reference.findall(".//Project")}
    if reference.findall(".//Issue") and Path(hoo["project"]).stem not in names:
        raise RuntimeError("Native report project does not match the HooSharper project")


def record_native_capture(hoo_path, resharper_path):
    """Bind a fresh native report to the checked Hoo inputs and source text."""
    hoo = json.loads(hoo_path.read_text())
    validate_hoo_inputs(hoo)
    root = Path(hoo["project"]).parent
    reference = ET.parse(resharper_path).getroot()
    validate_native_project(hoo, reference)
    names = {relative_source(item.get("File", ""), root)
             for item in reference.findall(".//Issue") if item.get("File")}
    hashes = {name: source_hash(root / name) for name in sorted(names)}
    capture = {"project": str(Path(hoo["project"]).resolve()),
               "hooReportSha256": hashlib.sha256(hoo_path.read_bytes()).hexdigest(),
               "referenceReportSha256": hashlib.sha256(resharper_path.read_bytes()).hexdigest(),
               "nativeSourceHashes": hashes}
    capture_path(resharper_path).write_text(json.dumps(capture, indent=2) + "\n")


def normalize_issue(issue, root, source_texts=None):
    """Keep offsets/multiplicity; ReSharper offsets count UTF-16 code units."""
    item = dict(issue.attrib)
    item["file"] = relative_source(item.get("File", ""), root)
    item["line"] = int(item.get("Line", "0"))
    item["endLine"] = item["line"]
    source = root / item["file"]
    if source.is_file() and "Offset" in item:
        start, end = map(int, item["Offset"].split("-"))
        text = source_texts[item["file"]] if source_texts is not None else utf16_source(source)
        item["line"] = len(re.findall(r"\r\n|\r|\n", text[: start * 2].decode("utf-16-le", "replace"))) + 1
        item["endLine"] = len(re.findall(r"\r\n|\r|\n", text[: max(start, end - 1) * 2].decode("utf-16-le", "replace"))) + 1
    return item


def overlaps(first, second):
    return first["file"] == second["file"] and max(first["line"], second["line"]) <= min(
        first["endLine"], second["endLine"]
    )


def compare(hoo_path, resharper_path):
    hoo = json.loads(hoo_path.read_text())
    validate_hoo_inputs(hoo)
    root = Path(hoo["project"]).parent
    sidecar = capture_path(resharper_path)
    if not sidecar.is_file():
        raise RuntimeError("Native report is missing capture provenance")
    capture = json.loads(sidecar.read_text())
    if (capture["project"] != str(Path(hoo["project"]).resolve()) or
            capture["hooReportSha256"] != hashlib.sha256(hoo_path.read_bytes()).hexdigest() or
            capture["referenceReportSha256"] != hashlib.sha256(resharper_path.read_bytes()).hexdigest()):
        raise RuntimeError("Native capture does not match the project and paired reports")
    verify_hashes(root, capture["nativeSourceHashes"])
    source_texts = {name: utf16_source(root / name) for name in capture["nativeSourceHashes"]}
    reference = ET.parse(resharper_path).getroot()
    validate_native_project(hoo, reference)
    types = {item.get("Id"): dict(item.attrib) for item in reference.findall(".//IssueType")}
    issues = [normalize_issue(item, root, source_texts) for item in reference.findall(".//Issue")]
    native = [item for item in issues if types.get(item.get("TypeId"), {}).get("CategoryId") != "RoslynAnalyzers"]
    rows = []
    for rule, reference_ids in MAPPING.items():
        left = [item for item in hoo["findings"] if item["id"] == rule]
        right = [item for item in native if item["TypeId"] in reference_ids]
        left_only = [item for item in left if not any(overlaps(item, other) for other in right)]
        right_only = [item for item in right if not any(overlaps(item, other) for other in left)]
        rows.append({
            "rule": rule, "referenceIds": reference_ids,
            "hooCount": len(left), "referenceCount": len(right),
            "hooWithReferenceOverlap": len(left) - len(left_only),
            "hooOnly": left_only, "referenceOnly": right_only,
            "comparisonAvailable": bool(reference_ids),
            "sharedReferenceIds": [reference_id for reference_id in reference_ids
                                   if sum(reference_id in ids for ids in MAPPING.values()) > 1],
        })
    return {
        "hooReport": str(hoo_path), "referenceReport": str(resharper_path),
        "toolsVersion": reference.get("ToolsVersion"),
        "hooPassed": hoo["passed"], "documents": hoo["documents"],
        "hooFindings": len(hoo["findings"]), "referenceFindings": len(issues),
        "nativeReferenceFindings": len(native), "roslynReferenceFindings": len(issues) - len(native),
        "referenceCounts": dict(sorted(collections.Counter(item["TypeId"] for item in native).items())),
        "scope": "Mapped inspection opportunities; unmatched findings require source review, not automatic parity failures.",
        "rows": rows,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("hoo", type=Path)
    parser.add_argument("resharper", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    report = compare(args.hoo, args.resharper)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    print(f"{report['documents']} documents: {report['hooFindings']} HooSharper findings, "
          f"{report['nativeReferenceFindings']} native ReSharper findings")
    for row in report["rows"]:
        if row["hooCount"] or row["referenceCount"]:
            print(f"{row['rule']}: HOO={row['hooCount']} R#={row['referenceCount']} "
                  f"HOO overlap={row['hooWithReferenceOverlap']} R# only={len(row['referenceOnly'])}")
    return 0 if report["hooPassed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
