#!/usr/bin/env python3
"""Verify a packed analyzer and code fix using the SDK compiler and formatter."""

import argparse
import difflib
import json
import shutil
import subprocess
import tempfile
from pathlib import Path
from xml.sax.saxutils import escape


def switch_fix_all_fixture() -> tuple[bytes, bytes]:
    # Adjacent, near-identical sections exercise BatchFixer's ability to merge
    # independent edits without confusing similar text in a neighboring case.
    def render(redundant_else):
        lines = ["internal static class SwitchProbe", "{", "    internal static int Read(int key, bool enabled)",
                 "    {", "        switch (key)", "        {"]
        for index in range(16):
            lines += [f"            case {index}:", "            {", "                if (enabled)",
                      "                {", f"                    return {400 + index};", "                }"]
            if redundant_else:
                lines += ["                else", "                {", f"                    return {800 + index};", "                }"]
            else:
                lines += [f"                return {800 + index};"]
            lines += ["            }"]
        lines += ["            default: return -1;", "        }", "    }", "}"]
        return ("\r\n".join(lines) + "\r\n").encode()
    return render(True), render(False)


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
    repo = Path(__file__).resolve().parents[1]
    version = (repo / "version").read_text().strip()

    with tempfile.TemporaryDirectory(prefix="hoosharper-consumer-") as directory:
        root = Path(directory)
        sdk_policy = json.loads(
            (repo / "global.json").read_text()
        )["sdk"]
        if args.sdk_version:
            sdk_policy = {"version": args.sdk_version, "rollForward": "disable"}
        # setup-dotnet respects the repository's latestPatch policy and may
        # install a later patch than its requested minimum version.
        (root / "global.json").write_text(json.dumps({"sdk": sdk_policy}))
        # Only the supplied archive may satisfy the reference, even when the
        # caller's artifact directory contains a good package next to a bad one.
        feed = root / "feed"
        feed.mkdir()
        shutil.copyfile(package, feed / package.name)
        (root / "Consumer.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            f'<TargetFramework>{escape(args.framework)}</TargetFramework>'
            '<OutputType>Exe</OutputType><RestorePackagesPath>packages</RestorePackagesPath>'
            '<TreatWarningsAsErrors>true</TreatWarningsAsErrors>'
            '</PropertyGroup><ItemGroup>'
            f'<PackageReference Include="HooSharper.Analyzers" Version="[{escape(version)}]" PrivateAssets="all" />'
            '</ItemGroup></Project>'
        )
        (root / "NuGet.config").write_text(
            '<configuration><packageSources><clear />'
            '<add key="local" value="feed" />'
            '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
            '</packageSources><packageSourceMapping>'
            '<packageSource key="local"><package pattern="HooSharper.Analyzers" /></packageSource>'
            '<packageSource key="nuget.org"><package pattern="Microsoft.*" /></packageSource>'
            '</packageSourceMapping></configuration>'
        )
        (root / ".editorconfig").write_text(
            'root = true\n\n[*.cs]\ndotnet_diagnostic.HOO1006.severity = error\n'
        )

        def run(*command: str) -> subprocess.CompletedProcess[str]:
            result = subprocess.run(
                [args.dotnet, *command],
                cwd=root, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                check=False,
            )
            print(result.stdout, end="")
            return result

        source = root / "Program.cs"
        original = (repo / "tests/smoke/PackageConsumer/Program.cs").read_text()
        source.write_text(original)
        build = ("build", "Consumer.csproj", "-c", "Release", "--nologo")
        positive = run(*build)
        if positive.returncode == 0 or "error HOO1006:" not in positive.stdout:
            raise SystemExit("Consumer did not enforce the expected HOO1006 diagnostic")
        if any(code in positive.stdout for code in ("CS9057", "CS8032", "AD0001")):
            raise SystemExit("Analyzer failed to load or execute in the consumer host")

        # Build the original program without diagnostics to capture its observable
        # behavior on this host. Compiler versions differ in which text they pass
        # to CallerArgumentExpression for an expanded params argument.
        binary = str(root / "bin/Release" / args.framework / "Consumer.dll")
        if run(*build, "--no-restore", "-p:RunAnalyzers=false").returncode:
            raise SystemExit("Original consumer did not compile")
        before = run(binary)
        if before.returncode or before.stdout.splitlines() not in (
                ["True", "Flag   ==   true", "True"],
                ["True", "Capture(flags: Flag   ==   true)", "True"]):
            raise SystemExit("Original program did not exercise the readonly reference and captured argument")

        fixed = run("format", "analyzers", "Consumer.csproj", "--no-restore",
                    "--diagnostics", "HOO1006", "--severity", "info")
        expected = original.replace("Console.WriteLine(Flag == false);", "Console.WriteLine(!Flag);")
        if fixed.returncode or source.read_text() != expected:
            raise SystemExit("SDK formatter must fix the safe comparison and preserve the readonly reference and captured text")
        clean = run(*build, "--no-restore")
        if clean.returncode != 0:
            raise SystemExit("Corrected consumer did not build cleanly with warnings as errors")
        after = run(binary)
        if after.returncode or after.stdout != before.stdout:
            raise SystemExit("Packaged fix changed runtime output")
        # Older libraries often have CRLF source without declaring end_of_line.
        # Read and write bytes here: read_text() would hide a mixed-ending fix.
        (root / ".editorconfig").write_text(
            "root = true\n[*.cs]\nindent_style = tab\ntab_width = 2\nindent_size = tab\n"
            "csharp_new_line_before_open_brace = none\ndotnet_diagnostic.HOO1010.severity = error\n"
        )
        nested = root / "NestedIf.cs"
        prefix = b"class NestedIf {\r\n\tpublic static int Run(bool first, bool second) {\r\n"
        suffix = b"\r\n\t\treturn 0;\r\n\t}\r\n}\r\n"
        nested.write_bytes(prefix + b"\t\tif (first) {\r\n\t\t\tif (second) {\r\n\t\t\t\treturn 1;\r\n\t\t\t}\r\n\t\t}\r\n" + suffix)
        expected_bytes = prefix + b"\t\tif (first && second) {\r\n\t\t\treturn 1;\r\n\t\t}\r\n" + suffix
        nested_build = run(*build, "--no-restore")
        if nested_build.returncode == 0 or "error HOO1010:" not in nested_build.stdout:
            raise SystemExit("Consumer did not enforce HOO1010 on the CRLF fixture")
        fixed = run("format", "analyzers", "Consumer.csproj", "--no-restore",
                    "--diagnostics", "HOO1010", "--severity", "info")
        if fixed.returncode or nested.read_bytes() != expected_bytes or run(*build, "--no-restore").returncode:
            raise SystemExit("SDK formatter did not preserve unconfigured CRLF when merging the nested if")

        (root / ".editorconfig").write_text("root = true\n[*.cs]\ndotnet_diagnostic.HOO1003.severity = error\n")
        switch = root / "SwitchProbe.cs"
        switch_before, switch_after = switch_fix_all_fixture()
        switch.write_bytes(switch_before)
        report = root / "switch-before.sarif"
        initial = run(*build, "--no-restore", f"-p:ErrorLog={report}")
        findings = json.loads(report.read_text(encoding="utf-8-sig"))["runs"][0]["results"]
        if not initial.returncode or sum(item["ruleId"] == "HOO1003" for item in findings) != 16:
            raise SystemExit("Consumer did not exercise all sixteen independent redundant else diagnostics")
        formatted = run("format", "analyzers", "Consumer.csproj", "--no-restore",
                        "--diagnostics", "HOO1003", "--severity", "info")
        if formatted.returncode:
            raise SystemExit("SDK formatter failed to run the HOO1003 switch fix")
        switch_result = switch.read_bytes()
        if switch_result != switch_after:
            # repr keeps accidental LF/CRLF changes visible in the diagnostic.
            differing = difflib.unified_diff(
                [repr(line) for line in switch_after.splitlines(keepends=True)],
                [repr(line) for line in switch_result.splitlines(keepends=True)],
                "expected switch", "rewritten switch", lineterm="",
            )
            print("\n".join(differing))
            raise SystemExit("HOO1003 did not remove every redundant else while preserving all switch labels, values and CRLF")
        if run(*build, "--no-restore").returncode:
            raise SystemExit("Switch consumer failed to compile after HOO1003 Fix All")
        print(f"Verified real {args.framework} consumer: HOO1006 enforced, safe fix applied, "
              "readonly reference and captured text preserved; HOO1010 preserved unconfigured CRLF; "
              "HOO1003 preserved all sixteen switch labels and both values per case; "
              "corrected sources build cleanly")


if __name__ == "__main__":
    main()
