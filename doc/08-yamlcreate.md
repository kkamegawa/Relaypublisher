# tools/yamlcreate.ps1 — Manifest creation and version update script

## 1. Purpose and scope

`tools/yamlcreate.ps1` uses interactive prompts to create Relaypublisher YAML manifests and update existing manifests
to a new version. It serves the same role as `Tools/YamlCreate.ps1` in winget-pkgs.

- The authoritative schema is [`doc/01-manifest-schema.md`](01-manifest-schema.md). This script assists with input; it does not define the schema.
- The authoritative validator is `relaypublisher validate`. The script duplicates some checks before saving to provide
  early feedback, then automatically runs the CLI's `validate` command after saving.
- The authoritative version update procedure is [`doc/05-operation.md`](05-operation.md) §4c. Update mode automates that procedure.

PowerShell 7.3 or later is required. The script runs on Windows, macOS, and Linux. There is no bash implementation,
because maintaining multiple scripts is unnecessary.

## 2. Modes

| Mode | Purpose |
|---|---|
| `New` | Create a manifest using prompts that follow the schema |
| `Update` | Update an existing manifest to a new `PackageVersion` |

If `-Mode` is omitted, the script uses `Update` when `-Path` is provided; otherwise, it prompts for the mode.

## 3. Parameters

| Parameter | Applicable modes | Description |
|---|---|---|
| `-Mode <New\|Update>` | Both | If omitted, determined by whether `-Path` is provided or by an interactive prompt |
| `-Path <path>` | Update | An existing `*.yaml` file or a version folder (updates all `*.yaml` files in the folder) |
| `-PackageVersion <version>` | Both | The new version in Update mode (required). The default value for the `PackageVersion` prompt in New mode |
| `-Platform <windows\|macos>` | Both | Selects the initial branch in New mode. Filters the target manifests in Update mode |
| `-Architecture <x64\|arm64>` | New | Selected interactively if omitted |
| `-OutputDirectory <dir>` | Both | Output directory. Uses the default layout in §7 if omitted |
| `-RepoRoot <dir>` | Both | Base directory for relative paths in the manifest. Defaults to `git rev-parse --show-toplevel` |
| `-GroupId <guid[]>` | New | Skips assignment prompts and creates an include assignment with `Intent: required` for each GUID. Separate multiple values with commas (`-GroupId 'a','b'`). Use `pwsh -Command` to pass multiple values, because space-separated values passed through `pwsh -File` are not treated as an array |
| `-FilterId <guid>` | New | Assignment filter applied to the assignments created by `-GroupId` |
| `-FilterMode <include\|exclude>` | New | Filter mode when `-FilterId` is specified. Defaults to `include` |
| `-EntraGroupCsv <path>` | New | The `entra-groups.csv` file produced by `tools/export-intune-entra.ps1`. Enables selecting GUIDs by display name |
| `-AssignmentFilterCsv <path>` | New | The `assignment-filters.csv` file produced by the same exporter |
| `-Sha256 <hash>` | Update | Supplies a digest without downloading. Can only be used for a single manifest file with a single source |
| `-NoDownload` | Both | Disables all network access. All digests are entered manually |
| `-SkipValidate` | Both | Skips `relaypublisher validate` after saving |
| `-Force` | Both | Overwrites existing files and skips the final confirmation prompt |

`-WhatIf` and `-Confirm` are also supported (for file writes only).

## 4. New mode

### 4.1 Select the platform first

The first prompt (or `-Platform`) selects `windows` or `macos` and determines the subsequent set of prompts.
Fields that apply only to the other platform are neither prompted for nor written to the output.

### 4.2 Common top-level fields

