# Windows file-system detection manifest schema (GitHub #142)

## Summary

Extend the manifest model and validators with a Windows `Detection.Type: file` shape while preserving the existing `script` shape.

## Required work

- Define file path, file/folder name, operation type, comparison operator, comparison value, and 32-bit-context fields. Every new property must be nullable, including `bool? Check32BitOn64System`: `InputHashCalculator` drops nulls from its canonical JSON, so a non-nullable property changes every existing manifest's hash and republishes every app.
- Make script-only fields and file-only fields mutually exclusive in both directions, and reject file-only fields for `Platform: macos`.
- Restrict `OperationType` to `exists` and `version` for this release, and `Operator` to the six comparison operators. `notConfigured` is a legal Graph value for both, but it is the Graph-side "unset" sentinel and produces a rule that never matches on the device, so reject it as manifest input with a message that says so.
- Require `Operator` and `ComparisonValue` for `version`, and reject both for `exists`.
- Validate the `ComparisonValue` format per operation type. Intune does not reject a malformed value with a 400; it simply never matches on the device, which presents as an app that reinstalls on every sync. For `version`, require one to four numeric parts of one to five digits each (`^\d{1,5}(\.\d{1,5}){0,3}$`). `Version.TryParse` is not a substitute because it rejects a single-part value that Intune accepts.
- Validate `Path` and `FileOrFolderName` as target-device values, never repository paths. `Path` accepts a drive-rooted, root-relative, UNC, or environment-variable-rooted form; `FileOrFolderName` is a single leaf name. Reject wildcards in both, because Intune does not expand them. `PathSafety` must not be applied: `ResolveWithin` joins against the repository root and `IsSafeRelativePath` rejects rooted paths, so it would reject every legal value. Keep the plausibility helpers in `ManifestValues` to make that boundary obvious.
- Keep macOS detection behavior unchanged.
- Add manifest loading and validation regression tests.
- Update the authoritative manifest schema design before implementation.

## Acceptance criteria

- Valid `exists` and version-based file detection loads and validates.
- Missing, mixed, or unsupported file detection fields fail with actionable errors, including `notConfigured` for either enum.
- The manifest hash of an unchanged script-detection manifest is identical before and after the model change.
- Two file-detection manifests differing only in `ComparisonValue` produce different manifest hashes.
- Existing script detection tests remain green.

## Dependencies

- Parent: GitHub #141
- Microsoft Graph mapping: GitHub #143
- Documentation, validation, and release: GitHub #144

Implemented by PR #145. This document remains the reviewable scope and acceptance record.
