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
