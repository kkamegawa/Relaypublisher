# Local E2E Test Guide

This guide describes how to run Relaypublisher end to end from a local terminal. Use a dedicated test tenant, test app identity, and test assignment group. A real `publish` changes Intune, so always run `--dry-run` first and confirm the target tenant.

The Japanese translation is available in [07-local-e2e_ja.md](07-local-e2e_ja.md).

## 1. Prerequisites

Run the commands from the repository root. The local workflow requires:

- .NET SDK 10.0 or later compatible with `global.json`.
- Azure CLI (`az`).
- Bash or zsh on macOS/Linux, or PowerShell 7 on Windows.
- A valid manifest whose repository files and package sources are available locally.
- A test Microsoft Entra tenant, Intune permission, and assignment group.
- A Windows machine or runner for generating Windows `.intunewin` packages.
- A deterministic macOS XAR fixture for parser/CLI checks, plus a disposable real `.pkg` source for tenant verification.
- A protected manual-run environment with an approval gate, an expected-tenant value, and an audit trail for `--force`.

The repository sample manifests are reference material rather than guaranteed E2E fixtures, with one exception: the PowerShell macOS manifests under `samples/manifests/Microsoft/Microsoft.PowerShell/7.6.4/` and `7.6.5/` (`powershell-macos-arm64.yaml` / `-x64.yaml`, four files total) point at real, publicly downloadable packages and run `plan` -> `validate` -> `package` unmodified. The other samples intentionally fail validation, or refer to package sources that do not resolve, to document schema shapes or real-world constraints rather than to be run as-is. See [samples/manifests/README.md](../samples/manifests/README.md) for which sample is which before assuming a failure is a bug. Use an organization-specific test manifest with real package inputs for anything beyond a quick local smoke test.

## 2. Local Azure CLI authentication

Relaypublisher uses `DefaultAzureCredential` for Microsoft Graph and Azure Blob access. For an app-only local E2E test, sign in to Azure CLI as the service principal for the Microsoft Entra app registration. `az login --tenant <tenant-id>` by itself performs an interactive user login and does not test the app registration's application permissions. The credential chain must also be pinned (see "Pin the credential for the run" below) for the local run to actually exercise this service principal instead of some other signed-in identity.

Before signing in, configure the app registration as follows:

- Record the application (client) ID and tenant ID.
- Grant the Microsoft Graph permission `DeviceManagementApps.ReadWrite.All` under **Application permissions** - not the identically named **Delegated** entry - and obtain admin consent. A delegated permission never appears in an app-only token, so it produces a 403 that looks like a missing permission even though the portal shows it as granted. See [06-troubleshooting.md](06-troubleshooting.md) section 2a.
- Prepare either a client secret or a PEM certificate registered on the app. A certificate is preferable when the local environment can protect its private key.
- If the selected manifest uses Azure Blob, grant the service principal `Storage Blob Data Reader` on the required storage scope.

The app-only Graph token uses the permissions preconfigured on the app registration through the `.default` scope. `DefaultAzureCredential` *can* then use the Azure CLI service-principal login, but only when the credential chain is pinned - unpinned, a signed-in Visual Studio, VS Code or broker identity can be tried first and silently win instead, producing a 403 that looks identical to a missing permission. See [06-troubleshooting.md](06-troubleshooting.md) section 2a and "Pin the credential for the run" below.

This permission is required for `publish --dry-run` as well, not only for a real publish. A dry-run resolves the existing Intune app before it reports what would change, so it calls `GET /deviceAppManagement/mobileApps` first and fails with 403 without the permission. Do not treat `--dry-run` as a way to rehearse the pipeline before permissions are granted.

Bash/zsh with a client secret. Use the portable prompt form below: Bash's `read -p` option is not compatible with zsh, where `-p` means reading from a coprocess.

```bash
APP_ID="<application-client-id>"
TENANT_ID="<tenant-id>"
printf '%s' 'Client secret: ' >&2
IFS= read -r -s CLIENT_SECRET
printf '\n' >&2
az login --service-principal \
  --username "$APP_ID" \
  --password "$CLIENT_SECRET" \
  --tenant "$TENANT_ID"
unset CLIENT_SECRET
az account show
```

PowerShell 7 with a client secret:

```powershell
$AppId = "<application-client-id>"
$TenantId = "<tenant-id>"
$Credential = Get-Credential -UserName $AppId -Message "Enter the client secret for the service principal"
az login --service-principal `
  --username $Credential.UserName `
  --password $Credential.GetNetworkCredential().Password `
  --tenant $TenantId
$Credential = $null
az account show
```

When using a certificate instead of a client secret, pass the PEM certificate that contains the service principal's private key:

```bash
APP_ID="<application-client-id>"
TENANT_ID="<tenant-id>"

az login --service-principal \
  --username "$APP_ID" \
  --certificate "/path/to/certificate.pem" \
  --tenant "$TENANT_ID"
```

```powershell
$AppId = "<application-client-id>"
$TenantId = "<tenant-id>"

az login --service-principal `
  --username $AppId `
  --certificate "C:\path\to\certificate.pem" `
  --tenant $TenantId
```

### Pin the credential for the run

After signing in, pin `DefaultAzureCredential`'s chain so this run actually uses the service principal
you just signed in as, rather than any other identity signed in to the same machine (doc/00-overview.md
section 6.19). This applies to the whole shell session, covers both the Graph publish path and Azure
Blob downloads, and `publish` prints a warning if it is not set.

```bash
export AZURE_TOKEN_CREDENTIALS=AzureCliCredential
```

```powershell
$env:AZURE_TOKEN_CREDENTIALS = "AzureCliCredential"
```

Keep client secrets, private keys, and access tokens out of shell history, logs, manifests, and artifacts. Do not commit the certificate or its private key.

If the service principal has no Azure subscription, add `--allow-no-subscriptions` to `az login`. This is sufficient for Graph-only tests. Azure Blob tests use `DefaultAzureCredential` and require RBAC on the storage scope (for example, `Storage Blob Data Reader`), but do not require the service principal to have an Azure subscription in Azure CLI.

This procedure follows Microsoft Learn's [Sign in with Azure CLI using a service principal](https://learn.microsoft.com/cli/azure/authenticate-azure-cli-service-principal?view=azure-cli-latest) and [Get access without a user - Microsoft Graph](https://learn.microsoft.com/graph/auth-v2-service) guidance.

If you need an Azure CLI subscription context for other commands, select the subscription that contains the storage account. Blob download authorization itself comes from the service principal's RBAC assignment on the storage scope:

```bash
az account set --subscription <subscription-id>
```

```powershell
az account set --subscription <subscription-id>
```

Confirm that `az account show` reports the expected tenant and service-principal account. The service principal must have the required Graph/Intune permissions and access to the test assignment group. Keep the tenant guard in every publish command:

`--expected-tenant <tenant-id>`

Relaypublisher checks the token `tid` claim before writing to Graph and fails when it does not match the expected tenant.

## 3. Package input downloads

The `package` command downloads or copies the files referenced by the selected manifest:

| Source type | Local authentication | Download behavior |
|---|---|---|
| `publicHttp` | None, or `Auth.Type: none` | Downloads anonymously. |
| `githubRelease` | With `Auth.Type: token`, set the environment variable named by `Auth.SecretName`. | Downloads the release asset through the GitHub API. |
| `azureBlob` | Use `Auth.Type: workloadIdentity`; local `DefaultAzureCredential` uses the Azure CLI service-principal login. | Downloads from Azure Blob Storage. |

For a private GitHub Release asset, set `Auth.Type: token` and the manifest's secret variable before running `package`:

```bash
export GH_RELEASE_PAT="<token>"
```

```powershell
$env:GH_RELEASE_PAT = "<token>"
```

The variable name must exactly match `Auth.SecretName`. Do not put tokens, storage keys, SAS URLs, or authorization headers in a manifest, log, or artifact.

`package` writes staged package output under the directory passed to `--output`. Windows output includes `.intunewin` content and package metadata. macOS output contains the staged `.pkg` and `package-metadata.json`.

In CI, the same output is passed between jobs as the `intunewin-packages` artifact. The publish job must download that artifact and pass its directory to `--package-dir`. It must use the same `manifest-list.json` produced by `plan`.

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

## 4. Local E2E workflow

The examples invoke the CLI from the source tree. The `--` separates `dotnet run` options from Relaypublisher options. When validating changes on a branch, keep using this source-tree command: a globally installed `relaypublisher` command is a published NuGet tool and can still contain the old content URL implementation. Use the global command only after a release containing the change has been installed.