| Prompt | Required | Default / Notes |
|---|---|---|
| `PackageIdentifier` | Required | Part of the app identity. Must remain the same across versions |
| `PackageName` | Required | |
| `Publisher` | Required | |
| `Description` | Required | |
| `PackageVersion` | Required | Uses `-PackageVersion` as the default |
| `Owner` | Optional | Omitted if empty |
| `Developer` | Optional | Omitted if empty |
| `InformationUrl` | Optional | Omitted if empty |
| `Icon` | Optional | Repository-relative path. Required only for `AppType: lob`. Validates the extension, 1 MiB size limit, and file existence |
| `RoleScopeTagIds` | Optional | Comma-separated. Each item is always quoted in the output |
| `AssignmentSync` | Optional | Defaults to `merge` |
| `DisplayName` | Required | Defaults to the format `<PackageName> [Windows x64]`. Values containing `PackageVersion` are rejected |
| `Categories` | Optional | Answering `n` omits the key entirely (preserving existing associations). Answering `y` and leaving the input empty produces `Categories: []` |

`SchemaVersion` is fixed at `"1.0"` and is not prompted for.

### 4.3 Windows-specific fields

| Prompt | Default / Notes |
|---|---|
| `Package.IntuneWin.SetupFile` | Defaults to `install.ps1` |
| `Package.RepositoryFiles[]` | Zero or more pairs of `Source` (repository-relative; file existence is checked) and `Destination` |
| `Package.ExternalFiles[]` | Zero or more source items as described in §5 |
| `Install.CommandLine` | Defaults to `powershell.exe -ExecutionPolicy Bypass -File .\install.ps1` |
| `Install.UninstallCommandLine` | Defaults to `powershell.exe -ExecutionPolicy Bypass -File .\uninstall.ps1` |
| `Install.InstallExperience` | Defaults to `system` |
| `Install.RestartBehavior` | Defaults to `suppress` |
| `Install.ReturnCodes[]` | Optional. If no entries are added, the key is omitted and Intune defaults apply (0/1707 success, 3010 softReboot, 1641 hardReboot, 1618 retry) |
| `Detection.ScriptFile` | `Type: script` is fixed. Repository-relative; file existence is checked |
| `Detection.RunAs32Bit` / `EnforceSignatureCheck` | Default to false |
| `Requirements.MinimumOSVersion` | Lists only the keys in `WindowsReleaseTable`, along with release names. Defaults to `10.0.19045` |
| `Requirements.Architecture` | Automatically set to the app's `Architecture` |

### 4.4 macOS-specific fields

| Prompt | Default / Notes |
|---|---|
| `AppType` | Defaults to `pkg`. Selecting `lob` makes the top-level `Icon` required |
| `Source` | One source item as described in §5 |
| `Requirements.MinimumOSVersion` | Lists only the keys in `MacOsMinimumOperatingSystemTable`. For `AppType: lob`, the beta-only values 14.0 / 15.0 / 26.0 are excluded. Always quoted in the output (YAML reads an unquoted `14.0` as a float, which no longer matches the version table key) |
| `Detection.IgnoreAppVersion` | Defaults to false |
| `Detection.IncludedApps[]` | One or more `BundleId` + `BundleVersion` pairs. `BundleVersion` defaults to `PackageVersion` |
| `Scripts.PreInstall` / `PostInstall` | Prompted for only when `AppType: pkg`. Validates the `.sh` extension, file existence, length below 15360 characters, absence of a BOM, and a leading `#!`. Omits the `Scripts` block if both are empty |

## 5. Source items and Sha256

The unified item shape for `publicHttp` / `githubRelease` / `azureBlob` (doc/01-manifest-schema.md §5.0.1)
is shared by each Windows `ExternalFiles` entry and the macOS `Source`.

The script enforces the following `Auth` constraints.

| Type | Allowed `Auth.Type` values |
|---|---|
| `publicHttp` | Fixed at `none` (anonymous downloads only) |
| `githubRelease` | `none` (default) / `token` |
| `azureBlob` | Fixed at `workloadIdentity` |

For `Auth.Type: token`, enter the **environment variable name**. The token value is never written to the manifest or logs.

`Sha256` is obtained as follows for each of the three source types (with `-NoDownload`, these steps are skipped and the digest is entered manually).

