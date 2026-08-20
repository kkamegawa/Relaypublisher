# Troubleshooting Guide

This guide covers common production failures and recovery paths for Relaypublisher.

The Japanese translation is available in [06-troubleshooting_ja.md](06-troubleshooting_ja.md).

## 0. Manifest Selection in Recovery Commands

The normal CI flow is one-way: `plan` resolves the target set, writes `manifest-list.json`, and the later
`validate`, `package`, and `publish` commands consume that same list. Use `plan --manifest-root <directory>`
to discover a repository manifest tree, `plan --manifest <path>...` (or `--manifests`) for an explicit set,
and `--manifest-list <file>` for downstream commands. A direct `--manifest <path>` is appropriate only for
single-manifest local checks or the focused recovery example below; it should not be used to recompute a CI set.

## 1. Management Metadata in Intune Notes Was Damaged

Relaypublisher stores management metadata as JSON in the Intune app `notes` field. The `notes` field is editable in the Intune admin center, so an operator can accidentally delete or corrupt the metadata.

Expected recovery path:

1. Confirm the manifest still has the same `DisplayName`.
2. Run `plan` for the full manifest root and then `validate --manifest-list` to confirm repository-wide `DisplayName` uniqueness.
3. Run `publish --dry-run` for the affected manifest, or use the same manifest list when checking the full batch.
4. If exactly one Intune app matches the `DisplayName`, Relaypublisher resolves it through DisplayName fallback.
5. On the next real `publish`, Relaypublisher adopts the app by writing fresh management metadata back to `notes`.

Commands:

```powershell
relaypublisher plan --manifest-root manifests --output manifest-list.json

relaypublisher validate --manifest-list manifest-list.json

relaypublisher publish --manifest <manifest-path> --package-dir ./out `
  --expected-tenant <tenant-id> --dry-run
```

```bash
relaypublisher plan --manifest-root manifests --output manifest-list.json

relaypublisher validate --manifest-list manifest-list.json

relaypublisher publish --manifest <manifest-path> --package-dir ./out \
  --expected-tenant <tenant-id> --dry-run
