import { readFileSync, writeFileSync } from "node:fs";

const versionPath = "version";
const readmePath = "README.md";
const checkOnly = process.argv.includes("--check");
const version = readFileSync(versionPath, "utf8").trim();

if (!/^\d+\.\d+\.\d+$/.test(version)) {
  throw new Error(`Invalid semantic version in ${versionPath}: ${JSON.stringify(version)}`);
}

let readme = readFileSync(readmePath, "utf8");
const replacements = [
  {
    name: "dotnet CLI installation commands",
    pattern: /(--version\s+)\d+\.\d+\.\d+/g,
    replace: (_match: string, prefix: string) => `${prefix}${version}`,
    expected: 2,
  },
  {
    name: "central package management reference",
    pattern: /(<PackageVersion Include="HooSharper\.Analyzers" Version=")\d+\.\d+\.\d+(" \/>)/g,
    replace: (_match: string, prefix: string, suffix: string) => `${prefix}${version}${suffix}`,
    expected: 1,
  },
  {
    name: "direct package reference",
    pattern: /(<PackageReference Include="HooSharper\.Analyzers"\s+Version=")\d+\.\d+\.\d+(")/g,
    replace: (_match: string, prefix: string, suffix: string) => `${prefix}${version}${suffix}`,
    expected: 1,
  },
];

for (const replacement of replacements) {
  let count = 0;
  readme = readme.replace(replacement.pattern, (...args: string[]) => {
    count++;
    return replacement.replace(...args);
  });

  if (count !== replacement.expected) {
    throw new Error(
      `Expected ${replacement.expected} ${replacement.name} in ${readmePath}, found ${count}`,
    );
  }
}

const current = readFileSync(readmePath, "utf8");
if (current === readme) {
  console.log(`${readmePath} already uses version ${version}`);
} else if (checkOnly) {
  throw new Error(`${readmePath} does not use version ${version}; run bun run sync-readme-version`);
} else {
  writeFileSync(readmePath, readme);
  console.log(`Updated ${readmePath} to version ${version}`);
}
