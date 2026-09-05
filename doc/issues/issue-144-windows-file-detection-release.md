# Windows file-system detection documentation and release (GitHub #144)

## Summary

Document, validate, and release Relaypublisher file-system detection.

## Required work

- Update the canonical, Japanese-only design documents: `doc/00-overview.md`, `doc/01-manifest-schema.md` (the Windows manifest examples and the Windows Graph mapping table), and `doc/02-dotnet-architecture.md` (the `DetectionManifest` declaration). Per AGENTS.md, the affected design decision in `doc/00-overview.md` is updated first.
- Update the paired English and Japanese documents: `README.md` / `README_ja.md`, `doc/05-operation.md` / `_ja`, `doc/06-troubleshooting.md` / `_ja`, `doc/07-local-e2e.md` / `_ja`, and `samples/manifests/README.md` / `README_ja.md`.
- Add a Windows `Type: file` sample manifest. None exists today; every Windows sample uses `Type: script`. Update the sample catalog table in both languages.
- Document the intended input-hash asymmetry: `file` detection criteria are manifest fields and so change the hash, while a `script` detection body stays excluded from the hash (`doc/00-overview.md` 6.7).
- Record the design decision in `doc/adr.md` (decision, reason, impact, future caution) and the session outcome in `doc/task.md`.
- Run Release build/tests, package creation, package metadata validation, and `dotnet list package --vulnerable --include-transitive`.
- Publish `v1.1.0` through the existing GitHub Environment, draft-release, and NuGet Trusted Publishing workflow.

## Acceptance criteria

- Documentation pairs remain aligned, and the canonical design documents describe the `file` detection shape and its constraints.
- The released package contains the file-detection implementation.
- The draft release provenance and all three package destinations are verified before `v1.1.0` is consumed.

## Consumer verification (separate repository)

Updating the two `intuneapps` Global Secure Access manifests to use official file-version detection, and running their validate, package, and Intune dry-run in Azure Pipelines, is follow-up work in a separate repository. It is not part of this repository's completion criteria. The dry-run must report two non-failed outcomes and perform no writes. Note that `publish --dry-run` does not print the detection rule, so the mapped rule contents are covered by unit tests and by `EnsureMappableAsync` failing closed, not by reading dry-run output.

Production Intune publishing remains a separate approval boundary.

## Dependencies

- Parent: GitHub #141
- Manifest schema and validation: GitHub #142
- Microsoft Graph mapping: GitHub #143

## Release boundary

Implementation and documentation are delivered by PR #145. Tagging, draft-release generation, package
publication, and Intune validation remain deferred. The release follow-up must use the existing draft-release
and Trusted Publishing workflows. Publishing the draft release and publishing to Intune each remain separately
reviewable operations.
