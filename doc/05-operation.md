# Operation Guide

This guide covers the setup and daily operation required to publish Intune LOB apps with Relaypublisher.

The Japanese translation is available in [05-operation_ja.md](05-operation_ja.md).

For a complete local terminal procedure, see [07-local-e2e.md](07-local-e2e.md).

## 0. Tool Installation and Version Control

Relaypublisher is distributed as a NuGet global tool. The same package version is published to three
feeds, so pick the one your environment can reach:

| Feed | Intended consumer |
| --- | --- |
| nuget.org | General users. This is the default source, so no extra flag is needed. |
| GitHub Packages | Users working from this repository. Always requires a GitHub token with `read:packages`, even for a public package. |
| Azure Artifacts | Internal CI or closed networks. Requires access to the organization's feed. |

Install from nuget.org:

```bash
dotnet tool install --global relaypublisher
```

Install from GitHub Packages. `--add-source` only supplies the feed URL, so the source must be
registered with credentials first - GitHub Packages returns 401 for an anonymous NuGet request even
when the package is public:

```bash
# Provide the token through the environment; do not paste it on the command line.
export GH_PACKAGES_TOKEN="<github-pat-with-read-packages>"

dotnet nuget add source "https://nuget.pkg.github.com/<owner>/index.json"   --name relaypublisher-github   --username "<github-username>"   --password "$GH_PACKAGES_TOKEN"   --store-password-in-clear-text

dotnet tool install --global relaypublisher --add-source relaypublisher-github
```

PowerShell 7:

```powershell
$env:GH_PACKAGES_TOKEN = '<github-pat-with-read-packages>'

dotnet nuget add source "https://nuget.pkg.github.com/<owner>/index.json" `
  --name relaypublisher-github `
  --username "<github-username>" `
  --password $env:GH_PACKAGES_TOKEN `
  --store-password-in-clear-text

dotnet tool install --global relaypublisher --add-source relaypublisher-github
```

`--store-password-in-clear-text` writes the token into the user-level *NuGet.config* in plain text.
It is required on Linux and macOS because NuGet's encrypted credential store is Windows-only. Treat
that file as a secret, or drop the flag on Windows. Remove the source with
`dotnet nuget remove source relaypublisher-github` when it is no longer needed.

Install from Azure Artifacts. Install the credential provider first, then authenticate once:

```bash
dotnet tool install --global Microsoft.Artifacts.CredentialProvider.NuGet.Tool   --source https://api.nuget.org/v3/index.json

dotnet nuget add source "<azure-artifacts-feed-v3-index-url>" --name relaypublisher-ado

dotnet tool install --global relaypublisher --add-source relaypublisher-ado --interactive
```

`--interactive` triggers the sign-in prompt on first use. Later commands reuse the cached session
token and do not need the flag.

Update:

```bash
dotnet tool update --global relaypublisher
```

Pin/rollback to a specific version:

```bash
dotnet tool update --global relaypublisher --version <x.y.z>
```

Verify installed version:

```bash
dotnet tool list --global | grep relaypublisher
```

Release version policy:

- Published package ID: `relaypublisher`
- Command name: `relaypublisher`
- Package version source: Git tag `vX.Y.Z` injected by CI (`-p:Version=X.Y.Z`)
- Release flow: pushing a `v*` tag onto main creates a **draft** GitHub release with the `.nupkg`,
  the self-contained single-file apps (`win-x64`, `win-arm64`, `osx-arm64`) and `SHA256SUMS.txt`.
  Publishing that draft release by hand is what pushes the package to the three feeds.
  See [03-ci-github-actions.md](03-ci-github-actions.md) section 12a.
- The single-file apps are neither code-signed nor notarized. macOS shows a Gatekeeper warning.

## 1. Microsoft Entra App Registration

Create one Microsoft Entra application registration for the CI publisher identity.

Required configuration:

- Account type: single tenant for the target tenant.
- Microsoft Graph application permission: `DeviceManagementApps.ReadWrite.All`. It must be added under
  **Application permissions**, not **Delegated permissions** - the portal lists the same name under
  both. Relaypublisher signs in as a service principal, and an app-only token carries only application
  permissions (`roles` claim); a delegated permission produces a 403 even after admin consent. See
  [06-troubleshooting.md](06-troubleshooting.md) section 2a.
- Admin consent: granted by a tenant administrator before the first production publish.
- No client secret is required for the recommended CI setup. Use workload identity federation instead.

Operational notes:

- Store the application client ID in the CI secret or variable named `AZURE_CLIENT_ID`.
- Store the tenant ID in `AZURE_TENANT_ID`.
- If Azure Blob sources are used, also store the subscription ID in `AZURE_SUBSCRIPTION_ID` and grant the CI identity read access to the package storage scope.
- Use `publish --expected-tenant <tenant-id>` so a token from the wrong tenant fails before any write.
- Set `AZURE_TOKEN_CREDENTIALS` (see section 3) so `DefaultAzureCredential` resolves deterministically to this identity. `--expected-tenant` cannot detect a wrong identity in the same tenant.

## 2. Federated Credentials

Federated credentials allow CI to exchange a runner-issued OIDC token for a Microsoft identity platform access token. Configure them on the same Entra app registration used for Graph publishing.

Use the Microsoft-recommended token exchange audience `api://AzureADTokenExchange`. The federated credential
`issuer`, `subject`, and `audience` values must match the incoming OIDC token exactly, including case. Wildcard
matching is not supported for the standard credential form.

