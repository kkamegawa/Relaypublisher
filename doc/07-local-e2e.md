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

The repository sample manifests are reference material rather than guaranteed E2E fixtures. Some intentionally fail validation or refer to files that are not included in the repository. Use an organization-specific test manifest with real package inputs.

## 2. Local Azure CLI authentication

Relaypublisher uses `DefaultAzureCredential` for Microsoft Graph and Azure Blob access. For local execution, sign in with Azure CLI:

```bash
az login --tenant <tenant-id>
az account show
```

```powershell
az login --tenant <tenant-id>
az account show
```

If the package source uses Azure Blob, select the subscription that contains the storage account:

```bash
az account set --subscription <subscription-id>
```

```powershell
az account set --subscription <subscription-id>
```

The signed-in identity must have the required Graph/Intune permissions and access to the test assignment group. Keep the tenant guard in every publish command:

`--expected-tenant <tenant-id>`

Relaypublisher checks the token `tid` claim before writing to Graph and fails when it does not match the expected tenant.

## 3. Package input downloads

The `package` command downloads or copies the files referenced by the selected manifest:

| Source type | Local authentication | Download behavior |
|---|---|---|
| `publicHttp` | None, or `Auth.Type: none` | Downloads anonymously. |
| `githubRelease` | With `Auth.Type: token`, set the environment variable named by `Auth.SecretName`. | Downloads the release asset through the GitHub API. |
| `azureBlob` | Use `Auth.Type: workloadIdentity`; local `DefaultAzureCredential` uses the Azure CLI login. | Downloads from Azure Blob Storage. |

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

The examples invoke the CLI from the source tree. The `--` separates `dotnet run` options from Relaypublisher options. A globally installed `relaypublisher` command can be used instead after replacing the command prefix.

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

Fix all validation errors before packaging. Validation includes manifest schema, path safety, file-backed assets, and identity/display-name uniqueness for the selected set.

### 4.4 Package

On Windows, generate Windows `.intunewin` packages with the normal command:

```powershell
dotnet run --configuration Release --project $CliProject -- `
  package --manifest-list manifest-list.json --output ./out
```

The command downloads external files, verifies their SHA-256 values, stages repository files, and generates package metadata.

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

Check the selected app identity, existing app resolution, package version, input hash, assignment plan, tenant, and platform-specific mapping errors.

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

Verify the app in the Intune admin center, including its display name, management metadata in `notes`, committed content, detection rules, and assignments. Keep `publish-result.json` out of public artifacts if it contains operational details.

## 5. Safe rerun and cleanup

- `plan`, `validate`, and `package --stage-only` can be rerun safely.
- `package` can be rerun when an input download or staging operation fails. Reuse the same `manifest-list.json`.
- `publish --dry-run` can be rerun without writing to Intune.
- A real publish is designed to converge, but content activation cannot be undone by the tool.
- For an intentional rollback, package the previous manifest version and use `--allow-downgrade` explicitly.
- Remove local `out`, `manifest-list.json`, and `publish-result.json` after the test when they are no longer needed. Never commit package output, tokens, or signed download URLs.

If the package artifact came from CI instead of a local `package` run, confirm that the downloaded artifact contains the same manifest entries and `package-metadata.json` files before running `publish`.

## 6. Exit codes

| Exit code | Meaning | Operator action |
|---|---|---|
| `0` | The command completed successfully. | Continue to the next E2E step. |
| `1` | Validation, packaging, authentication, tenant, Graph, or publish failure. | Read the error, fix the manifest or environment, and rerun the affected step. |
| `2` | Reserved for not-yet-implemented command paths. | Treat as a tool implementation gap, not an operator retry condition. |
