# GitHub Actions CI Design: Release Workflow Guidance

This is the English counterpart for the release-workflow guidance in
[03-ci-github-actions.md](03-ci-github-actions.md). The Japanese document remains the complete
GitHub Actions CI design.

## 12a. NuGet Global Tool Release Workflow

Release `relaypublisher` with two workflows that are fully separate from the Intune publishing
workflow:

| Workflow | Trigger | Responsibility |
| --- | --- | --- |
| `.github/workflows/release-draft.yml` | Push of a `v*` tag | Build, test, pack, publish single-file apps, create a **draft** GitHub release with its assets, and repackage the selected `.nupkg` into a per-build preview-version package for Azure Artifacts internal testing. |
| `.github/workflows/release-publish.yml` | `release: [published]` | When a person publishes the draft release, push its reviewed package asset to GitHub Packages and nuget.org, and for stable releases open a pull request that updates the Homebrew tap formula. |

nuget.org does not permit deleting a published package version and can only unlist one. Therefore,
publishing a draft GitHub release remains the final human gate for public distribution. Azure
Artifacts is an internal-test feed, so after validating the tag the draft workflow repackages the
selected `.nupkg` by rewriting only its package `<version>` in the `.nuspec` to
`{X.Y.Z}-preview.{yyyyMMddHHmm}.{run_number}.{run_attempt}` before pushing it there.
Dependency `version="..."` attributes are left unchanged.

Each Azure Artifacts push therefore uses a fresh preview version, so rerunning the same tag no
longer collides with an earlier internal-feed version. The official tag version remains unchanged
on the draft release asset and in `.github/workflows/release-publish.yml`, which still pushes the
reviewed official-version package to GitHub Packages and nuget.org.

### Safe draft-workflow reruns

The workflow must not replace assets on an existing draft release. Before retaining the existing
expected package asset, download and expand both packages, then compare normalized package contents
and metadata with the newly produced package. NuGet ZIP entry timestamps can differ between builds
and are excluded from that comparison. If any actual package content or metadata differs, create a
new version tag. When the normalized contents match, the draft workflow uploads a short-lived
`release-package` workflow artifact containing the selected bytes: the freshly attached package for
a new draft, or the existing draft asset bytes for an existing draft.

The Azure Artifacts job must download that `release-package` workflow artifact by exact package
name, then rewrite only the package `<version>` in its `.nuspec` (leaving dependency version
attributes untouched) and push the rebuilt preview package without rebuilding from source. It keeps
only `contents: read` plus `id-token: write`; it must not download the draft release with its
read-only token and must not receive repository write permission alongside Azure credentials.

Never upload replacement assets to a published release. The `release: [published]` event does not
fire again when assets are replaced, which could otherwise leave the release asset inconsistent with
the packages already pushed to public feeds.

### Homebrew tap update job (`update-homebrew-tap`)

For stable releases, the `update-homebrew-tap` job in `release-publish.yml` opens a pull request
against the Homebrew tap (`kkamegawa/homebrew-tap`) that updates the formula. See `00-overview.md`
6.17 for the distribution design decisions.

- The `guard` job outputs `stable` (the version contains no `-`) and `mac-archive`
  (`relaypublisher-<version>-osx-arm64.zip`). The job skips prereleases with
  `if: needs.guard.outputs.stable == 'true'`.
- It is not chained to `push-packages` with `needs`. A tap failure never stops the NuGet push, and a
  feed failure never stops the tap update. Both use the same reviewed assets, so a partial success
  cannot leave them inconsistent.
- It checks that the release carries **exactly one** `mac-archive` and exactly one
  `SHA256SUMS.txt`, downloads both by exact name, computes the archive's actual hash, and fails if it
  does not match `SHA256SUMS.txt`.
- It renders the formula with `tools/New-HomebrewFormula.ps1`, which validates the version,
  repository, and sha256 formats and rejects prereleases. The repository-name check also keeps the
  value from breaking out of the Ruby string literals.
- It writes to the tap with a token minted by `actions/create-github-app-token` (pinned to a commit
  SHA), scoped to `repositories: homebrew-tap` with `permission-contents: write` and
  `permission-pull-requests: write`. `GITHUB_TOKEN` cannot write to another repository, and pull
  requests opened with it do not trigger the tap's workflows.
- Idempotency: it rebuilds branch `relaypublisher-<version>` from the tap's default branch on every
  run and force pushes it. If the default branch already pins that version or a newer one, it does
  nothing, so rerunning an older release, or publishing an older release after a newer one, never
  opens a downgrade pull request. If an open pull request for the branch exists, it only updates the
  branch.
- The job never merges the pull request. A person merges it after the tap CI (`brew test-bot`,
  `codesign --verify --strict` on the installed binary, and `relaypublisher --version`) passes.

The `release` environment also holds `HOMEBREW_TAP_APP_CLIENT_ID` and
`HOMEBREW_TAP_APP_PRIVATE_KEY`. Create the GitHub App with webhooks disabled and only these
repository permissions: Contents read and write, Pull requests read and write, and Metadata
read-only. Install it only on the `homebrew-tap` repository, and protect the tap's default branch so
the `brew test-bot` check is required.

The single-file apps carry only the .NET SDK's ad-hoc signature; they are not signed with a Developer
ID and not notarized. A zip downloaded directly from the GitHub
release triggers a Gatekeeper warning on macOS; installing through the Homebrew formula does not,
because Homebrew does not quarantine formula downloads.
