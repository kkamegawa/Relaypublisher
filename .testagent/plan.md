# Issue #114 test plan

1. Update the shared valid LOB fixture with an independent build version.
2. Add validator cases for exact/prefix success and every selector/list/app-type rejection boundary.
3. Add mapper cases for PKG and LOB selected-first wire contracts plus non-mutation.
4. Pin the omitted-field macOS manifest hash and prove each opt-in field changes it.
5. Run the targeted MSTest classes, then the full Release solution tests and build.
6. Re-read assertions against the production selector/validator/mapper and record the quality review in `status.md`.
