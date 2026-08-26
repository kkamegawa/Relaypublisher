# Issue #114 test research

## Scope

Broad foundation coverage across manifest validation, selector projection, input hashing, and Graph payload mapping.

## Existing conventions

- SDK-style .NET 10 solution (`IntuneLobPublisher.slnx`).
- MSTest 4.3.3 in `tests/IntuneLobPublisher.Core.Tests`.
- Fixtures are created through `TestManifests`; validation assertions use `AssertInvalid`.
- Payload tests assert typed properties and serialized wire fields where Graph shape matters.

## Acceptance checklist

- Omitted primary preserves first entry and does not mutate the manifest.
- Exact and unique segment-prefix selection reorder only the payload projection.
- Zero, multiple, case-mismatched, and non-segment matches fail validation.
- Blank selector, Windows use, duplicate IDs, and more than 500 entries fail.
- LOB requires `BundleBuildVersion`; PKG permits it and ignores it in Graph mapping.
- PKG maps selected bundle to primary fields and selected-first `includedApps`.
- LOB maps selected bundle to top-level `bundleId`, separate build/version fields, and selected-first `childApps`.
- New nullable fields omitted from an existing macOS manifest retain a pinned manifest hash.
- Setting either new field changes the hash deterministically.

## Target inventory

- `ManifestValidationTests.cs`
- `Publishing/MacOsAppPayloadMapperTests.cs`
- `InputHashCalculatorTests.cs`
- `TestManifests.cs`

## Issue #115 secure XAR/PKG inspector test scope

The stack branch is an SDK-style .NET 10 solution. The canonical test project is
`tests/IntuneLobPublisher.Core.Tests/IntuneLobPublisher.Core.Tests.csproj`; it
uses MSTest 4.3.3 and includes C# files by SDK glob. Existing packaging tests
create temporary files under the test-owned temp directory and use in-memory
or synthetic byte content; no test invokes `pkgutil`, a shell process, an
external URL, or a tenant.

The bounded production targets for this phase are the XAR reader/PKG bundle
inspector and its value objects, expected under `src/IntuneLobPublisher.Core`
packaging/inspection namespaces. Tests will use synthetic XAR bytes constructed
in test code so archive layout, compression, offsets, and XML safety remain
deterministic on Windows and Ubuntu runners.

### Acceptance checklist

- A valid XAR with valid `PackageInfo` yields bundle id, short version, and
  build version facts.
- A valid XAR with valid `Distribution` yields the same facts when
  `PackageInfo` is absent or not authoritative.
- Uncompressed and gzip-compressed heap entries are both accepted.
- Multiple declarations are deduplicated deterministically and priority/order
  is stable; required-bundles/helper declarations are not reported as installed
  application bundles.
- Invalid XAR magic, unsupported header/version, truncated header/TOC/heap,
  checked-arithmetic overflow, and out-of-bounds offsets fail closed.
- Unsupported heap encodings fail closed; no fallback parser or external tool
  is used.
- Malformed XML, invalid UTF-8, DTD, and external entity content fail closed.
- Compressed TOC, expanded TOC, metadata-entry, bundle-record, and XML-depth
  limits are enforced at or around their documented boundaries.
- Test fixtures are self-contained and do not depend on network, macOS-only
  utilities, wall-clock timing, or a live Graph tenant.
