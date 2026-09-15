# ADR: CI and Release Distribution

This is the English counterpart for the release-distribution decision recorded in
[ci-release.md](ci-release.md). The Japanese document remains the complete ADR history.

## 2026-09-15: macOS Distribution Through a Homebrew Tap (Issue #173)

- **Decision**: Distribute `relaypublisher` for macOS on Apple silicon as a Homebrew formula in a
  tap repository separate from this one, `kkamegawa/homebrew-tap`. The formula downloads
  `relaypublisher-<version>-osx-arm64.zip` from the GitHub release; no separate binary is built for
  Homebrew.
  - **Reason**: Offer `brew install` / `brew upgrade` while the GitHub release stays the single
    source of the binary. Keeping the tap in this repository would force users to pass the tap URL
    and would require a formula-update commit on main after each release, which does not fit the
    tag- and main-based release gate.
- **Decision**: Use a Formula, not a Cask.
  - **Reason**: Homebrew quarantines only cask downloads, and Homebrew 5.0 removed
    `--no-quarantine`. The single-file app is neither code-signed nor notarized, so Gatekeeper blocks
    it as a cask but not as a formula.
- **Decision**: Support `osx-arm64` only; reject Intel Macs with `depends_on arch: :arm64`. Linux is
  out of scope.
  - **Reason**: Releases only ship `osx-arm64`, and Homebrew 7.0 moved Intel macOS to Tier 3 (support
    ends 2027-09).
- **Decision**: Keep building on the ubuntu runner in `release-draft.yml`.
  - **Reason**: The .NET 10 SDK ad-hoc signs single-file bundles with its managed signer even on
    non-macOS hosts (dotnet/runtime PR #110417), which satisfies Apple silicon's signature
    requirement. The first tap CI run confirmed `codesign --verify --strict` passes.
  - **Follow-up**: After updating the .NET SDK, confirm the tap CI's `codesign --verify --strict`
    still passes.
- **Decision**: Deliver only stable releases to the tap. The `update-homebrew-tap` job in
  `release-publish.yml` opens a pull request against the tap, and a person merges it after the tap
  CI passes. Writes use a token from a GitHub App installed only on the tap.
  - **Reason**: Keep a broken formula from reaching users immediately. Unlike a PAT, a GitHub App
    needs no expiry management; unlike `GITHUB_TOKEN`, it can write to another repository and its
    pull requests trigger the tap's CI.
  - **Impact**: The `release` environment needs `HOMEBREW_TAP_APP_CLIENT_ID` and
    `HOMEBREW_TAP_APP_PRIVATE_KEY`. The tap job is independent of `push-packages`, so neither
    failure blocks the other.
- **Follow-up**: Homebrew 6.0 tap trust means users must run `brew tap --trust kkamegawa/tap` first.
  Homebrew 7.0 deprecated `post_install` in third-party taps, so do not add `post_install` to the
  formula.

## 2026-09-10: Azure Artifacts Early Distribution for Internal Testing (Issue #153)

- **Decision**: Push packages to Azure Artifacts from
  `.github/workflows/release-draft.yml`, deriving a preview-version package from the selected
  `.nupkg` by rewriting only the package's own `<version>` in the `.nuspec` to
  `{X.Y.Z}-preview.{yyyyMMddHHmm}.{run_number}.{run_attempt}` while leaving dependency
  `version="..."` attributes unchanged. `.github/workflows/release-publish.yml` is responsible
  only for public distribution to GitHub Packages and nuget.org.
  - **Reason**: Internal CI and closed-network users can validate the package before a person
    publishes the release and makes it available through public feeds.
  - **Impact**: Add a `push-azure-artifacts` job to `release-draft.yml`. It uses the existing
    `release` environment and has `id-token: write` for Azure workload identity federation. Keep
    Azure credentials separate from the packing job and push an Azure-only preview version while
    keeping the draft release asset and public feeds on the official tag version.
  - **Lifecycle and rerun rule**: Each Azure Artifacts push uses a fresh preview version, so a
    draft-workflow rerun no longer collides with a previously published Azure Artifacts version.
    The official version contract is unchanged for the draft release asset, GitHub Packages, and
    nuget.org.

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
    asset bytes when they match. Azure Artifacts therefore receives a preview-version repack built
    from the draft asset while its job keeps only `contents: read` and `id-token: write`.
  - **Operational note**: The handoff artifact contains only the `.nupkg`, logs no package
    contents, and is used only for job-to-job transfer. The public release gate remains manual
    publication of the draft release.
