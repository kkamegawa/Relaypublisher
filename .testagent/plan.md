# Issue #114 test plan

1. Update the shared valid LOB fixture with an independent build version.
2. Add validator cases for exact/prefix success and every selector/list/app-type rejection boundary.
3. Add mapper cases for PKG and LOB selected-first wire contracts plus non-mutation.
4. Pin the omitted-field macOS manifest hash and prove each opt-in field changes it.
5. Run the targeted MSTest classes, then the full Release solution tests and build.
6. Re-read assertions against the production selector/validator/mapper and record the quality review in `status.md`.

## Issue #115 secure inspector test phases

1. Add a reusable synthetic XAR builder in a new test helper. It must emit the
   real big-endian XAR header, zlib TOC, heap entries, optional gzip encoding,
   and controlled corruptions without reading any fixture from disk.
2. Add valid metadata tests for PackageInfo and Distribution, including
   uncompressed/gzip entries, deterministic priority/deduplication, and
   exclusion of required-bundles/helper metadata from installed applications.
3. Add fail-closed tests for magic/header/truncation/overflow/bounds, unsupported
   encodings, malformed or unsafe XML, and each configured extraction limit.
4. Run only the inspector test class(es) first, then the full test project if
   the new production API requires adjacent packaging tests to compile.
5. Re-read every assertion against the implementation; run test-gap-analysis
   and assertion-quality, then record clean command output, any justified
   out-of-scope limit cases, and the final requirement mapping in
   `.testagent/status.md`.
