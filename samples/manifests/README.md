# Sample manifests

This directory mixes two kinds of manifests:

- **E2E-runnable samples**: point at a real, publicly downloadable package. `plan` -> `validate` -> `package` succeed unmodified (`publish` still needs a real tenant and, for `Assignments`, a real group GUID; see [../../doc/07-local-e2e.md](../../doc/07-local-e2e.md)).
- **Reference-only samples**: illustrate a schema shape or a constraint. Some are written to fail `validate` or `package` on purpose, to document a real-world limitation. Read the comments inside the file before assuming it is a bug.

All commands below use `--repo-root samples`, because `RepositoryFiles.Source` / `Icon` paths in these manifests are relative to this `samples/` directory, not the repository root (`scripts/windows/...` here means `samples/scripts/windows/...`, not `<repo-root>/scripts/windows/...`). Keep `--repo-root samples` consistent across `plan`, `validate`, `package`, and `publish` for the same manifest — the manifest path recorded in `manifest-list.json` is resolved against whatever `--repo-root` was passed at `plan` time.

| Manifest | Status | `validate` | `package` | Notes |
|---|---|---|---|---|
| `powershell-macos-arm64.yaml` | E2E-runnable | passes | passes (downloads ~68 MB from GitHub, verifies SHA-256) | See below |
| `powershell-macos-x64.yaml` | E2E-runnable | passes | passes (downloads ~73 MB from GitHub, verifies SHA-256) | See below |
| `contoso-tool-windows-x64.yaml` | E2E-runnable | passes | passes (stages local `RepositoryFiles`, builds a real `.intunewin` — requires a Windows machine/runner) | No external download; `.intunewin` build needs `IntuneWinAppUtil.exe`, downloaded automatically |
| `contoso-tool-windows-arm64.yaml` | E2E-runnable | passes | passes (same as above) | |
| `contoso-tool-macos-arm64.yaml` | Reference-only (schema example) | passes | **fails** — `Source` points at a fictitious Azure Blob account (`contosopackages`) with a placeholder all-zero `Sha256` | Shows the `azureBlob` shape from [doc/01-manifest-schema.md §5.3](../../doc/01-manifest-schema.md); not meant to resolve |
| `apple-container-macos-arm64.yaml` | Reference-only (intentional failure) | **fails** — `Detection.IncludedApps` is empty | n/a | The Apple Container PKG installs no `.app` bundle, so `IncludedApps` cannot be populated with real values without fabricating a bundle ID. Documents a real Intune macOS-detection limitation; see the comments at the top of the file and [doc/01-manifest-schema.md §5.4](../../doc/01-manifest-schema.md) |

## Why the PowerShell samples work as an E2E fixture

Intune's macOS `Detection.IncludedApps` must list the bundle ID + version of an application the PKG *actually installs* (see [Add an Unmanaged macOS PKG App to Microsoft Intune](https://learn.microsoft.com/intune/app-management/deployment/add-unmanaged-pkg-macos#step-4-%E2%80%93-detection-rules)). A CLI-only PKG like Apple Container's has nothing to point at.

PowerShell's macOS PKG does: its installer places `PowerShell.app` under `/Applications`, with:

- `BundleId`: `com.microsoft.powershell`
- `BundleVersion`: the release version (e.g. `7.6.5`)

(Source: PowerShell/PowerShell `tools/packaging/packaging.psm1`, `New-MacOSLauncher` / `Get-MacOSPackageIdentifierInfo`, and the `MacOSLauncherPlistTemplate` in `packaging.strings.psd1`.) You can confirm this yourself on a Mac after installing the package:

```bash
defaults read /Applications/PowerShell.app/Contents/Info CFBundleIdentifier
defaults read /Applications/PowerShell.app/Contents/Info CFBundleShortVersionString
```

`Requirements.MinimumOSVersion: "14.0"` is deliberate: it is the lowest version PowerShell 7.6 (LTS) supports, and it is also the lowest version that requires the beta-only `v14_0` Graph flag — exercising the `AppType: pkg` / beta path in `MacOsMinimumOperatingSystemTable`. `AppType: lob` cannot use `14.0` or higher (v1.0 has no `v14_0`/`v15_0` flag).

`Assignments` is intentionally left as `[]` so the file applies unmodified in any tenant. Add your own group before a real (non-dry-run) `publish`:

```yaml
Assignments:
  - Target: group
    GroupId: "<your-assignment-group-guid>"
    Intent: required
```

## Running the PowerShell sample end to end

```bash
CLI_PROJECT="src/IntuneLobPublisher.Cli/IntuneLobPublisher.Cli.csproj"

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  plan --repo-root samples --manifest manifests/powershell-macos-arm64.yaml --output manifest-list.json

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  validate --repo-root samples --manifest-list manifest-list.json

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  package --repo-root samples --manifest-list manifest-list.json --output ./out
```

```powershell
$CliProject = "src/IntuneLobPublisher.Cli/IntuneLobPublisher.Cli.csproj"

dotnet run --configuration Release --project $CliProject -- `
  plan --repo-root samples --manifest manifests/powershell-macos-arm64.yaml --output manifest-list.json

dotnet run --configuration Release --project $CliProject -- `
  validate --repo-root samples --manifest-list manifest-list.json

dotnet run --configuration Release --project $CliProject -- `
  package --repo-root samples --manifest-list manifest-list.json --output ./out
```

For `publish --dry-run` / `publish`, follow [doc/07-local-e2e.md §2 and §4.5-4.6](../../doc/07-local-e2e.md) (local Azure CLI authentication, `--expected-tenant`) and remember to add an `Assignments` entry first.

## Updating the PowerShell sample to a newer release

Replace, consistently, in both `powershell-macos-arm64.yaml` and `powershell-macos-x64.yaml`:

- `PackageVersion` (top level)
- `Source.Tag` (`v<version>`), `Source.AssetName`, `Source.Destination`
- `Source.Sha256` — take it from the release's `hashes.sha256` asset, not from memory; `package` verifies it against the actual download and fails on a mismatch
- `Detection.IncludedApps[0].BundleVersion`
- `Requirements.MinimumOSVersion` if the new release drops support for macOS 14

## Cleanup

`manifest-list.json`, `./out`, and any downloaded `.pkg`/`.intunewin` files are local, regenerable artifacts. Do not commit them; delete them once you are done (see [doc/07-local-e2e.md §5](../../doc/07-local-e2e.md)).
