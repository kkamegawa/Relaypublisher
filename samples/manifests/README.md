# Sample manifests

This directory mixes two kinds of manifests:

- **E2E-runnable samples**: point at a real, publicly downloadable package. `plan` -> `validate` -> `package` succeed unmodified (`publish` still needs a real tenant and, for `Assignments`, a real group GUID; see [../../doc/07-local-e2e.md](../../doc/07-local-e2e.md)).
- **Reference-only samples**: illustrate a schema shape or a constraint. Some are written to fail `validate` or `package` on purpose, to document a real-world limitation. Read the comments inside the file before assuming it is a bug.

All commands below use `--repo-root samples`, because `RepositoryFiles.Source` / `Icon` paths in these manifests are relative to this `samples/` directory, not the repository root (`scripts/windows/...` here means `samples/scripts/windows/...`, not `<repo-root>/scripts/windows/...`). Keep `--repo-root samples` consistent across `plan`, `validate`, `package`, and `publish` for the same manifest — the manifest path recorded in `manifest-list.json` is resolved against whatever `--repo-root` was passed at `plan` time.

`Microsoft/Microsoft.PowerShell/` uses the version-folder layout that real repository operators are
expected to use (doc/00-overview.md §6.8, doc/05-operation.md §4c): each `PackageVersion` gets its own
folder under `<Publisher>/<PackageIdentifier>/<version>/`, and old version folders are kept rather than
overwritten. The other samples stay flat directly under `manifests/` because they illustrate a single
schema shape or constraint rather than a version-upgrade lifecycle.

| Manifest | Status | `validate` | `package` | Notes |
|---|---|---|---|---|
| `Microsoft/Microsoft.PowerShell/7.6.5/powershell-macos-arm64.yaml` | E2E-runnable, current version | passes | passes (downloads ~68 MB from GitHub, verifies SHA-256) | See below; also demonstrates `Scripts.PreInstall`/`PostInstall` (§5.4.2) |
| `Microsoft/Microsoft.PowerShell/7.6.5/powershell-macos-x64.yaml` | E2E-runnable, current version | passes | passes (downloads ~73 MB from GitHub, verifies SHA-256) | See below; also demonstrates `Scripts.PreInstall`/`PostInstall` (§5.4.2) |
| `Microsoft/Microsoft.PowerShell/7.6.4/powershell-macos-arm64.yaml` | E2E-runnable, previous version | passes | passes (downloads ~68 MB from GitHub, verifies SHA-256) | Same identity as the 7.6.5 manifest above; `publish` treats it as superseded when both are resolved together — see below. Has no `Scripts` block, unlike 7.6.5, to keep both variants (with/without) covered in this directory |
| `Microsoft/Microsoft.PowerShell/7.6.4/powershell-macos-x64.yaml` | E2E-runnable, previous version | passes | passes (downloads ~73 MB from GitHub, verifies SHA-256) | Same as above |
| `contoso-tool-windows-x64.yaml` | E2E-runnable | passes | passes (stages local `RepositoryFiles`, builds a real `.intunewin` — requires a Windows machine/runner) | No external download; `.intunewin` build needs `IntuneWinAppUtil.exe`, downloaded automatically |
| `contoso-tool-windows-arm64.yaml` | E2E-runnable | passes | passes (same as above) | |
| `contoso-tool-windows-file-detection.yaml` | E2E-runnable (file detection example) | passes | passes (same local Windows packaging path) | Shows `Detection.Type: file` with file-version detection and no detection script |
| `contoso-tool-macos-arm64.yaml` | Reference-only (schema example) | passes | **fails** — `Source` points at a fictitious Azure Blob account (`contosopackages`) with a placeholder all-zero `Sha256` | Shows the `azureBlob` shape from [doc/01-manifest-schema.md §5.3](../../doc/01-manifest-schema.md); not meant to resolve |
| `apple-container-macos-arm64.yaml` | Reference-only (intentional failure) | **fails** — `Detection.IncludedApps` is empty | n/a | The Apple Container PKG installs no `.app` bundle, so `IncludedApps` cannot be populated with real values without fabricating a bundle ID. Documents a real Intune macOS-detection limitation; see the comments at the top of the file and [doc/01-manifest-schema.md §5.4](../../doc/01-manifest-schema.md) |

