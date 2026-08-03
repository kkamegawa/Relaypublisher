# Operation Guide

This guide covers the setup and daily operation required to publish Intune LOB apps with Relaypublisher.

The Japanese translation is available in [05-operation_ja.md](05-operation_ja.md).

## 1. Microsoft Entra App Registration

Create one Microsoft Entra application registration for the CI publisher identity.

Required configuration:

- Account type: single tenant for the target tenant.
- Microsoft Graph application permission: `DeviceManagementApps.ReadWrite.All`.
- Admin consent: granted by a tenant administrator before the first production publish.
- No client secret is required for the recommended CI setup. Use workload identity federation instead.

Operational notes:

- Store the application client ID in the CI secret or variable named `AZURE_CLIENT_ID`.
- Store the tenant ID in `AZURE_TENANT_ID`.
- If Azure Blob sources are used, also store the subscription ID in `AZURE_SUBSCRIPTION_ID` and grant the CI identity read access to the package storage scope.
- Use `publish --expected-tenant <tenant-id>` so a token from the wrong tenant fails before any write.

## 2. Federated Credentials

Federated credentials allow CI to exchange a runner-issued OIDC token for a Microsoft identity platform access token. Configure them on the same Entra app registration used for Graph publishing.

Use the Microsoft-recommended token exchange audience for the federated credential. Keep the issuer and subject values exact; wildcard matching is not supported.

### GitHub Actions

Use a GitHub Actions federated credential scoped to the protected production environment.

Recommended subject shape:

```text
repo:<owner>/<repo>:environment:production
```

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

## 3. Source Provider Environment Variables

Source provider authentication is controlled by each manifest item's `Auth` block.

| Source type | `Auth.Type` | Required environment variable | Notes |
|---|---|---|---|
| `publicHttp` | omitted or `none` | none | The download is anonymous. |
| `githubRelease` | `token` | the value of `Auth.SecretName`, commonly `GH_RELEASE_PAT` | The token is read from the environment variable with the same name. |
| `azureBlob` | `workloadIdentity` | `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and CI OIDC variables | Access is granted through the federated CI identity. |

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
dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  plan --base-ref <base-ref> --output manifest-list.json
```

```bash
dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- \
  plan --base-ref <base-ref> --output manifest-list.json
```

Validate the selected manifests:

```powershell
dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  validate --manifest-list manifest-list.json
```

```bash
dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- \
  validate --manifest-list manifest-list.json
```

Package Windows Win32 apps on Windows:

```powershell
dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  package --manifest-list manifest-list.json --output ./out
```

On non-Windows runners, use `--stage-only` for Windows entries to skip `.intunewin` generation while still
validating staging. macOS entries in the same manifest list are staged (and their `package-metadata.json`
written) regardless of `--stage-only`'s absence, since macOS packaging has no external tool step and does
not require a Windows runner:

```bash
dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- \
  package --manifest-list manifest-list.json --output ./out --stage-only
```

Preview publish changes without writing to Intune:

```powershell
dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  publish --manifest-list manifest-list.json --package-dir ./out `
  --expected-tenant <tenant-id> --dry-run
```

Publish to Intune:

```bash
dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- \
  publish --manifest-list manifest-list.json --package-dir ./out \
  --expected-tenant <tenant-id>
```

## 4a. macOS Notes

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

## 5. Exit Codes

| Exit code | Meaning | Operator action |
|---|---|---|
| `0` | The command completed successfully. | Continue the CI workflow. |
| `1` | Validation, packaging, authentication, tenant, Graph, or publish failure. | Read the error message, fix the manifest or environment, and rerun. |
| `2` | Reserved for not-yet-implemented command paths. | Treat as a tool implementation gap, not an operator retry condition. |

## 6. Production Checklist

- `validate` passes for the full repository.
- `plan` output is stored as `manifest-list.json` and reused by later jobs.
- The package job does not recompute changed manifests.
- The publish job runs with a protected environment and serialized execution.
- `publish` always uses `--expected-tenant`.
- GitHub release tokens and other source provider secrets are passed only to jobs that need them.
- No authorization header, token, signed package URI, or secret value is written to logs or artifacts.
