# Troubleshooting Guide

This guide covers common production failures and recovery paths for Relaypublisher.

The Japanese translation is available in [06-troubleshooting_ja.md](06-troubleshooting_ja.md).

## 1. Management Metadata in Intune Notes Was Damaged

Relaypublisher stores management metadata as JSON in the Intune app `notes` field. The `notes` field is editable in the Intune admin center, so an operator can accidentally delete or corrupt the metadata.

Expected recovery path:

1. Confirm the manifest still has the same `DisplayName`.
2. Run `validate` for the repository to confirm `DisplayName` is unique.
3. Run `publish --dry-run` for the affected manifest.
4. If exactly one Intune app matches the `DisplayName`, Relaypublisher resolves it through DisplayName fallback.
5. On the next real `publish`, Relaypublisher adopts the app by writing fresh management metadata back to `notes`.

Commands:

```powershell
dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  validate --manifest <manifest-path>

dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  publish --manifest <manifest-path> --package-dir ./out `
  --expected-tenant <tenant-id> --dry-run
```

```bash
dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- \
  validate --manifest <manifest-path>

dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- \
  publish --manifest <manifest-path> --package-dir ./out \
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
dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- `
  publish --manifest <manifest-path> --package-dir ./out `
  --expected-tenant <tenant-id> --allow-downgrade
```

```bash
dotnet run --project src/IntuneLobPublisher.Cli --configuration Release -- \
  publish --manifest <manifest-path> --package-dir ./out \
  --expected-tenant <tenant-id> --allow-downgrade
```

After rollback, verify the app in Intune and confirm assignments still match the intended manifest state.

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

## 7. Safe Rerun Rules

- `validate`, `plan`, and `package --stage-only` are safe to rerun.
- `package` is safe to rerun and should reproduce the same deterministic `inputHash` for the same inputs.
- `publish --dry-run` is safe to rerun.
- Real `publish` is designed to converge, but the content activation step cannot be undone by the tool. Roll back by publishing the previous manifest version with `--allow-downgrade`.
