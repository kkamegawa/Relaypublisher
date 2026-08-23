# Implement Intune Graph create/update flow for Win32 apps

## Goal

Publish generated `.intunewin` packages to Microsoft Intune through Microsoft Graph.

## Prerequisites

- Microsoft Entra ID app registration with application permission `DeviceManagementApps.ReadWrite.All` and admin consent.
- Federated credential configured for the CI identity (subject claim restricted to the production environment).
- Token acquisition path: `azure/login` (or Azure Pipelines service connection) signs in to Azure CLI; the CLI acquires the Graph token via `DefaultAzureCredential` for scope `https://graph.microsoft.com/.default`.

## Requirements

### Authentication and safety

- Authenticate using Azure.Identity.
- Get Graph token for https://graph.microsoft.com/.default.
- Support `--expected-tenant <tenant-id>`: compare against the token `tid` claim and fail before any write when it does not match.
- Honor `Retry-After` on HTTP 429/503 for every Graph call, with capped exponential backoff.
- Log `client-request-id` / `request-id` on failure.

### App resolution

- Resolve existing app by management metadata in `notes`.
- Fallback to DisplayName.
- Fail when either lookup returns multiple matches (never overwrite an ambiguous app).
- When resolved via DisplayName fallback, write management metadata back to `notes` (adopt), so manual metadata damage is self-healing.
- Do not match by version.

### Version guard and idempotency

- Compare manifest `PackageVersion` with the `packageVersion` stored in notes metadata. If the manifest version is lower, skip with a warning unless `--allow-downgrade` is specified.
- Compare the deterministic input hash (from package metadata JSON) with the `inputHash` stored in notes metadata. When equal, skip content upload and only reconcile app properties and assignments.
- Re-running a failed publish must converge to the same result (idempotent).

### Create / update mapping

- Map manifest to `win32LobApp`:
  - `installCommandLine` / `uninstallCommandLine` / `installExperience` / `restartBehavior`.
  - `returnCodes`: from manifest `Install.ReturnCodes`; when omitted apply the Intune default set (0 success, 1707 success, 3010 softReboot, 1641 hardReboot, 1618 retry). `returnCodes` must never be empty.
  - Architecture: use `allowedArchitectures` (`x64`, `arm64`). Note: the v1.0 `applicableArchitectures` enum has NO `arm64` value; when `allowedArchitectures` is set, `applicableArchitectures` becomes `none`.
  - `minimumSupportedWindowsRelease`: map from manifest `MinimumOSVersion` build number using a table in Core (e.g. `10.0.19045` -> `Windows10_22H2`). Fail on unknown build numbers.
  - Detection: read `Detection.ScriptFile` content from the repository and embed it base64-encoded into a `win32LobAppPowerShellScriptRule` (detection rule). The script is NOT distributed via the package; it lives in the Graph payload.
  - Optional app info: `owner`, `developer`, `informationUrl`, `largeIcon` (from manifest `Icon`), `roleScopeTagIds`.
  - `displayVersion`: set from `PackageVersion`.
- Create win32LobApp if not found.
- Update existing app if found. Preserve existing Intune app ID.
- Do not include package version in display name.
- Validate that the management metadata JSON fits within the `notes` length limit before writing.

### Content upload flow

The `.intunewin` file produced by IntuneWinAppUtil is a ZIP container holding the encrypted payload (`IntunePackage.intunewin`) and `Detection.xml` (encryption key, IV, MAC, file digest). The upload flow is:

1. Extract the `.intunewin` container; parse `Detection.xml` into a `fileEncryptionInfo` payload (encryptionKey, initializationVector, mac, macKey, profileIdentifier=ProfileVersion1, fileDigest, fileDigestAlgorithm=SHA256).
2. `POST .../mobileApps/{id}/microsoft.graph.win32LobApp/contentVersions` to create a new content version.
3. `POST .../mobileApps/{id}/microsoft.graph.win32LobApp/contentVersions/{cv}/files` with `name`, `size` (unencrypted), `sizeEncrypted`.
4. Poll the file until `uploadState = azureStorageUriRequestSuccess` and read `azureStorageUri`.
5. Upload the encrypted payload to the SAS URI as Azure block blob chunks. For long uploads, call the `renewUpload` action before SAS expiry and continue.
6. `POST .../files/{f}/commit` with the `fileEncryptionInfo`.
7. Poll until `uploadState = commitFileSuccess` (fail on `commitFileFailed`), with a configurable timeout.
8. `PATCH` the win32LobApp with `committedContentVersion = {cv}`.
9. Poll `publishingState` until `published`.
10. Update notes management metadata (packageVersion, inputHash, manifestHash, sourceCommit).

Transaction boundary: steps 1–7 are safe to retry — existing clients keep receiving the previous committed content. On retry, a `notPublished` app reuses its sole existing content version: uncommitted files are removed and replaced, while a sole committed file for the same `inputHash` resumes from step 8. Multiple versions, mixed committed/uncommitted files, or a committed file that cannot be tied to the current input fail without deleting the app or committed content. Step 8 activates the new content and cannot be undone by this tool (rollback = republish the previous manifest version with `--allow-downgrade`).

## Acceptance criteria

- New app can be created.
- Existing app can be updated.
- Same PackageIdentifier + Platform + Architecture updates the same app.
- Ambiguous resolution (multiple matches) fails without writing.
- DisplayName fallback repairs (adopts) the notes metadata.
- Publishing a lower version is skipped unless `--allow-downgrade`.
- Unchanged input hash skips content upload.
- Arm64 app is created with `allowedArchitectures = arm64`.
- `returnCodes` are always populated (manifest values or defaults).
- Detection script content is embedded base64 in the detection rule.
- `fileEncryptionInfo` from `Detection.xml` is sent on commit; `committedContentVersion` is patched after commit succeeds.
- 429 responses are retried honoring `Retry-After`.
- Graph request id is logged on failure.
- Publish against an unexpected tenant fails before any write.