```

If multiple apps match either management metadata or DisplayName fallback, publishing fails. Do not bypass this failure. Fix the duplicate Intune apps or duplicate manifests first, then rerun.

## 2. `TenantMismatchException`

`TenantMismatchException` means the Graph token tenant claim does not match `--expected-tenant`. Relaypublisher fails before writing anything to Intune.

Check these items:

- The CI variable or secret used for `<tenant-id>` is the intended tenant.
- The Entra app registration belongs to that tenant.
- The federated credential is configured on the same app registration used by CI.
- The CI login step receives the intended `AZURE_CLIENT_ID` and `AZURE_TENANT_ID`.
- The GitHub Actions environment or Azure Pipelines service connection is not pointing to an older identity.

Recovery:

1. Fix the CI identity or expected tenant value.
2. Rerun the failed workflow.
3. Keep `--expected-tenant`; do not remove it to make the run pass.

## 2a. Graph Returned 403 on the App Listing

```
error: <package-identifier> macos-arm64: Failed to list Intune mobile apps. Graph request to
'/beta/deviceAppManagement/mobileApps?$select=id,displayName,notes' returned 403 (Forbidden): ...
```

`GET /deviceAppManagement/mobileApps` is the first Graph call of every publish, including
`publish --dry-run` (see "Why dry-run needs Graph permission" below). Every app entry resolves through
it, so Relaypublisher treats 401/403 here as identity-wide and stops the batch instead of repeating the
same error once per entry.

First separate the two failure classes:

- **401** - the token could not be obtained, or it was rejected. Check the CI login step and
  `--expected-tenant` (section 2).
- **403** - the token is valid but the identity is not permitted. Continue below.

### Check what the token actually carries

An app-only token carries **application** permissions in its `roles` claim. If `roles` is missing or
does not contain `DeviceManagementApps.ReadWrite.All` (or `DeviceManagementApps.Read.All`), the 403 is
explained. Treat the access token itself as a secret: do not paste it into an issue, a chat, or a log.

Bash / zsh:

```bash
TOKEN=$(az account get-access-token --resource https://graph.microsoft.com --query accessToken -o tsv)
PAYLOAD=$(printf '%s' "$TOKEN" | cut -d. -f2 | tr '_-' '/+')
while [ $(( ${#PAYLOAD} % 4 )) -ne 0 ]; do PAYLOAD="${PAYLOAD}="; done
printf '%s' "$PAYLOAD" | base64 -d | grep -o '"roles":\[[^]]*\]'
unset TOKEN PAYLOAD
```

PowerShell 7:

```powershell
$Token = az account get-access-token --resource https://graph.microsoft.com --query accessToken -o tsv
$Payload = $Token.Split('.')[1].Replace('-', '+').Replace('_', '/')
$Payload = $Payload.PadRight([int][Math]::Ceiling($Payload.Length / 4) * 4, '=')
$Claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Payload)) | ConvertFrom-Json
$Claims.roles
$Token = $null
```

### Most common cause: the permission is delegated, not application

Microsoft Graph offers `DeviceManagementApps.ReadWrite.All` in two forms, and the portal shows both
under the same name. Only one of them works here:

| Portal permission type | Token claim | Works for Relaypublisher |
|---|---|---|
| Delegated | `scp` (user tokens only) | No |
| Application | `roles` (app-only tokens) | Yes |

Relaypublisher authenticates as a service principal with the client credentials flow, which produces an
app-only token. A delegated permission never appears in such a token, so Graph answers 403 even when
the portal shows the permission as granted and admin-consented. In the Entra admin center, open the app
registration's **API permissions** blade and confirm the **Type** column reads **Application** for
`DeviceManagementApps.ReadWrite.All`. If it reads **Delegated**, add the permission again under
**Application permissions** and grant admin consent; the delegated entry can then be removed.

`ReadWrite.All` includes read access, so `DeviceManagementApps.Read.All` does not need to be added
alongside it.

### After changing permissions

Consent does not update tokens that were already issued. Sign out and back in so a fresh token is
acquired, then rerun:

```bash
az account clear
az login --service-principal --username <application-client-id> --tenant <tenant-id> --certificate <certificate-path>
```

Relaypublisher also caches the token in-process for the lifetime of one run, so rerun the command
rather than expecting a running process to pick up the new permission.

### If `roles` is correct and 403 persists

Relaypublisher logs the identity that acquired the token on every fresh Graph token acquisition, before
the `roles` check above even applies:

```
info: IntuneLobPublisher.Core.Publishing.GraphAuthenticationHandler[0] Acquired Graph token for identity
appid=<guid> idtyp=<type> roles=<permission names>.
```

Compare `appid` with the application (client) ID of the intended app registration - a different value
means a different identity acquired the token. `idtyp=app` confirms an app-only (client-credentials)
token; any other value (for example `idtyp=user`, or the claim missing) means a user identity was used
instead of the service principal. This is the fastest way to confirm the wrong identity was used,
without falling back to `az rest`.

If the identity is unexpected, or you want to confirm the permission independently of Relaypublisher:

```bash
az rest --method get --url 'https://graph.microsoft.com/beta/deviceAppManagement/mobileApps?$select=id,displayName&$top=1'
```

If this succeeds while `publish` still returns 403, the permission is fine and the two calls are not
using the same identity. `DefaultAzureCredential` tries several credentials in order and the Azure CLI
login is not guaranteed to be the one that wins, so on a developer machine it can pick up a signed-in
Visual Studio, VS Code or broker identity instead. That identity is usually in the same tenant, so
`--expected-tenant` does not catch it. This is a documented, supported configuration
([05-operation.md](05-operation.md) section 3, [00-overview.md](00-overview.md) section 6.19), not just a
workaround: pin the credential chain for every run, not only when troubleshooting.

```bash
export AZURE_TOKEN_CREDENTIALS=AzureCliCredential
```

```powershell
$env:AZURE_TOKEN_CREDENTIALS = "AzureCliCredential"
```

If `publish` printed a warning starting with `AZURE_TOKEN_CREDENTIALS is not set` before the 403, the
chain was not pinned - act on that warning and rerun before investigating permissions any further.

- Confirm the tenant has an active Intune license. The Microsoft Graph API for Intune requires one, and
  a tenant without it returns 403 regardless of permissions.
- Confirm the `/beta/` endpoint is available in the tenant. The app listing uses beta so that
  `macOSPkgApp` entries are not silently omitted; see section 6a for the related pkg-only failure.
- Report the `client-request-id` and `request-id` values from the error message when opening a support
  case. Relaypublisher includes both in the message; they are correlation ids, not secrets.

### Why dry-run needs Graph permission

`publish --dry-run` resolves the existing Intune app before it decides what would change - it has to,
because the dry-run output states whether an app would be created or updated and compares the published
version. That resolution happens before the dry-run branch, so `--dry-run` requires the same Graph read
access as a real publish. A dry-run cannot be used to test the pipeline without granting permissions.

## 3. Downgrade Was Skipped

By default, Relaypublisher skips a publish when the manifest `PackageVersion` is lower than the version stored in Intune management metadata.

Typical causes:

- An older manifest was selected by `plan`.
- A release branch contains an older package version.
- The operator is intentionally rolling back to a previous package.

Check the selected manifests:

```powershell
Get-Content manifest-list.json
```

```bash
cat manifest-list.json
```

If this is not an intentional rollback, update the manifest version or fix the base ref used by `plan`.

For an intentional rollback, publish the previous package explicitly with `--allow-downgrade`:

```powershell
relaypublisher publish --manifest <manifest-path> --package-dir ./out `
  --expected-tenant <tenant-id> --allow-downgrade
```

```bash
relaypublisher publish --manifest <manifest-path> --package-dir ./out \
  --expected-tenant <tenant-id> --allow-downgrade
```

After rollback, verify the app in Intune and confirm assignments still match the intended manifest state.

## 3a. Version Upgrade Did Not Update the Existing App

Symptoms after bumping `PackageVersion` for an existing app (doc/05-operation.md §4c):

| Symptom | Likely cause | Fix |
|---|---|---|
| A second app showed up in Intune instead of the existing one being updated | `DisplayName`, `PackageIdentifier`, `Platform`, or `Architecture` changed along with the version, so identity resolution (doc/00-overview.md §6.1) no longer matched the existing app | Restore the original identity fields, republish so the correct app is updated, and remove the extra app manually in the Intune admin center (doc/00-overview.md §6.11 - retirement is out of scope for this tool) |
| The run reported `skipped (downgrade)` | The manifest version is lower than the version stored in Intune metadata | See §3 above |
| `publish` succeeded but the content did not change | `inputHash` matched the stored value, so content upload was skipped (doc/00-overview.md §6.7) | Confirm the manifest or its input files actually changed; an unchanged `inputHash` is expected to skip re-upload |
| macOS: devices still report the old detected version after publish | `Detection.IncludedApps[].BundleVersion` was not updated to match the new release | Fix the manifest and republish |
| The log shows `superseded by version X` for the older manifest | Expected when a resolved set contains more than one version of the same identity (doc/00-overview.md §6.8) | No action needed - only the highest version is published |

## 4. GitHub Release Token Is Missing

When a `githubRelease` source uses `Auth.Type: token`, Relaypublisher reads the token from the environment variable named by `Auth.SecretName`.

If the variable is missing or empty:

- Confirm the manifest uses the intended `SecretName`.
- Confirm the CI job maps the secret to an environment variable with the same name.
- Confirm pull request jobs are not expected to download private release assets.

Example:

```yaml
Auth:
  Type: token
  SecretName: GH_RELEASE_PAT
```

The CI environment must expose `GH_RELEASE_PAT` to the package job.

## 5. Azure Blob Source Cannot Be Downloaded

For `azureBlob` sources, Relaypublisher uses workload identity instead of a static secret.

Check these items:

- The package job has OIDC enabled.
- The CI identity has the storage reader role at the required storage scope.
- `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and `AZURE_SUBSCRIPTION_ID` are available to the login step.
- The manifest `AccountName`, `Container`, and `BlobName` identify the intended package object.

Rerun after correcting identity or storage permissions. Do not place storage account keys or signed package URIs in manifests, logs, or artifacts.

## 6. Package Metadata Is Missing

`publish` consumes `package-metadata.json` written by `package`.

If publish reports missing package metadata:

- Confirm the package job completed successfully.
- Confirm the publish job downloaded the `intunewin-packages` artifact to the same path passed as `--package-dir`.
- Confirm the same `manifest-list.json` was used by `package` and `publish`.
- Rerun `package` rather than editing package metadata manually.

## 6a. macOS-Specific Failures

- **`UnsupportedMacOsVersionException` mentioning "no known macOS minimum-operating-system mapping"**:
  `Requirements.MinimumOSVersion` is not one of the values `MacOsMinimumOperatingSystemTable` recognizes
  (`10.13`-`13.0`, or `14`/`14.0`/`15`/`15.0`/`26`/`26.0` for `AppType: pkg` only). This mapping only runs during
  `publish` (including `--dry-run`), not `package`, so fix the version string before publishing.
- **`UnsupportedMacOsVersionException` mentioning "AppType 'pkg'"**: the manifest has `AppType: lob` with
  `Requirements.MinimumOSVersion` set to macOS 14 or later. `macOSLobApp` stays on Graph v1.0, which has no
  minimum-OS flag past macOS 13. Either lower `MinimumOSVersion`, or switch to `AppType: pkg` (Graph beta,
  which does support 14/15). `validate` does not catch this - it is a Graph API-version limitation, not a
  manifest schema rule - and neither does `package`, which never maps `MinimumOSVersion` to a Graph value;
  it only surfaces at `publish` time (and in `publish --dry-run`, which maps the payload to surface exactly
  this kind of error before any Graph write).
- **`Detection.IncludedApps` missing or empty**: every macOS app entry requires at least one
  `IncludedApps` item (`BundleId` + `BundleVersion`); this fails at `validate`, not `publish`.
- **PKG content upload never reaches `commitFileSuccess`, or fails with an HTTP 400 on the SAS URI upload
  itself (fixed)**: earlier versions of `PkgContentPreparer` uploaded only the AES-256-CBC ciphertext, but
  Intune expects the uploaded content stream to start with a 48-byte `[mac (32 bytes)][iv (16 bytes)]`
  header in front of the ciphertext - the same layout a `.intunewin` content entry already has, which is
  why `IntuneWinContentExtractor` could stream it unmodified. This was confirmed against Microsoft's own
  reference implementation (`microsoftgraph/powershell-intune-samples`, `LOB_Application/Application_LOB_Add.ps1`,
  the `EncryptFileWithIV` function) rather than reverse-engineered. `PkgContentPreparer` now writes that
  header (see doc/00-overview.md §6.13). If commit still fails for macOS entries specifically (Windows
  entries unaffected) after upgrading, file an issue with the Graph error and `client-request-id`/
  `request-id` from the log rather than retrying blindly.
- **`GraphRequestException` 400 mentioning `v14_0`/`v15_0` "does not exist on type
  'microsoft.graph.macOSMinimumOperatingSystem'" (fixed)**: earlier versions always serialized `v14_0`
  and `v15_0` (even as `false`) on every macOS app payload, but Graph v1.0's `macOSMinimumOperatingSystem`
  has no such properties at all - only the beta resource does. This made every `AppType: lob` create/update
  fail, regardless of `Requirements.MinimumOSVersion`. `MacOsMinimumOperatingSystemPayload` now leaves
  those fields (and the newly-added beta-only `v26_0`) null for a v1.0 target, so they are omitted from
  the request body instead of sent as a literal `false`.
- **`GraphRequestException` with a 403/404 specific to macOS `AppType: pkg` entries**: pkg apps are
  created, updated, and content-uploaded entirely through Graph **beta** (`macOSPkgApp` does not exist in
  v1.0). Confirm the service principal's Graph permissions (section 2a) and the tenant's beta API
  availability; this does not affect Windows or `AppType: lob` publishes, which stay on v1.0, so it
  fails only the pkg entries and lets the rest of the batch continue - unlike a 403 on the app listing,
  which stops the whole run.
- **Device error `2016214710` ("The preinstall script provided by the admin failed")**: the
  `Scripts.PreInstall` script returned a non-zero exit code on the device. This may be expected if the
  script is waiting for a precondition; Intune retries it at the next device check-in. If it persists,
  check the script's logic and exit codes - Relaypublisher cannot see the script's runtime behavior, only
  that its content was uploaded correctly. A `Scripts.PostInstall` failure is never reported this way; the
  app still shows "success" regardless of the post-install script's exit code
  (doc/01-manifest-schema.md §5.4.2).
- **`ManifestLoadException` mentioning `Scripts.PreInstall` / `Scripts.PostInstall` "does not exist"**:
  the script path in the manifest does not resolve under `--repo-root` at publish time. This mirrors the
  `Icon` existence check (doc/01-manifest-schema.md §5.4.1) but for scripts; `validate` catches this before
  any Graph call, so if it surfaces only at `publish` the repository root or working directory likely
  differs between the two commands.

## 7. Safe Rerun Rules

- `validate`, `plan`, and `package --stage-only` are safe to rerun.
- `package` is safe to rerun and should reproduce the same deterministic `inputHash` for the same inputs.
- `publish --dry-run` is safe to rerun.
- Real `publish` is designed to converge, but the content activation step cannot be undone by the tool. Roll back by publishing the previous manifest version with `--allow-downgrade`.