### 4.1 Build and test

Bash/zsh:

```bash
dotnet build IntuneLobPublisher.slnx --configuration Release
dotnet test IntuneLobPublisher.slnx --configuration Release --no-build
```

PowerShell:

```powershell
dotnet build IntuneLobPublisher.slnx --configuration Release
dotnet test IntuneLobPublisher.slnx --configuration Release --no-build
```

The normal test command is safe to use on macOS and Linux. Tests that exercise the Windows-only
IntuneWin packaging boundary are skipped by MSTest on those platforms; portable IntuneWin parsing,
metadata, Graph mapping, and macOS tests continue to run. Use a Windows runner when the skipped
Windows-only test coverage must be executed.

### 4.2 Resolve the manifest set once

Use an explicit manifest path for a focused local test. This avoids depending on the current Git base ref and makes the input set easy to review.

Bash/zsh:

```bash
CLI_PROJECT="src/IntuneLobPublisher.Cli/IntuneLobPublisher.Cli.csproj"
MANIFEST="manifests/<manifest-file>.yaml"

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  plan \
  --manifest "$MANIFEST" \
  --output manifest-list.json
```

PowerShell:

```powershell
$CliProject = "src/IntuneLobPublisher.Cli/IntuneLobPublisher.Cli.csproj"
$Manifest = "manifests/<manifest-file>.yaml"

dotnet run --configuration Release --project $CliProject -- `
  plan `
  --manifest $Manifest `
  --output manifest-list.json
```

For changed-manifest testing, use `plan --base-ref <base-ref> --output manifest-list.json`. Do not recompute changed manifests in later steps.

To try the workflow with a concrete, runnable manifest before wiring up your own, use `--repo-root samples --manifest manifests/Microsoft/Microsoft.PowerShell/7.6.5/powershell-macos-arm64.yaml` — see [samples/manifests/README.md](../samples/manifests/README.md) for why `--repo-root samples` is required and which other samples are runnable.

### 4.3 Validate

Bash/zsh:

```bash
dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  validate --manifest-list manifest-list.json
```

PowerShell:

```powershell
dotnet run --configuration Release --project $CliProject -- `
  validate --manifest-list manifest-list.json
```

Fix all validation errors before packaging. Validation includes manifest schema, path safety, file-backed assets, and identity/display-name uniqueness for the selected set. It is schema/static validation only for package contents: it does not download a source or inspect the PKG/XAR.

### 4.4 Package

On Windows, generate Windows `.intunewin` packages with the normal command:

```powershell
dotnet run --configuration Release --project $CliProject -- `
  package --manifest-list manifest-list.json --output ./out
```

The command downloads external files, verifies their SHA-256 values, stages repository files, and generates package metadata. For macOS `.pkg` entries, SHA verification completes before XAR inspection; the inspection report records the detected bundle IDs/versions, selected primary, source SHA, manifest identity, and exact CLI version. Semantic warnings use the TTY/non-TTY/`--force` policy described below; hard errors cannot be forced.

A macOS-only manifest can use the normal `package` command on macOS/Linux because macOS packaging has no Windows packaging tool step:

```bash
dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  package --manifest-list manifest-list.json --output ./out
```

For Windows entries on macOS/Linux, use staging-only mode:

```bash
dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  package --manifest-list manifest-list.json --output ./out --stage-only
```

`--stage-only` does not produce `.intunewin` or package metadata for the selected entries. Do not use that output for a Windows `publish`. Because the CLI has no platform-selection option, create a separate manifest list for the macOS entries, for example with `plan --manifest <macos-manifest-path> --output macos-manifest-list.json`, and run normal `package` with that list before publishing macOS entries.

### 4.5 Preview the publish

Run a dry-run against the package directory before any real Graph write:

Bash/zsh:

```bash
TENANT_ID="<tenant-id>"

dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  publish \
  --manifest-list manifest-list.json \
  --package-dir ./out \
  --expected-tenant "$TENANT_ID" \
  --dry-run
```

PowerShell:

```powershell
$TenantId = "<tenant-id>"

dotnet run --configuration Release --project $CliProject -- `
  publish `
  --manifest-list manifest-list.json `
  --package-dir ./out `
  --expected-tenant $TenantId `
  --dry-run
```

