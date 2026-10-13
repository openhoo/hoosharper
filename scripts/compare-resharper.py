#!/usr/bin/env python3
"""Compare captured HooSharper and native ReSharper findings without hiding gaps.

Usage: compare-resharper.py HOO.json RESHARPER.xml OUTPUT.json
Matching uses project-relative files and overlapping inclusive line ranges.
This is an opportunity comparison, not a semantic-equivalence oracle. Keep the
original reports: diagnostic spans and inspections are not one-to-one.
"""

import argparse
import collections
import json
import os
import re
from functools import lru_cache
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


@lru_cache(maxsize=512)
def utf16_source(source):
    return source.read_bytes().decode("utf-8-sig").encode("utf-16-le")


def normalize_issue(issue, root):
    """Keep offsets/multiplicity; ReSharper offsets count UTF-16 code units."""
    item = dict(issue.attrib)
    item["file"] = item.get("File", "").replace("\\", "/")
    if Path(item["file"]).is_absolute():
        item["file"] = Path(os.path.relpath(Path(item["file"]).resolve(), root.resolve())).as_posix()
    item["line"] = int(item.get("Line", "0"))
    item["endLine"] = item["line"]
    source = root / item["file"]
    if source.is_file() and "Offset" in item:
        start, end = map(int, item["Offset"].split("-"))
        text = utf16_source(source)
        item["line"] = len(re.findall(r"\r\n|\r|\n", text[: start * 2].decode("utf-16-le", "replace"))) + 1
        item["endLine"] = len(re.findall(r"\r\n|\r|\n", text[: max(start, end - 1) * 2].decode("utf-16-le", "replace"))) + 1
    return item


def overlaps(first, second):
    return first["file"] == second["file"] and max(first["line"], second["line"]) <= min(
        first["endLine"], second["endLine"]
    )


def compare(hoo_path, resharper_path):
    hoo = json.loads(hoo_path.read_text())
    root = Path(hoo["project"]).parent
    reference = ET.parse(resharper_path).getroot()
    types = {item.get("Id"): dict(item.attrib) for item in reference.findall(".//IssueType")}
    issues = [normalize_issue(item, root) for item in reference.findall(".//Issue")]
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