| Type | Method |
|---|---|
| `publicHttp` | Downloads `Url` to a temporary directory and runs `Get-FileHash -Algorithm SHA256` |
| `githubRelease` | Retrieves the assets for the release tag, prompts for `AssetName`, downloads the selected asset through the asset ID REST API with `Accept: application/octet-stream`, and calculates the hash. For `Auth.Type: token`, the environment variable value is used as the Bearer token. Public and private repositories use the same download path |
| `azureBlob` | No automatic download (workload identity is required). Falls back to manual input and provides instructions for `az storage blob download` |

If automatic calculation fails, the script displays a warning and switches to manual input. Manually entered values must be 64 hexadecimal digits.
Downloaded temporary files are always deleted.

URL credentials, query strings, and fragments are masked in manifest previews, update diffs, lines that still contain the old version,
and download errors. URLs are preserved in the saved manifest.

## 6. Assignments

`GroupId` and `FilterId` are **both optional**. If no assignments are added, the script writes `Assignments: []`.
This form contains no tenant-specific IDs, so it can pass through `plan` → `validate` → `package` → `publish --dry-run`
unchanged in any tenant.

| Prompt | Default / Notes |
|---|---|
| `Target` | Defaults to `group`. `GroupId` is omitted for `allDevices` / `allLicensedUsers` |
| `GroupId` | Required for `Target: group`. Validated as a GUID |
| `Mode` | Defaults to `include` |
| `Intent` | Required for `Mode: include`. For `AppType: pkg`, `uninstall` is excluded from the choices |
| `FilterId` | Optional. `FilterMode` is prompted for (and required) only when a filter ID is specified |
| `Settings.Notifications` / `RestartGracePeriodMinutes` | Windows (win32) only |

Assignments with duplicate `Target` + `GroupId` + `Mode` combinations are not added within the same manifest.

Passing CSV files produced by [`tools/export-intune-entra.ps1`](../tools/export-intune-entra.ps1) to `-EntraGroupCsv`
and `-AssignmentFilterCsv` enables selection by display name instead of entering GUIDs directly. If the CSV does not contain
the expected column names, the script warns and falls back to manual GUID input.

The exported column names are `GroupName` / `GroupId` for groups and `FilterName` / `FilterId` for filters.
`DisplayName` / `Name` are also accepted as name columns. A CSV containing only headers provides no choices, so the script falls back to manual input.

When `-GroupId` is specified, assignment prompts are skipped and an assignment with
`Target: group` / `Mode: include` / `Intent: required` is created for each GUID. If `-FilterId` is also specified,
the same filter is applied to every assignment.

## 7. Output location

If `-OutputDirectory` is specified, files are written to that folder. Otherwise, the defaults are as follows.

