export default {
  branches: ["main"],
  packages: [
    {
      name: "HooSharper.Analyzers",
      path: ".",
      type: "version-file",
      manifest: "version",
      changelog: "CHANGELOG.md",
      scopes: ["hoosharper", "analyzers", "code-fixes", "release"],
      dependencies: [],
    },
  ],
  hooks: {
    afterVersion: ["bun run sync-readme-version"],
  },
  github: {
    releases: true,
  },
};
