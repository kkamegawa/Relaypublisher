# Windows file-system detection rules (GitHub #141)

## Summary

Add first-class, scriptless Windows Win32 application detection based on the Microsoft Graph v1.0 `win32LobAppFileSystemRule` contract, so a manifest can map a file version rule without embedding a PowerShell detection script. Existing script detection stays compatible without a manifest migration.

## Scope decisions

- `Detection.Type` becomes a discriminator with `script` and `file` values.
- `v1.1.0` supports the `exists` and `version` operation types only. Graph also defines `modifiedDate`, `createdDate`, and `sizeInMB`; each needs its own comparison-value format rule and tests, so they are deferred rather than shipped unvalidated.
- `notConfigured` is a legal Graph value for `operationType` and `operator`, but it is the Graph-side "unset" sentinel. A manifest that spells it produces a rule that never matches on the device, which surfaces as an app that reinstalls on every sync, so it is rejected as manifest input. The mapper synthesizes `operator: notConfigured` for `exists`.
- `Detection.Path` and `Detection.FileOrFolderName` describe the target device, never the repository, so they never go through `PathSafety`.

## Required work

- Manifest model and validation: GitHub #142.
- Microsoft Graph mapping: GitHub #143.
- Documentation, validation, and release: GitHub #144.

## Acceptance criteria

- The manifest schema supports both the existing `script` detection and the new `file` detection shape.
- Validation rejects incomplete or incompatible file detection fields, including `notConfigured`.
- Publishing emits a Graph v1.0 `win32LobAppFileSystemRule` with version comparison support.
- Script-based detection remains compatible and its Graph payload is byte-identical to today's.
- English and Japanese documentation describe the new shape and constraints.
- Release build, tests, package validation, and vulnerability audit pass.
- A new Relaypublisher package version is released through the existing Trusted Publishing workflow.

## Release boundary

Relaypublisher's part of this work is complete when `v1.1.0` has reached all three package destinations. Updating the `intuneapps` Global Secure Access manifests and running their Intune dry-run is follow-up work in a separate repository, and production Intune publishing remains a separate approval boundary.

## Out of scope

- `modifiedDate`, `createdDate`, and `sizeInMB` operation types.
- Registry and MSI product-code detection.
- Requirement rules; only detection rules are generated.
- Printing the detection rule in `plan` or `publish --dry-run` output.

Implemented by PR #145. This document remains the reviewable scope and acceptance record; release publication
and consumer-repository validation remain deferred as described above.
