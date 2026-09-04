# Windows file-system detection documentation and release (GitHub #144)

## Summary

Document, validate, and release Relaypublisher file-system detection, then use the released tool for the Global Secure Access dry-run.

## Required work

- Update paired English and Japanese documentation and samples.
- Record the design decision and task completion in the repository records.
- Run Release build/tests, package creation, package metadata validation, and vulnerability audit.
- Publish through the existing GitHub Environment and NuGet Trusted Publishing workflow.
- Update the two Global Secure Access manifests to use official file-version detection and validate, package, and dry-run them in Azure Pipelines.

## Acceptance criteria

- Documentation pairs remain aligned.
- The released package contains the file-detection implementation.
- The Global Secure Access x64 and Arm64 manifest list validates and packages.
- Intune dry-run reports two non-failed outcomes and performs no writes.
- Production Intune publishing remains a separate approval boundary.

## Dependencies

- Parent: GitHub #141
- Manifest schema and validation: GitHub #142
- Microsoft Graph mapping: GitHub #143

## Release boundary

Implementation, tagging, draft-release generation, package publication, and Intune validation are intentionally deferred. The follow-up implementation must use the existing draft-release and Trusted Publishing workflows. Publishing the draft release and publishing to Intune each remain separately reviewable operations.
