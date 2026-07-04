# Implement Intune app assignment merge

## Goal

Apply assignment definitions from manifests to Intune apps.

## Assignment model

Each assignment supports:

- `Target`: `group` (default) | `allDevices` | `allLicensedUsers`
  - `group` requires a valid `GroupId` GUID (`groupAssignmentTarget`).
  - `allDevices` / `allLicensedUsers` map to the built-in targets and must not have `GroupId`.
- `Mode`: `include` (default) | `exclude`
  - `exclude` maps to `exclusionGroupAssignmentTarget` (group targets only). `Intent` applies to include targets only.
- `Intent`: `required` | `available` | `uninstall`
- `FilterId` / `FilterMode` (optional): assignment filter GUID and `include` | `exclude`. `FilterMode` is required when `FilterId` is set.
- `Settings` (optional, win32 only): notification behavior, restart grace period.

Validation:

- `GroupId` must be a valid GUID for `Target: group`; forbidden otherwise.
- Duplicate targets within one manifest fail validation.
- `Intent: uninstall` is rejected for macOS `AppType: pkg` apps (not supported by the app type).

## Merge / Replace semantics

Intune cannot hold two intents for the same group, so merge is defined as a per-group upsert:

- `merge` (default):
  - Targets present in the manifest are added; when the target already exists, its intent, settings, and filter are updated to the manifest values (manifest wins on intent conflict).
  - Existing assignments not listed in the manifest are never removed.
- `replace`:
  - The manifest assignment list is authoritative; existing assignments not in the manifest are removed.

## Requirements

- Parse assignments from manifest (extended model above).
- Validate GroupId as GUID and the target/mode/filter combinations.
- Get current Intune app assignments.
- Compute a plan: add / update / keep / (replace only) remove.
- Apply the plan via Graph, honoring `Retry-After` on 429.
- Add dry-run diff output showing the full plan before applying.

## Acceptance criteria

- App can be assigned to target groups by GUID.
- Built-in targets (allDevices / allLicensedUsers) can be assigned.
- Exclusion assignments can be created.
- Assignment filters are applied when specified.
- Existing assignments are not removed in merge mode.
- Intent conflict for the same group is resolved to the manifest value in merge mode.
- Replace mode removes assignments not present in the manifest.
- Dry-run shows assignment diff without changing anything.