| Mode | Default output location |
|---|---|
| New | `<RepoRoot>/manifests/<Publisher>/<PackageIdentifier>/<PackageVersion>/` |
| Update (the source file's parent folder name matches the old version) | A sibling of the source folder: `<...>/<new-version>/` |
| Update (otherwise) | `<RepoRoot>/manifests/<Publisher>/<PackageIdentifier>/<new-version>/` |

New mode uses the filename `<packageidentifier>-<platform>-<architecture>.yaml` (lowercase). Update mode keeps the original filename.
Existing files are not overwritten unless `-Force` is specified.

Files are encoded as UTF-8 without a BOM. New mode uses LF line endings. Update mode **preserves the original file's line endings**
so that local diffs contain only the version update changes.

## 8. Update mode (version updates)

### 8.1 Fields that are updated

Updates are line-based, preserving comments, key order, and formatting. For `PackageVersion`, version-related source fields,
and `Sha256`, only the value span is edited; quote styles and trailing comments (including references to the old version) are preserved.

1. Set the top-level `PackageVersion` to the new version.
2. Replace the old version string in the values of `Url` / `Tag` / `AssetName` / `BlobName` / `Destination` / `BundleVersion` lines.
   Tags with a `v` prefix, such as `v7.6.4`, are also updated.
   To avoid replacing `1.2` within `1.2.3`, matches preceded by a digit or dot, or followed by a digit or a dot followed by a digit, are excluded.
   A version immediately followed by a file extension, such as `tool-1.2.pkg`, is replaced.
3. Recalculate every `Sha256`. A version change also changes the digest, so no hash update may be skipped.
   `-NoDownload`, `-Sha256`, or a failed automatic download requires a manually supplied digest.
4. Warn by listing lines that still contain the old version string after replacement, such as references in comments. These are not rewritten automatically.
5. Display changed lines as a colored old/new diff, then save after confirmation (`-Force` skips confirmation).

Source authentication is read independently of the order of `Auth` and `Sha256`, and credentials from different source items are kept separate.
For single-line quoted values, `''` inside single quotes and YAML escapes inside double quotes are decoded before the values are used as sources or credentials.
A `#` inside quotes is treated as part of the value, distinct from a trailing comment outside the quotes.

### 8.2 Fields that are not updated

`PackageIdentifier` / `Platform` / `Architecture` / `DisplayName` are not rewritten. These define the app identity;
changing them creates a separate app in Intune and leaves the existing app behind (see the design invariants in AGENTS.md).

`Requirements.MinimumOSVersion`, `Icon`, `Scripts`, `Assignments`, and `Categories` are also left unchanged.
To change these for a new version, edit the generated manifest manually.

### 8.3 Examples

```powershell
# PowerShell 7.6.4 -> 7.6.5. Update all *.yaml files in the folder and write them to the sibling 7.6.5/ folder.
./tools/yamlcreate.ps1 -Mode Update `
    -Path samples/manifests/Microsoft/Microsoft.PowerShell/7.6.4 `
    -PackageVersion 7.6.5
```

```powershell
# Update a single file without network access, supplying the digest explicitly.
./tools/yamlcreate.ps1 -Mode Update `
    -Path manifests/Contoso/Contoso.Tool/1.2.3/contoso.tool-macos-arm64.yaml `
    -PackageVersion 1.2.4 -NoDownload -Sha256 <sha256> -Force
```

## 9. Validation before saving

Immediately before saving, the script performs the following checks without calling Graph or the CLI.
Each check matches the corresponding rule in `src/IntuneLobPublisher.Core/Validation/`.

- Check manifest-derived paths (`Destination` / `Source` / `SetupFile` / `ScriptFile` / `Icon` / `Scripts.*`)
  for path traversal, absolute paths, and drive letters
- Ensure `Sha256` contains 64 hexadecimal digits
- Ensure `GroupId` / `FilterId` are GUIDs
- Ensure `DisplayName` does not contain `PackageVersion`
- Validate the `Icon` extension (`.png` / `.jpg` / `.jpeg`), 1 MiB size limit, and file existence
- Validate macOS `Scripts`: `.sh` extension, file existence, length below 15360 characters, no BOM, and a leading `#!`
- Ensure `AppType: lob` has an `Icon` and cannot select macOS 14 or later
- Ensure `AppType: pkg` cannot select `Intent: uninstall`

After saving, if `relaypublisher` is on PATH, the script runs `relaypublisher validate --manifest <saved-path> --repo-root <RepoRoot>`.
If it is not found, the script displays the command to run. `-SkipValidate` suppresses this step.

## 10. Limitations

- The script does not fully parse YAML. Update mode edits lines only for the keys listed in §8.1. Manifests with severely malformed
  indentation or written in flow style (`{ }` / `[ ]`) are not supported.
- The script does not implement `AssignmentSync` semantics (merge / replace); it only writes the value.
- It cannot verify whether the names in `Categories` exist in the tenant. This is checked during the Graph preflight
  in publish / dry-run (doc/01-manifest-schema.md §5.8).
- `Sha256` is not calculated automatically for `azureBlob`.
- Update mode does not change `Requirements` / `Assignments` / `Scripts` / `Categories`.

## 11. Regression tests

`tests/Tools/YamlCreate.Tests.ps1` verifies creation and update output, authenticated download requests, CSV selection,
and URL masking without network access or additional modules. CI runs these tests on both Windows and Linux.

PowerShell 7:

```powershell
pwsh -NoProfile -File ./tests/Tools/YamlCreate.Tests.ps1
```

bash (macOS / Linux; requires PowerShell 7):

```bash
pwsh -NoProfile -File ./tests/Tools/YamlCreate.Tests.ps1
```
