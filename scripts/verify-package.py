#!/usr/bin/env python3
"""Verify HooSharper's consumer package contract before upload or publication."""

import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from zipfile import ZipFile


def verify(package: Path) -> None:
    expected_version = (Path(__file__).resolve().parents[1] / "version").read_text().strip()
    required = {
        "analyzers/dotnet/cs/HooSharper.Analyzers.dll",
        "analyzers/dotnet/cs/HooSharper.CodeFixes.dll",
        "README.md",
        "LICENSE",
        "hoosharper-logo.png",
        "HooSharper.Analyzers.nuspec",
    }
    with ZipFile(package) as archive:
        names = set(archive.namelist())
        missing = required - names
        if missing:
            raise ValueError(f"Missing package files: {', '.join(sorted(missing))}")
        for name in required:
            if archive.getinfo(name).file_size == 0:
                raise ValueError(f"Empty package file: {name}")
        if any(name.startswith(("lib/", "runtimes/")) for name in names):
            raise ValueError("Analyzer package unexpectedly contains runtime assets")

        root = ET.fromstring(archive.read("HooSharper.Analyzers.nuspec"))
        namespace = {"n": root.tag.partition("}")[0].removeprefix("{")}
        metadata = root.find("n:metadata", namespace)
        if metadata is None:
            raise ValueError("NuGet package metadata is missing")
        if metadata.findtext("n:id", namespaces=namespace) != "HooSharper.Analyzers":
            raise ValueError("Unexpected package ID")
        if metadata.findtext("n:version", namespaces=namespace) != expected_version:
            raise ValueError(f"Package version does not match {expected_version}")
        if metadata.findall(".//n:dependency", namespace):
            raise ValueError("Analyzer package unexpectedly exposes runtime dependencies")
    print(f"Verified {package.name}: version {expected_version}, analyzer and code fixes, documentation, license, icon")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: python3 scripts/verify-package.py <package.nupkg>")
    verify(Path(sys.argv[1]))
