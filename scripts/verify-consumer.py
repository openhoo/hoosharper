#!/usr/bin/env python3
"""Prove that a packed analyzer loads in a real SDK compiler host."""

import argparse
import json
import subprocess
import tempfile
from pathlib import Path
from xml.sax.saxutils import escape


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--framework", default="net10.0")
    parser.add_argument("--sdk-version")
    args = parser.parse_args()
    package = args.package.resolve()
    if not package.is_file():
        parser.error(f"Package does not exist: {package}")
    version = (Path(__file__).resolve().parents[1] / "version").read_text().strip()

    with tempfile.TemporaryDirectory(prefix="hoosharper-consumer-") as directory:
        root = Path(directory)
        sdk_policy = json.loads(
            (Path(__file__).resolve().parents[1] / "global.json").read_text()
        )["sdk"]
        if args.sdk_version:
            sdk_policy = {"version": args.sdk_version, "rollForward": "disable"}
        # setup-dotnet respects the repository's latestPatch policy and may
        # install a later patch than its requested minimum version.
        (root / "global.json").write_text(json.dumps({"sdk": sdk_policy}))
        # Isolate the package cache so an older published package with the same
        # version cannot hide a regression in the locally packed assemblies.
        (root / "Consumer.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            f'<TargetFramework>{escape(args.framework)}</TargetFramework>'
            '<RestorePackagesPath>packages</RestorePackagesPath>'
            '<TreatWarningsAsErrors>true</TreatWarningsAsErrors>'
            '</PropertyGroup><ItemGroup>'
            f'<PackageReference Include="HooSharper.Analyzers" Version="{escape(version)}" PrivateAssets="all" />'
            '</ItemGroup></Project>'
        )
        (root / "NuGet.config").write_text(
            '<configuration><packageSources><clear />'
            f'<add key="local" value="{escape(str(package.parent), {chr(34): "&quot;"})}" />'
            '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
            '</packageSources><packageSourceMapping>'
            '<packageSource key="local"><package pattern="HooSharper.Analyzers" /></packageSource>'
            '<packageSource key="nuget.org"><package pattern="Microsoft.*" /></packageSource>'
            '</packageSourceMapping></configuration>'
        )
        (root / ".editorconfig").write_text(
            'root = true\n\n[*.cs]\ndotnet_diagnostic.HOO1006.severity = error\n'
        )

        def build() -> subprocess.CompletedProcess[str]:
            result = subprocess.run(
                [args.dotnet, "build", "Consumer.csproj", "-c", "Release", "--nologo"],
                cwd=root, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                check=False,
            )
            print(result.stdout, end="")
            return result

        source = root / "Example.cs"
        source.write_text('public class Example { public bool Run(bool ready) => ready == true; }\n')
        positive = build()
        if positive.returncode == 0 or "error HOO1006:" not in positive.stdout:
            raise SystemExit("Consumer did not enforce the expected HOO1006 diagnostic")
        if any(code in positive.stdout for code in ("CS9057", "CS8032", "AD0001")):
            raise SystemExit("Analyzer failed to load or execute in the consumer host")

        source.write_text('public class Example { public bool Run(bool ready) => ready; }\n')
        clean = build()
        if clean.returncode != 0:
            raise SystemExit("Corrected consumer did not build cleanly with warnings as errors")
        print(f"Verified real {args.framework} consumer: HOO1006 enforced, corrected source builds cleanly")


if __name__ == "__main__":
    main()
