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