Check the selected app identity, existing app resolution, package version, input hash, category plan, assignment plan, tenant, platform-specific mapping errors, and the primary bundle inspection result. `publish --dry-run` rehashes and re-inspects staged macOS packages and preflights every selected entry before reporting the plan.

For an interactive semantic warning, review the detected bundle list and answer `[y/N]`. For a non-interactive run, the same warning fails unless the protected command explicitly supplies `--force`. `--force` acknowledges semantic differences only; it cannot bypass a malformed archive, an ambiguous primary, a checksum mismatch, a stale/tampered artifact, or a tenant/Graph safety error.

If the manifest declares `Categories`, the dry-run also reads the tenant category catalog and the app's
current categories, then prints a `Category plan for app <id>: N add, N keep, N remove` block (a new app uses
the placeholder id `(new app)`). This is the only point where a category name that does not exist in the
tenant is detected - `validate` never contacts Graph. Nothing is written during the dry-run, including when a warning is rejected or a hard error is found in a later manifest entry.

### 4.6 Publish to the test tenant

Only after the dry-run is correct, run the real publish:

Bash/zsh:

```bash
dotnet run --configuration Release --project "$CLI_PROJECT" -- \
  publish \
  --manifest-list manifest-list.json \
  --package-dir ./out \
  --expected-tenant "$TENANT_ID" \
  --result-file publish-result.json
```

PowerShell:

```powershell
dotnet run --configuration Release --project $CliProject -- `
  publish `
  --manifest-list manifest-list.json `
  --package-dir ./out `
  --expected-tenant $TenantId `
  --result-file publish-result.json
