# Implement GitHub Release and Azure Blob source providers

## Goal

Add authenticated source providers for private/internal package binaries.

## Unified source item shape

All source items (Windows `ExternalFiles` entries and macOS `Source`) share one shape:

```yaml
Type: publicHttp | githubRelease | azureBlob
Destination: <relative path in staging>
Sha256: "<sha256>"     # required
Auth:
  Type: none | token | workloadIdentity
  SecretName: <env var name>   # required for Type: token
```

Type-specific fields: `Url` (publicHttp) / `Owner`, `Repository`, `Tag`, `AssetName` (githubRelease) / `AccountName`, `Container`, `BlobName` (azureBlob).

## Requirements

- Implement githubRelease provider.
  - Support private GitHub Release asset download.
  - Read the token from the environment variable named by `Auth.SecretName`. Fail with a clear message when the variable is missing or empty.
  - CI wiring: the pipeline maps the secret to that environment variable on the package job (see `03-ci-github-actions.md` / `04-ci-azure-pipelines.md`). Fork PRs receive no secrets; document that authenticated downloads only run on push/dispatch.
- Implement azureBlob provider.
  - Support Azure workload identity authentication (`Auth.Type: workloadIdentity` via `DefaultAzureCredential`).
  - The CI job that runs staging needs Azure login when any manifest uses azureBlob.
- Verify SHA256 for all downloaded files (`Sha256` is required for every source type).
- Add retry/backoff for transient download failures.
- Mask credentials in logs; never log Authorization headers or signed URLs.

## Acceptance criteria

- Private GitHub Release asset can be downloaded.
- Azure Blob can be downloaded using federated identity.
- Missing `Auth.SecretName` environment variable fails with an actionable error.
- SHA256 mismatch fails the download.
- Credentials are not logged.