## Why the PowerShell samples work as an E2E fixture

## Windows file-system detection sample

`contoso-tool-windows-file-detection.yaml` packages the same local installer inputs as the Windows script
samples, but its detection rule is evaluated on the managed device rather than read from this repository.
`Detection.Path` is therefore a Windows target-device path, not an input for `--repo-root`. The sample uses
the supported `version` operation; use `exists` when no version comparison is required, omitting both
`Operator` and `ComparisonValue`.

Intune's macOS `Detection.IncludedApps` must list the bundle ID + version of an application the PKG *actually installs* (see [Add an Unmanaged macOS PKG App to Microsoft Intune](https://learn.microsoft.com/intune/app-management/deployment/add-unmanaged-pkg-macos#step-4-%E2%80%93-detection-rules)). A CLI-only PKG like Apple Container's has nothing to point at.

PowerShell's macOS PKG does: its installer places `PowerShell.app` under `/Applications`, with:

- `BundleId`: `com.microsoft.powershell`
- `BundleVersion`: the release version (e.g. `7.6.5`)

(Source: PowerShell/PowerShell `tools/packaging/packaging.psm1`, `New-MacOSLauncher` / `Get-MacOSPackageIdentifierInfo`, and the `MacOSLauncherPlistTemplate` in `packaging.strings.psd1`.) You can confirm this yourself on a Mac after installing the package:

```bash
defaults read /Applications/PowerShell.app/Contents/Info CFBundleIdentifier
defaults read /Applications/PowerShell.app/Contents/Info CFBundleShortVersionString
```

`Requirements.MinimumOSVersion: "14.0"` is deliberate: it is the lowest version PowerShell 7.6 (LTS) supports, and it is also the lowest version that requires the beta-only `v14_0` Graph flag — exercising the `AppType: pkg` / beta path in `MacOsMinimumOperatingSystemTable`. `AppType: lob` cannot use `14.0` or higher (v1.0 has no `v14_0`/`v15_0`/`v26_0` flag).

`Assignments` is intentionally left as `[]` so the file applies unmodified in any tenant. Add your own group before a real (non-dry-run) `publish`:

## Pre/post-install scripts (7.6.5 only)

The 7.6.5 manifests also set `Scripts.PreInstall` / `Scripts.PostInstall`
([doc/01-manifest-schema.md §5.4.2](../../doc/01-manifest-schema.md)), pointing at
`samples/scripts/macos/powershell/preinstall.sh` and `postinstall.sh`. These are `AppType: pkg`-only
Graph properties (`macOSPkgApp.preInstallScript`/`postInstallScript`), so they only apply here, not to
a hypothetical `AppType: lob` variant.

The sample scripts remove a Homebrew-installed `pwsh` and a stale `pwsh` symlink before install, check
for 500 MB of free disk space, and — after install — verify `pwsh` is present and add `/usr/local/bin`
to `/etc/paths.d` so it is on `PATH` in new login shells. They are illustrative, not exhaustively
tested against every macOS configuration; adapt them before using them in a real tenant.

```yaml
Assignments:
  - Target: group
    GroupId: "<your-assignment-group-guid>"
    Intent: required
```

## App categories

No sample declares `Categories`, on purpose: category names are tenant-specific, and a name that does not
exist in the target tenant fails the publish preflight. Omitting `Categories` is also the only value that
touches nothing — the app's current categories are preserved and no category Graph call is made at all.

To try it, create the category in the Intune admin center first, then add it to the app entry:

```yaml
Apps:
  - Platform: macos
    Architecture: arm64
    Categories:
      - Business Apps
```