For common setup failures after configuration, see the [Troubleshooting Guide](06-troubleshooting.md), especially
[`TenantMismatchException`](06-troubleshooting.md#2-tenantmismatchexception) and
[`Azure Blob Source Cannot Be Downloaded`](06-troubleshooting.md#5-azure-blob-source-cannot-be-downloaded).

### GitHub Actions

Use a GitHub Actions federated credential scoped to the protected production environment.

Recommended subject shape:

```text
repo:<owner>/<repo>:environment:production
```

For GitHub Actions, set the issuer to `https://token.actions.githubusercontent.com/` and the audience to
`api://AzureADTokenExchange`. The subject must use the exact owner, repository, and environment names from the
workflow. The sample uses `environment: production`, so the subject is
`repo:<owner>/<repo>:environment:production`.

Required workflow settings:

- The publish job has `permissions: id-token: write`.
- The publish job uses `environment: production`.
- The workflow passes `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and `AZURE_SUBSCRIPTION_ID` to the Azure login action.
- Pull request jobs do not receive `id-token: write` or production secrets.

If `azureBlob` sources are used during packaging, the Windows package job also needs OIDC login and the storage reader role.

### Azure Pipelines

Use an Azure Resource Manager service connection configured with workload identity federation.

Recommended setup:

- Create or convert a service connection to workload identity federation.
- Do not grant broad access to all pipelines unless the project policy requires it.
- Authorize only the pipeline that publishes Intune apps.
- Use an environment named `production` with an Exclusive Lock check to serialize publish runs.
- Pass `<tenant-id>` from a protected variable group to `publish --expected-tenant`.

Use the issuer and subject identifier generated by the Azure DevOps workload identity federation service connection
when creating the Entra federated credential; do not substitute a GitHub issuer or invent a subject. Set the
audience to `api://AzureADTokenExchange`, and copy the generated issuer and subject exactly. The service connection
must be authorized for this pipeline only.

### Token acquisition after CI login

`azure/login` in GitHub Actions and `AzureCLI@2` with the workload identity service connection in Azure Pipelines
establish the Azure CLI login on the runner. Relaypublisher's `DefaultAzureCredential` then requests the Graph
scope `https://graph.microsoft.com/.default` from whichever credential source it resolves first — the Azure CLI
login is only one candidate in that chain, not a guaranteed one, so set `AZURE_TOKEN_CREDENTIALS` in the job
environment right after the login step (see section 3). A successful Azure CLI login alone is not a substitute
for the required Graph application permission and admin consent, and `publish` prints a warning if
`AZURE_TOKEN_CREDENTIALS` is not set and logs the acquired identity's `appid`/`idtyp`/`roles` on the first Graph
call.

Bash / zsh, after `azure/login`:

```bash
export AZURE_TOKEN_CREDENTIALS=AzureCliCredential
```

PowerShell 7, after `AzureCLI@2`:

```powershell
$env:AZURE_TOKEN_CREDENTIALS = "AzureCliCredential"
```

For common failures, see [06-troubleshooting.md](06-troubleshooting.md):

- OIDC or tenant mismatch: [TenantMismatchException](06-troubleshooting.md#2-tenantmismatchexception)
- Missing GitHub Release token: [GitHub Release Token Is Missing](06-troubleshooting.md#4-github-release-token-is-missing)
- Azure Blob access or download failure: [Azure Blob Source Cannot Be Downloaded](06-troubleshooting.md#5-azure-blob-source-cannot-be-downloaded)

## 3. Source Provider Environment Variables

Source provider authentication is controlled by each manifest item's `Auth` block.

| Source type | `Auth.Type` | Required environment variable | Notes |
|---|---|---|---|
| `publicHttp` | omitted or `none` | none | The download is anonymous. |
| `githubRelease` | `token` | the value of `Auth.SecretName`, commonly `GH_RELEASE_PAT` | The token is read from the environment variable with the same name. |
| `azureBlob` | `workloadIdentity` | `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and CI OIDC variables | Access is granted through the federated CI identity. |

### Credential selection (`AZURE_TOKEN_CREDENTIALS`)

`AZURE_TOKEN_CREDENTIALS` is not scoped to one source provider — it is a process-wide environment
variable that `Azure.Identity` (1.15.0+) reads internally, so setting it once governs every
`DefaultAzureCredential` construction in the process: the Graph publish path (`publish`) and every
`azureBlob` download during `package` / `plan`. Recommended value for CI runners and CLI-login-based
local use is `AzureCliCredential`, which restricts the chain to only the Azure CLI login. Setting it is
optional — `DefaultAzureCredential` still works without it — but recommended, because an unpinned chain
can silently resolve to a different signed-in identity (doc/00-overview.md section 6.19,
[06-troubleshooting.md](06-troubleshooting.md) section 2a). `publish` warns when it is not set.

```bash
export AZURE_TOKEN_CREDENTIALS=AzureCliCredential
```

```powershell
$env:AZURE_TOKEN_CREDENTIALS = "AzureCliCredential"
```

Example manifest fragment:

```yaml
ExternalFiles:
  - Type: githubRelease
    Owner: <owner>
    Repository: <repository>
    Tag: <tag>
    AssetName: <asset-name>
    Destination: bin/app.exe
    Sha256: "<sha256>"
    Auth:
      Type: token
      SecretName: GH_RELEASE_PAT
```

In CI, map the secret to the exact environment variable name:

```powershell
$env:GH_RELEASE_PAT = "<token>"
```

```bash
export GH_RELEASE_PAT="<token>"
```

## 4. Daily Commands

Run build and tests before publishing:

```powershell
dotnet build IntuneLobPublisher.slnx --configuration Release
dotnet test IntuneLobPublisher.slnx --configuration Release --no-build
```

```bash
dotnet build IntuneLobPublisher.slnx --configuration Release
dotnet test IntuneLobPublisher.slnx --configuration Release --no-build
```

Resolve the manifest set once:

```powershell
relaypublisher plan --base-ref <base-ref> --output manifest-list.json
```

```bash
relaypublisher plan --base-ref <base-ref> --output manifest-list.json
```

Validate the selected manifests:

```powershell
relaypublisher validate --manifest-list manifest-list.json
```

```bash
relaypublisher validate --manifest-list manifest-list.json
```

Package Windows Win32 apps on Windows:

```powershell
relaypublisher package --manifest-list manifest-list.json --output ./out
```

On non-Windows runners, use `--stage-only` for Windows entries to skip `.intunewin` generation while still
validating staging. macOS entries do not need `--stage-only` on a non-Windows runner - macOS packaging has
no external tool step, so plain `package` (without `--stage-only`) already stages the `.pkg`, verifies its
checksum, and writes `package-metadata.json` there. `--stage-only` applies uniformly to every platform in
the manifest list, though: if a mixed Windows/macOS list needs it for the Windows entries, macOS entries in
that same run are staged but their `package-metadata.json` is *not* written either, so a later `publish`
against that output fails with missing package metadata for the macOS entries too. Run `package` again
without `--stage-only` for the macOS entries (or split them into a separate `package` invocation) before
publishing:

```bash
relaypublisher package --manifest-list manifest-list.json --output ./out --stage-only
```

Preview publish changes without writing to Intune:

```powershell
relaypublisher publish --manifest-list manifest-list.json --package-dir ./out `
  --expected-tenant <tenant-id> --dry-run
```

Publish to Intune:

```bash
relaypublisher publish --manifest-list manifest-list.json --package-dir ./out \
  --expected-tenant <tenant-id>
```

## 4a. Package Input and CI Artifact Handoff

The `package` command obtains manifest inputs through the configured source provider and writes the resulting package files under `--output`:

- `publicHttp` downloads anonymously.
- `githubRelease` with `Auth.Type: token` reads the environment variable named by `Auth.SecretName`.
- `azureBlob` requires `Auth.Type: workloadIdentity`; local `DefaultAzureCredential` can use an Azure CLI login, while CI uses its workload identity login.

For a private GitHub Release asset, set `Auth.Type: token` and the manifest's secret variable before packaging:

```bash
export GH_RELEASE_PAT="<token>"
```

```powershell
$env:GH_RELEASE_PAT = "<token>"
```

Windows packaging produces the `.intunewin` output required by `publish`. macOS packaging produces the staged `.pkg` and `package-metadata.json`. Do not use output from `package --stage-only` for a Windows publish because it does not contain the final `.intunewin` or package metadata.

In CI, the package job uploads the package directory as the `intunewin-packages` artifact. The publish job must download that artifact and pass the downloaded directory to `--package-dir`; it must also reuse the exact `manifest-list.json` produced by `plan`.

For a macOS `.pkg`, packaging follows this order:

1. Download the source and verify the manifest `Source.Sha256` against the downloaded bytes.
2. Inspect the staged XAR archive and record the declared application bundle IDs and versions.
3. Compare the inspection result with `Detection.IncludedApps` and `Detection.PrimaryBundleId`.
4. Write package metadata and the inspection report, including the source SHA and exact CLI version used.

The inspection is not performed by `validate`. `validate` performs schema and other static repository checks only; it does not download a source or inspect package contents. A package source that requires credentials is therefore exercised by `package`, not by `validate`.

The publish job must not redownload a private source. Before it contacts Graph for a write, `publish` rehashes every staged macOS `.pkg`, re-inspects its XAR contents, and compares the result with the manifest, package metadata, and inspection report. It then completes this preflight for **every selected entry**. A warning rejected by the operator, a hard error, a stale report, or any SHA mismatch stops the batch before the first Graph write. The package and publish jobs must use the same exact CLI version; record and verify it with `relaypublisher --version` in both jobs.

GitHub Actions:

```yaml
- uses: actions/download-artifact@v4
  with:
    name: manifest-list

- uses: actions/download-artifact@v4
  with:
    name: intunewin-packages
    path: ./out

- run: >
    relaypublisher publish
    --manifest-list manifest-list.json
    --package-dir ./out
    --expected-tenant "<tenant-id>"
```

Azure Pipelines:

```yaml
- download: current
  artifact: manifest-list

- download: current
  artifact: intunewin-packages

- script: >
    relaypublisher publish
    --manifest-list '$(Pipeline.Workspace)/manifest-list/manifest-list.json'
    --package-dir '$(Pipeline.Workspace)/intunewin-packages'
    --expected-tenant '<tenant-id>'
```

## 4b. macOS Notes

macOS support (doc/00-overview.md §6.13) has two `AppType` values with different Graph and operational
characteristics:

- `AppType: pkg` (default, `macOSPkgApp`): unsigned packages allowed, up to 8 GB, no `Intent: uninstall`.
  Every Graph call for this app - create/update, content upload, notes/committedContentVersion patches,
  and its appearance in app resolution - goes through Graph **beta**, because `macOSPkgApp` does not
  exist in v1.0. There is no operator action needed for this; it is handled internally, but it means a
  tenant-side beta API outage affects `pkg` publishes specifically.
- `AppType: lob` (`macOSLobApp`): requires Developer ID Installer signing, capped at 2 GB, requires a
  top-level `Icon`, and stays on Graph **v1.0**. Because v1.0's `minimumSupportedOperatingSystem` has no
  flag past macOS 13, a `lob` manifest entry with `Requirements.MinimumOSVersion` set to macOS 14 or
  later fails at `publish` (and in `--dry-run`) with `UnsupportedMacOsVersionException` pointing at
  `AppType: pkg` as the fix - it is not caught by `validate`, since the constraint is a Graph API-version
  limitation rather than a manifest schema rule.
- `.pkg` content is encrypted in-process at publish time (no packaging-time tool like IntuneWinAppUtil
  exists for macOS), so unlike Windows there is no separate "regenerate the encrypted package" step to
  re-run after a content change; re-running `publish` re-encrypts the currently staged `.pkg`.
- `AppType: pkg` only: an optional `Scripts.PreInstall` / `Scripts.PostInstall` block maps to Graph
  `preInstallScript` / `postInstallScript` (doc/01-manifest-schema.md §5.4.2). Requires the Intune
  management agent for macOS **2309.007 or later** on the device. A non-zero pre-install exit code fails
  the app install (retried at the next device check-in); a post-install failure is not reported at all -
  the app still shows "success". Script content is not part of the deterministic inputHash, so editing a
  script and re-running `publish` updates it without re-uploading the (possibly multi-GB) `.pkg`.

### 4b.1. Primary bundle inspection and warning policy

`Detection.PrimaryBundleId` is optional. When it is omitted, the first `IncludedApps` entry remains the declared primary. When it is present, an ordinal exact match or a segment-boundary prefix match must select one manifest entry; payload mapping moves that entry to the first position without changing the manifest file.

The XAR inspection is a semantic check of the package contents. It does not rewrite `IncludedApps` or automatically remove an updater. The following conditions are semantic warnings and can be acknowledged with `--force`:

| Condition | Default behavior |
|---|---|
| The package declares multiple application bundles while `PrimaryBundleId` is omitted | Show the detected bundle list and explain that the first declared entry is used. |
| The declared `PrimaryBundleId` is absent from the package | Show the detected bundle list and require operator confirmation. |
| The package contains an application bundle that is not listed in `IncludedApps` | Show the unlisted bundle and require operator confirmation. |

In an interactive TTY, each semantic warning is followed by a `[y/N]` confirmation; the default is to stop. In a non-interactive environment, the command fails unless `--force` is supplied. `--force` records the acknowledgement and bypasses only these semantic warnings. It never bypasses a schema error, an ambiguous primary selection, a missing or malformed XAR entry, an unsupported archive, a source SHA mismatch, a stale/tampered artifact, or a Graph/tenant safety check.

If `PrimaryBundleId` matches more than one discovered bundle, the selection is ambiguous and is a hard error. If the archive contains no usable application bundle, or its XAR/XML cannot be safely parsed, it is also a hard error. Fix the manifest or source and rerun `package`; do not edit the report by hand.

For `AppType: lob`, `BundleVersion` supplies the short bundle version used for Graph `buildNumber`, while `BundleBuildVersion` supplies the build version used for Graph `versionNumber`. Keep both values aligned with the bundle metadata when the package distinguishes `CFBundleShortVersionString` and `CFBundleVersion`; do not copy one value into both fields by default. The selected primary is also mapped to the top-level LOB bundle fields and is first in `childApps`.

## 4c. Updating an Existing App to a New Version

App identity is `PackageIdentifier + Platform + Architecture` and does not include the version
(doc/00-overview.md §6.1/§6.2). Publishing a new `PackageVersion` under the same identity therefore
updates the existing Intune app in place - the app ID, its assignments, and the devices it is already
installed on are all preserved. This is the only supported way to move an app to a new package version;
Relaypublisher does not create a second app or set up an Intune supersedence relationship between
versions.

Steps:

1. Create `manifests/<Publisher>/<PackageIdentifier>/<new-version>/` and copy the manifest from the
   previous version folder into it. Keep old version folders - they are not deleted, and function as
   history (doc/00-overview.md §6.8).
2. Update the top-level `PackageVersion`.
3. Update the version-dependent `Source` fields for the new release (for example `Tag`, `AssetName`,
   `BlobName`, `Destination`). Take `Sha256` from the new release's published checksum, not from memory
   or a previous manifest - `package` downloads the asset and fails if it does not match.
4. macOS only: update `Detection.IncludedApps[].BundleVersion` to the new release's short bundle version. For
   `AppType: lob`, update `BundleBuildVersion` as well when the package's `CFBundleVersion` changes. If either
   value is left stale, Intune's detection rule can keep checking for the previous version after the update,
   which can make already-managed devices report as not installed/not up to date even though the new content
   was published.
5. Windows only: check `SetupFile`, `RepositoryFiles`, and any detection script for version references
   that also need to change.
6. Do not change `PackageIdentifier`, `Platform`, `Architecture`, or `DisplayName` when bumping a
   version. Changing any of these breaks identity resolution (doc/00-overview.md §6.1): `publish` will
   not find the existing app and creates a new one instead, leaving the old app and its assignments
   behind un-migrated.
7. Run the normal flow - `plan` selects the new manifest, and if an older version of the same manifest
   is still present in the resolved set, only the highest version is published; the rest are logged as
   superseded (doc/00-overview.md §6.8):

```powershell
relaypublisher plan --base-ref <base-ref> --output manifest-list.json
relaypublisher validate --manifest-list manifest-list.json
relaypublisher package --manifest-list manifest-list.json --output ./out
relaypublisher publish --manifest-list manifest-list.json --package-dir ./out `
  --expected-tenant <tenant-id> --dry-run
```

```bash
relaypublisher plan --base-ref <base-ref> --output manifest-list.json
relaypublisher validate --manifest-list manifest-list.json
relaypublisher package --manifest-list manifest-list.json --output ./out
relaypublisher publish --manifest-list manifest-list.json --package-dir ./out \
  --expected-tenant <tenant-id> --dry-run
```

8. Review the `--dry-run` output before running `publish` without `--dry-run`. Behind the scenes,
   `publish` resolves the existing app from notes metadata and applies the downgrade guard (§6.8).
   Before deciding whether the `inputHash` allows a skip, it reads the app's `publishingState`: an app in
   `processing` is polled until `published`, while an app in `notPublished` recovers its sole interrupted
   content version instead of creating a second one. A version with no files gets its first file. When stale
   files exist, exactly one compatible uncommitted file in a supported terminal failure state is renewed and
   reused; zero matches or multiple files fail without adding another file. A sole committed file
   with the same hash resumes activation. Unknown or ambiguous states fail without deleting the app or
   committed content. A `published` app with the same hash skips the content upload
   (§6.7); otherwise the tool uploads and commits a new content version. Content activation completes before
   existing-app metadata, category, or assignment writes, because Graph rejects those writes while the app is
   not published. If the state polling times out, rerun the same publish after Intune finishes processing -
   do not delete and recreate the app. Once `committedContentVersion` is patched the new content is live and
   this tool cannot revert it (§6.10) - rolling back means publishing the previous version's manifest again
   with `--allow-downgrade`.

See [samples/manifests/README.md](../samples/manifests/README.md#updating-the-powershell-sample-to-a-new-version)
for a runnable example that adds a new version folder next to an existing one.

## 4d. Intune App Categories

An app entry can declare the Intune app categories it belongs to. Categories are tenant-wide
`mobileAppCategory` resources; Relaypublisher only synchronizes the *relationship* between an app and an
existing category. It never creates, renames, or deletes a category, so create the category in the Intune
admin center first.

```yaml
Apps:
  - Platform: windows
    Architecture: x64
    Categories:
      - Business Apps
      - Productivity
```

| Manifest | Effect |
|---|---|
| `Categories` omitted | The app's current categories are left untouched. No category Graph call is made at all. |
| `Categories: []` | Every category relationship on the app is removed. |
| One or more names | The listed set becomes the app's exact category set; anything else is removed. |

Operational notes:

- Names are matched against `mobileAppCategory.displayName`, case-insensitively but otherwise verbatim: no
  trimming and no Unicode normalization. `validate` never contacts the tenant, so a **name that does not
  exist in the tenant is only detected during `publish` or `publish --dry-run`**, in the preflight that runs
  before the first write for that app. A missing or ambiguous name fails that manifest entry only; the rest
  of the batch continues and a rerun converges.
- `publish --dry-run` reads the tenant catalog and the app's current categories and prints the
  add/keep/remove plan without writing anything. A new app shows the placeholder id `(new app)`.
- Content is activated before an existing app's metadata and category relationships are written. Graph rejects
  these writes while `publishingState` is not `published`; if Intune reports `processing`, Relaypublisher waits
  using the configured publishing-state interval and timeout. A `notPublished` app reuses its sole interrupted
  content version, renewing a compatible uncommitted file, or resuming activation of a committed file for the same
  `inputHash`, so rerunning the same command repairs an app that was never activated.
- The result file (`--result-file`) gains one additive field per entry, `categoryOutcome`: `applied`,
  `unchanged`, `not-requested`, or null when publishing never reached the category step. Per-category
  detail is in the console output and logs.
- The `inputHash` covers the whole manifest, so **changing only `Categories` can still trigger repackaging
  and a content re-upload**. Manifests that do not declare `Categories` keep their previous hashes exactly.
- Once any manifest declares `Categories`, **keep every CLI that touches the repository on the same
  version**. An older CLI ignores unknown manifest fields, computes the older hash, and alternating versions
  makes `inputHash` oscillate and re-upload content on every run.
- No extra Graph permission is required: `DeviceManagementApps.ReadWrite.All` already covers category
  relationships. A 403 while listing the tenant catalog is identity-wide and stops the whole batch, just like
  a 403 on the app listing (see [06-troubleshooting.md](06-troubleshooting.md) section 2a).

## 5. Exit Codes

| Exit code | Meaning | Operator action |
|---|---|---|
| `0` | The command completed successfully. | Continue the CI workflow. |
| `1` | Validation, packaging, authentication, tenant, Graph, or publish failure. | Read the error message, fix the manifest or environment, and rerun. |
| `2` | Reserved for not-yet-implemented command paths. | Treat as a tool implementation gap, not an operator retry condition. |

## 6. Workflow setup checklist

Complete this checklist after copying a reference sample from `workflows/`. The sample files are not active until
they are copied into the target repository.

This section covers the *consumer* workflows that publish Intune apps. Relaypublisher's own CI/CD lives in
`.github/workflows/` and is already active in this repository; its setup checklist is in
"Relaypublisher release pipeline" below.

### Common

- [ ] The target repository contains the copied workflow and the manifest / script paths used by its triggers.
- [ ] The Entra app has `DeviceManagementApps.ReadWrite.All` application permission and admin consent.
- [ ] `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and (for Azure login) `AZURE_SUBSCRIPTION_ID` are stored in protected CI configuration.
- [ ] The expected tenant value is protected and passed to `publish --expected-tenant <tenant-id>`.
- [ ] The federated credential issuer, subject, and audience exactly match the CI token.
- [ ] The exact same Relaypublisher CLI version is pinned in `plan`, `validate`, `package`, and `publish`; each job logs `relaypublisher --version`.
- [ ] Semantic PKG warnings fail non-interactive jobs unless the protected workflow explicitly supplies `--force`; `--force` is not used to bypass hard errors.

### GitHub Actions

- [ ] Copy `workflows/github-actions/publish-intune-apps.yml` to `.github/workflows/publish-intune-apps.yml`.
- [ ] Create the `production` environment and protect it with the required reviewers or policy.
- [ ] Give `id-token: write` only to jobs that need OIDC; PR validation jobs must not receive it.
- [ ] Configure the GitHub federated credential with issuer `https://token.actions.githubusercontent.com/`, subject `repo:<owner>/<repo>:environment:production`, and audience `api://AzureADTokenExchange`.
- [ ] If a manifest uses `githubRelease`, add the secret named by `Auth.SecretName` (for example `GH_RELEASE_PAT`) and map it only to the package job.
- [ ] If a manifest uses `azureBlob`, allow OIDC login and the storage reader role on the package job.
- [ ] The publish job consumes the package artifact produced by the pinned package job, rehashes/re-inspects it, and runs all-entry preflight before any Graph write.

### Azure Pipelines

- [ ] Copy `workflows/azure-pipelines/azure-pipelines.yml` to the target repository root as `azure-pipelines.yml`.
- [ ] Create or select an Azure Resource Manager service connection using workload identity federation, and authorize only this pipeline.
- [ ] Configure the Entra federated credential from the service connection's generated issuer and subject, with audience `api://AzureADTokenExchange`.
- [ ] Create the `production` environment and configure an Exclusive Lock check.
- [ ] Add the protected variable group used by the sample, including `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, and the expected tenant value.
- [ ] If a manifest uses `githubRelease`, map the secret named by `Auth.SecretName` (for example `GH_RELEASE_PAT`) only to the package job.
- [ ] If a manifest uses `azureBlob`, ensure the package job uses the authorized service connection and has the storage reader role.
- [ ] The publish job consumes the package artifact produced by the pinned package job, rehashes/re-inspects it, and runs all-entry preflight before any Graph write.

### Relaypublisher release pipeline

This applies to the Relaypublisher repository itself, not to consumer repositories.

- [ ] Create the `release` GitHub environment and store the publishing secrets on it, not on the repository.
- [ ] `NUGET_API_KEY` holds a nuget.org key scoped to package publish only.
- [ ] `AZURE_ARTIFACTS_FEED_URL` holds the feed's v3 `index.json` URL. Keep it a secret so the URL never
      reaches the workflow logs.
- [ ] Create a user-assigned managed identity and copy its client ID, tenant ID, and subscription ID into
      `AZURE_ARTIFACTS_CLIENT_ID`, `AZURE_ARTIFACTS_TENANT_ID`, and `AZURE_ARTIFACTS_SUBSCRIPTION_ID`.
- [ ] Configure a federated identity credential on that managed identity that trusts this repository's
      `release` environment, with audience `api://AzureADTokenExchange`.
- [ ] In Azure DevOps, add the managed identity to the target project's **Contributors** group so it can
      push to the feed.
- [ ] Confirm `release-publish.yml` is the only workflow with `packages: write` and `id-token: write`.
- [ ] Confirm `ci.yml` references no secrets, so pull requests from forks still pass.

## 7. Production Checklist

- `validate` passes for the full repository's static schema and repository checks; it is not treated as a PKG-content inspection.
- `plan` output is stored as `manifest-list.json` and reused by later jobs.
- The package job does not recompute changed manifests.
- Every macOS package source SHA is verified before XAR inspection, and every publish rehashes/re-inspects the staged artifact.
- All selected entries complete preflight before the first Graph write; a rejected warning or hard error leaves Graph unchanged.
- The publish job runs with a protected environment and serialized execution.
- `publish` always uses `--expected-tenant`.
- The CLI version is pinned identically across all jobs and the version is visible in the job log and package metadata.
- GitHub release tokens and other source provider secrets are passed only to jobs that need them.
- No authorization header, token, signed package URI, or secret value is written to logs or artifacts.