```

Verify the app in the Intune admin center, including its display name, management metadata in `notes`, committed content, detection rules, assignments, and - when the manifest declares `Categories` - the app's categories. Keep `publish-result.json` out of public artifacts if it contains operational details.

Before the first Graph write, the command rehashes and re-inspects every staged macOS `.pkg` and completes the full selected set's preflight. It never trusts a package report without checking the current bytes. If an artifact is stale or tampered, replace it by rerunning `package` with the same manifest list and pinned CLI; do not edit the report. A warning rejection or hard error in any entry must leave Graph unchanged for the batch.

Relaypublisher checks the app's `publishingState` before deciding whether the content hash permits a
skip. An app in `processing` is polled until `published`; an app in `notPublished` reuses its sole
interrupted content version, creating the first file when none exists, renewing a sole compatible uncommitted
file in a supported terminal failure state, and failing safely for non-matching or multiple files,
or resuming activation of a committed file for the
same `inputHash`. If polling times out, wait for Intune to finish
processing and rerun the same publish. Do not delete and recreate the app. Existing-app metadata and
category writes are performed only after content activation, because Graph rejects them while the app is
not `Published`.

For content upload, the Graph request URL must include the concrete app type after the app id, for example
`.../mobileApps/<app-id>/microsoft.graph.macOSPkgApp/contentVersions` for the default macOS `pkg` app.
If the log instead shows `.../mobileApps/<app-id>/contentVersions` and Graph returns
`Resource not found for the segment 'contentVersions'`, the old CLI was executed. Rebuild the Release CLI from
the current source and rerun the publish with the source-tree command shown above (or install a released tool
version that contains the fix); do not substitute an older global tool. This failure occurs before a content
version is created.

If the log reports `Content upload step 'commit' failed with Graph uploadState 'commitFileFailed'` for a PKG,
the old CLI may have uploaded ciphertext without the required `[MAC (32 bytes)][IV (16 bytes)]` header, or
reported a ciphertext-only `sizeEncrypted`. Rebuild the Release CLI (`dotnet build IntuneLobPublisher.slnx
--configuration Release`) and rerun the same `publish` command with the existing `manifest-list.json` and
`./out` package artifact. PKG encryption is performed during `publish`, so do not rerun `package`; a failed
commit does not activate the new content, so do not delete or recreate the app.

If the next run reports `The mobile app content cannot be updated before the first content version is
committed`, it used an older CLI that tried to create a second version. Rebuild again from the current source
and rerun: the fixed flow reuses the first version only when it can renew a compatible failed file. When the
old file metadata does not match the current encrypted payload, it fails without adding a sibling file.

To exercise the category flow end to end on a disposable test tenant:

1. Create one or two throwaway categories in the Intune admin center (**Apps** > **App categories**), for
   example `Relaypublisher E2E A` and `Relaypublisher E2E B`.
2. Add `Categories: [Relaypublisher E2E A]` to the app entry, run `publish --dry-run` (expect one `+` line),
   then publish for real and confirm the category in the admin center. `publish-result.json` should show
   `"categoryOutcome":"applied"`.
3. Run the same publish again unchanged. The plan should show only `=` (keep) lines and the result file
   should report `"categoryOutcome":"unchanged"` - this is the idempotency check.
4. Change the list to `[Relaypublisher E2E B]` and publish: the plan adds B and removes A, and the admin
   center reflects the exact set.
5. Set `Categories: []` and publish: every relationship is removed. Then remove the `Categories` key entirely
   and publish once more - the app's categories must stay exactly as they are, with no category Graph call
   made (`"categoryOutcome":"not-requested"`).
6. Delete the throwaway categories from the tenant afterwards. Relaypublisher never deletes a category
   resource itself.

### 4.7. Primary bundle acceptance run

Run this as a protected, manually approved E2E against a disposable tenant. Keep the deterministic XAR fixture in automated tests; use a real `.pkg` source here to verify the complete source-to-device path.

1. Use one fixture/manifest pair that contains a selected application bundle and a second application bundle. Run `validate` and confirm it performs only static checks. Run `package` and verify that source SHA validation happens before XAR inspection, and that the report records the detected IDs, versions, selected primary, manifest identity, and CLI version.
2. Run `publish --dry-run` with a TTY, reject the semantic warning, and confirm that no Graph write occurs. Repeat without a TTY and confirm it fails unless `--force` is supplied. Repeat with the protected `--force` approval and confirm that the warning is recorded while hard errors still fail.
3. Publish both `AppType: pkg` and `AppType: lob` variants. Read the Graph resources back and verify that the selected primary is first in `includedApps`/`childApps`; for `lob`, verify `BundleVersion` -> `buildNumber`, `BundleBuildVersion` -> `versionNumber`, and the top-level primary bundle fields.
4. Assign each app to the disposable test group, wait for a managed macOS device check-in, and verify the device reports the selected bundle and expected version. This is the device-detection portion of the E2E and cannot be replaced by a Graph payload read-back.
5. Rerun the unchanged publish and verify idempotency: no second app, no duplicate content, and no changed primary. Change only `PrimaryBundleId`, repackage, publish, and verify the primary order and device detection change as intended.
6. Replace one staged `.pkg` byte or its report, rerun `publish`, and verify stale/tampered preflight failure with zero Graph writes. Restore the artifact and rerun successfully.

This runbook is designed to be automated as a `workflow_dispatch`-only workflow gated by a protected `intune-e2e` environment (doc/03-ci-github-actions.md "Protected manual E2E (Intune publish)"); step 4's device check-in is the one step that workflow cannot fully automate and still requires a human to confirm out of band.

## 5. Safe rerun and cleanup

- `plan`, `validate`, and `package --stage-only` can be rerun safely.
- `package` can be rerun when an input download or staging operation fails. Reuse the same `manifest-list.json`.
- `publish --dry-run` can be rerun without writing to Intune.
- A real publish is designed to converge, but content activation cannot be undone by the tool.
- Category `$ref` add/remove is idempotent, so an interrupted category synchronization converges on the next run.
- A stale or tampered package artifact is never repaired by editing metadata; rerun `package` with the same manifest list and exact CLI version.
- For an intentional rollback, package the previous manifest version and use `--allow-downgrade` explicitly.
- After the protected E2E, remove the test app, content versions, assignments, and disposable categories from the tenant. Remove local `out`, `manifest-list.json`, and `publish-result.json` after the test when they are no longer needed. Never commit package output, tokens, or signed download URLs.

If the package artifact came from CI instead of a local `package` run, confirm that the downloaded artifact contains the same manifest entries and `package-metadata.json` files before running `publish`.

## 6. Exit codes

| Exit code | Meaning | Operator action |
|---|---|---|
| `0` | The command completed successfully. | Continue to the next E2E step. |
| `1` | Validation, packaging, authentication, tenant, Graph, or publish failure. | Read the error, fix the manifest or environment, and rerun the affected step. |
| `2` | Reserved for not-yet-implemented command paths. | Treat as a tool implementation gap, not an operator retry condition. |
