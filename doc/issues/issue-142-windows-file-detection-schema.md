# Windows file-system detection manifest schema (GitHub #142)

## Summary

Extend the manifest model and validators with a Windows `Detection.Type: file` shape while preserving the existing `script` shape.

## Required work

- Define file path, file/folder name, operation type, comparison operator, comparison value, and 32-bit-context fields.
- Make script-only fields and file-only fields mutually exclusive.
- Validate supported operation/operator combinations and required comparison values.
- Keep macOS detection behavior unchanged.
- Add manifest loading and validation regression tests.
- Update the authoritative manifest schema design before implementation.

## Acceptance criteria

- Valid version-based file detection loads and validates.
- Missing, mixed, or unsupported file detection fields fail with actionable errors.
- Existing script detection tests remain green.

## Dependencies

- Parent: GitHub #141
- Microsoft Graph mapping: GitHub #143
- Documentation, validation, and release: GitHub #144

Implementation is intentionally deferred. This document records the reviewable scope for the follow-up change.