`Categories: []` is different from omitting the key: it removes every category relationship from the app.
Names are matched against the tenant's `mobileAppCategory.displayName` case-insensitively but otherwise
verbatim, and Relaypublisher never creates, renames, or deletes a category. `validate` cannot check the name
against the tenant; run `publish --dry-run` to see the category plan. See
[doc/05-operation.md §4d](../../doc/05-operation.md#4d-intune-app-categories) and
[doc/01-manifest-schema.md §5.8](../../doc/01-manifest-schema.md).

Note that adding or changing `Categories` changes the manifest-wide `inputHash`, so the next `package` /
`publish` re-packages and re-uploads the content even though only metadata changed.

## Running the PowerShell sample end to end

```bash
CLI_PROJECT="src/IntuneLobPublisher.Cli/IntuneLobPublisher.Cli.csproj"

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  plan --repo-root samples --manifest manifests/Microsoft/Microsoft.PowerShell/7.6.5/powershell-macos-arm64.yaml --output manifest-list.json

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  validate --repo-root samples --manifest-list manifest-list.json

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  package --repo-root samples --manifest-list manifest-list.json --output ./out
```

```powershell
$CliProject = "src/IntuneLobPublisher.Cli/IntuneLobPublisher.Cli.csproj"

dotnet run --configuration Release --project $CliProject -- `
  plan --repo-root samples --manifest manifests/Microsoft/Microsoft.PowerShell/7.6.5/powershell-macos-arm64.yaml --output manifest-list.json

dotnet run --configuration Release --project $CliProject -- `
  validate --repo-root samples --manifest-list manifest-list.json

dotnet run --configuration Release --project $CliProject -- `
  package --repo-root samples --manifest-list manifest-list.json --output ./out
```

For `publish --dry-run` / `publish`, follow [doc/07-local-e2e.md §2 and §4.5-4.6](../../doc/07-local-e2e.md) (local Azure CLI authentication, `--expected-tenant`) and remember to add an `Assignments` entry first.

## Updating the PowerShell sample to a new version

This is the runnable example for the general upgrade procedure in
[doc/05-operation.md §4c](../../doc/05-operation.md#4c-updating-an-existing-app-to-a-new-version): add a
new version folder, do not overwrite the existing one. `Microsoft/Microsoft.PowerShell/7.6.4/` and
`Microsoft/Microsoft.PowerShell/7.6.5/` already demonstrate this side by side — both share the same
`PackageIdentifier + Platform + Architecture`, so they resolve to the same Intune app identity, and
`7.6.4` is what `7.6.5` was copied from before being bumped.

To move the sample to a release newer than 7.6.5, create
`Microsoft/Microsoft.PowerShell/<new-version>/` from a copy of the `7.6.5` files and, consistently in
both the `arm64` and `x64` manifests, replace:

- `PackageVersion` (top level)
- `Source.Tag` (`v<version>`), `Source.AssetName`, `Source.Destination`
- `Source.Sha256` — take it from the release's `hashes.sha256` asset, not from memory; `package` verifies it against the actual download and fails on a mismatch
- `Detection.IncludedApps[0].BundleVersion` — if this is left at the old value, Intune's detection rule keeps checking for the previous bundle version after the update, so already-managed devices can report as not up to date even though new content was published
- `Requirements.MinimumOSVersion` if the new release drops support for macOS 14

Do not change `PackageIdentifier`, `Platform`, `Architecture`, or `DisplayName` — see
[doc/05-operation.md §4c](../../doc/05-operation.md#4c-updating-an-existing-app-to-a-new-version) for why.

To see the version-selection behavior itself, resolve both 7.6.4 and 7.6.5 into the same
`manifest-list.json` and preview a publish:

```bash
dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  plan --repo-root samples \
  --manifest manifests/Microsoft/Microsoft.PowerShell/7.6.4/powershell-macos-arm64.yaml manifests/Microsoft/Microsoft.PowerShell/7.6.5/powershell-macos-arm64.yaml \
  --output manifest-list.json

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  validate --repo-root samples --manifest-list manifest-list.json

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  publish --repo-root samples --manifest-list manifest-list.json --package-dir ./out \
  --expected-tenant <tenant-id> --dry-run
```

`validate` passes — same identity with different `PackageVersion` across version folders is expected, not
a conflict (doc/00-overview.md §6.8). `publish` selects only the higher version and logs the rest, so the
output includes a line like:

```text
Skipping Microsoft.PowerShell macos-arm64 version 7.6.4 from '.../7.6.4/powershell-macos-arm64.yaml' (superseded by version 7.6.5).
```

## Cleanup

`manifest-list.json`, `./out`, and any downloaded `.pkg`/`.intunewin` files are local, regenerable artifacts. Do not commit them; delete them once you are done (see [doc/07-local-e2e.md §5](../../doc/07-local-e2e.md)).
