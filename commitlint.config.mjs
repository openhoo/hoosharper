export default {
  // Hooversion must retain its case-sensitive package name to resume a
  // prepared release. The release CLI validates its package/version metadata.
  ignores: [
    (message) => /^chore\(release\): HooSharper\.Analyzers \d+\.\d+\.\d+(?: \(#\d+\))?(?:\n|$)/u.test(message),
  ],
  extends: ["@commitlint/config-conventional"],
};
