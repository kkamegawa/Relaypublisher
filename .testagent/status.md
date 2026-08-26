# Issue #114 test status

## Quality review

- Validator assertions cover positive and negative selector boundaries, platform constraints, duplicate/count limits, and LOB/PKG version requirements.
- Selector assertions verify a real unique segment-prefix selection, stable relative order, and a non-mutating projection.
- Mapper assertions verify typed PKG/LOB fields, independent LOB versions, list ordering, and absence of the PKG-irrelevant build value from serialized JSON.
- Hash assertions pin the omitted-field macOS manifest shape and prove both new opt-in fields alter the canonical hash.
- Existing Graph client construction was updated to assert the new required LOB wire contract.

## Clean validation

- Targeted tests: 133 passed, 0 failed (before the two selector tests were added).
- `dotnet build IntuneLobPublisher.slnx --configuration Release`: succeeded with 0 warnings and 0 errors.
- `dotnet test IntuneLobPublisher.slnx --configuration Release --no-build`: 714 passed, 0 failed, 0 skipped.

## Issue #115 secure XAR/PKG inspector test status

### Requirement coverage

| Requirement | Evidence |
|---|---|
| Valid `PackageInfo` and `Distribution` metadata | `Inspect_PackageInfoUncompressed_ReturnsBundleFacts`, `Inspect_DistributionGzip_ReturnsBundleFacts`, `Inspect_DistributionBundleVersion_ReturnsBundleFacts` assert bundle ID, short version, build version, and source entry. |
| Uncompressed and gzip heap entries | The two valid metadata tests use `application/octet-stream` and `application/x-gzip` synthetic heap entries. |
| Deterministic priority and deduplication | `Inspect_PackageInfoAndDistribution_DeduplicatesWithPackageInfoPriority` asserts PackageInfo-first order, duplicate suppression, and PackageInfo values winning. |
| Required/helper declarations are not installed apps | `Inspect_RequiredBundlesAndHelpers_AreNotReportedAsInstalledApplications` asserts only the application bundle remains. |
| Invalid magic/header/truncated archive | `Inspect_InvalidMagic_FailsClosed`, `Inspect_UnsupportedHeaderVersion_FailsClosed`, `Inspect_TruncatedHeader_FailsClosed`, `Inspect_HeaderShorterThanMinimum_FailsClosed`, `Inspect_HeaderExtendsBeyondArchive_FailsClosed`, `Inspect_TruncatedCompressedToc_FailsClosed`, and `Inspect_TruncatedExpandedToc_FailsClosed`. |
| Overflow/out-of-bounds and unsupported encoding | `Inspect_UnsignedOffsetOverflowText_FailsClosed`, `Inspect_ArchiveEntryOutsideHeap_FailsClosed`, and `Inspect_UnsupportedHeapEncoding_FailsClosed`. |
| Malformed XML, invalid UTF-8, DTD/external entity rejection | `Inspect_MalformedTocXml_FailsClosed`, `Inspect_TocDtdXml_FailsClosed`, `Inspect_MalformedMetadataXml_FailsClosed`, `Inspect_DtdMetadataXml_FailsClosed`, and `Inspect_InvalidUtf8MetadataXml_FailsClosed`. |
| Compressed/expanded/entry/bundle/depth limits | `Inspect_CompressedTocLimitExceeded_FailsBeforeReadingToc`, `Inspect_ExpandedTocLimitExceeded_FailsBeforeAllocatingToc`, `Inspect_ExpandedTocContainsMoreBytesThanDeclared_FailsClosed`, `Inspect_MetadataEntryLimitExceeded_FailsClosed`, `Inspect_TooManyBundleRecords_FailsClosed`, `Inspect_Exactly4096BundleRecords_Succeeds`, `Inspect_MetadataXmlDepthLimitExceeded_FailsClosed`, and `Inspect_MetadataXmlDepthAtLimit_Succeeds`. |
| Null, cancellation, and stream capability guards | `Inspect_NullStream_ThrowsArgumentNullException`, `Inspect_CanceledToken_StopsBeforeReadingArchive`, and `Inspect_NonSeekableStream_FailsClosed`. |
| Deterministic/no-external-fixture operation | `XarPkgBundleInspectorTests.cs` builds every XAR header, zlib/gzip stream, TOC, heap, and corruption in memory; no network, shell, macOS utility, timer, or tenant is used. |

### Quality review

- Assertion-depth review: 33 tests, 0 assertion-free tests, 0 trivial-only tests, and 0 self-referential round-trip assertions. Positive cases assert concrete bundle IDs, versions, source precedence, order, and exact counts. Negative cases assert the exact hard-failure type; the cancellation case additionally checks the cancellation token.
- Pseudo-mutation review: the in-scope guard, boundary, ordering, filtering, encoding, XML-safety, and hard-failure mutations are pinned by the named tests above. The production source was not mutated during this worker pass because this worker owns only `tests/` and `.testagent/`; the review is therefore static rather than an injected-mutation score. XML node-count and cancellation during a long read remain implementation concerns outside the requested fixture matrix; cancellation before reading is covered.

### Clean validation

- Targeted inspector, policy, packaging, and artifact-integrity tests: 59 passed, 0 failed, 0 skipped.
- `dotnet build IntuneLobPublisher.slnx --configuration Release`: succeeded with 0 warnings and 0 errors.
- `dotnet test IntuneLobPublisher.slnx --configuration Release --no-build`: 760 passed, 0 failed, 0 skipped.
- `git diff --check`: passed.
