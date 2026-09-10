# ADR: CI and Release Distribution

This is the English counterpart for the release-distribution decision recorded in
[ci-release.md](ci-release.md). The Japanese document remains the complete ADR history.

## 2026-09-10: Azure Artifacts Early Distribution for Internal Testing (Issue #153)

- **Decision**: Push packages to Azure Artifacts from
  `.github/workflows/release-draft.yml`, downloading the exact `.nupkg` attached to the draft
  GitHub release. `.github/workflows/release-publish.yml` is responsible only for public
  distribution to GitHub Packages and nuget.org.
  - **Reason**: Internal CI and closed-network users can validate the package before a person
    publishes the release and makes it available through public feeds.
  - **Impact**: Add a `push-azure-artifacts` job to `release-draft.yml`. It uses the existing
    `release` environment and has `id-token: write` for Azure workload identity federation. Keep
    Azure credentials separate from the packing job and push only the package that is attached to
    the release.
  - **Lifecycle and rerun rule**: An Azure Artifacts package can be deleted into the recycle bin,
    but its version identifier remains permanently reserved and cannot be republished.
    `--skip-duplicate` retains existing package bytes; a draft-workflow rerun is valid only when
    normalized package contents and metadata are unchanged. If they differ, create a new version
    tag rather than attempting to republish the existing version.

## 2026-09-10: Draft Release Package Workflow Artifact Handoff (Issue #157)

- **Decision**: The `push-azure-artifacts` job does not download `.nupkg` assets from the draft
  release. The `draft-release` job uploads the exact selected package bytes as a `release-package`
  workflow artifact with one-day retention, and the Azure Artifacts job downloads that artifact
  before pushing it.
  - **Reason**: A job token with only `contents: read` cannot retrieve draft release assets, and
    granting `contents: write` to the Azure credential-bearing job would combine repository/release
    write access with Azure OIDC credentials in one job.
  - **Impact**: New drafts hand off the freshly attached package. Safe reruns against existing
    drafts first compare normalized package contents and metadata, then hand off the existing draft
    asset bytes when they match. Azure Artifacts therefore receives bytes identical to the draft
    asset while its job keeps only `contents: read` and `id-token: write`.
  - **Operational note**: The handoff artifact contains only the `.nupkg`, logs no package
    contents, and is used only for job-to-job transfer. The public release gate remains manual
    publication of the draft release.
