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
